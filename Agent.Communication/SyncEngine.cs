using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Agent.Configuration;
using Agent.Core;
using Agent.Models;
using Microsoft.Extensions.Logging;

namespace Agent.Communication;

public enum DrainOutcome { Idle, Completed, Failed, Blocked }

/// <summary>
/// Drains the SQLite outbox to the API. A record is only marked synced after the API acknowledged it, so a crash, network loss or
/// server outage can never lose data. Retries are safe because every record carries a deterministic idempotency key.
/// </summary>
public sealed class SyncEngine
{
    public const string LastSyncKey = "sync.last_success_utc";

    private readonly IOutbox _outbox;
    private readonly IApiClient _api;
    private readonly RegistrationManager _registration;
    private readonly IStateStore _state;
    private readonly IConfigProvider _config;
    private readonly RemoteConfigProvider _remoteConfig;
    private readonly IUpdateHandler _updates;
    private readonly IClock _clock;
    private readonly Func<string> _deviceId;
    private readonly ILogger _log;
    private DateTime _lastConfigPullUtc = DateTime.MinValue;
    private DateTime _lastPruneUtc = DateTime.MinValue;
    private readonly TimeSpan _maxRetryDelay;
    private int _failures;
    private TimeSpan _blockedFor = TimeSpan.Zero;

    public SyncEngine(IOutbox outbox, IApiClient api, RegistrationManager registration, IStateStore state, IConfigProvider config,
        RemoteConfigProvider remoteConfig, IUpdateHandler updates, IClock clock, Func<string> deviceId, int maxRetryDelaySeconds,
        ILogger<SyncEngine> log)
    {
        _outbox = outbox;
        _api = api;
        _registration = registration;
        _state = state;
        _config = config;
        _remoteConfig = remoteConfig;
        _updates = updates;
        _clock = clock;
        _deviceId = deviceId;
        _maxRetryDelay = TimeSpan.FromSeconds(Math.Max(60, maxRetryDelaySeconds));
        _log = log;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var delay = TimeSpan.FromSeconds(_config.Current.SyncIntervalSeconds);
            try
            {
                if (_registration.IsApproved)
                {
                    var outcome = await DrainAsync(ct).ConfigureAwait(false);
                    if (outcome == DrainOutcome.Failed)
                    {
                        _failures++;
                        delay = Max(delay, Backoff.Exponential(_failures - 1, TimeSpan.FromSeconds(10), _maxRetryDelay));
                    }
                    else if (outcome == DrainOutcome.Blocked) delay = Max(delay, _blockedFor);
                    else _failures = 0;
                }
                PruneIfDue();
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _failures++;
                _log.LogError(ex, "Sync cycle failed unexpectedly");
                delay = Max(delay, Backoff.Exponential(_failures - 1, TimeSpan.FromSeconds(10), _maxRetryDelay));
            }
            try { await Task.Delay(delay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b)
    {
        return a > b ? a : b;
    }

    private void PruneIfDue()
    {
        var now = _clock.UtcNow;
        if (now - _lastPruneUtc < TimeSpan.FromHours(1)) return;
        _lastPruneUtc = now;
        try
        {
            var removed = _outbox.Prune(_config.Current.Retention, now);
            if (removed > 0) _log.LogInformation("Retention removed {Count} old queue records", removed);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Retention pruning failed"); }
    }

    /// <summary>Sends everything that is ready. Stops at the first sign the server is unavailable.</summary>
    public async Task<DrainOutcome> DrainAsync(CancellationToken ct)
    {
        var started = _clock.UtcNow;
        bool sentAny = false;
        int guard = 0;
        while (guard++ < 2000 && !ct.IsCancellationRequested)
        {
            var modules = _outbox.ModulesWithReadyRecords(_clock.UtcNow);
            if (modules.Count == 0) break;
            bool progressed = false;
            foreach (var module in modules)
            {
                var batch = _outbox.Dequeue(module, _config.Current.BatchSize, _clock.UtcNow);
                if (batch.Count == 0) continue;
                var outcome = await SendBatchAsync(module, batch, ct).ConfigureAwait(false);
                if (outcome == DrainOutcome.Failed || outcome == DrainOutcome.Blocked) return outcome;
                sentAny = true;
                progressed = true;
            }
            if (!progressed) break;
            if (_clock.UtcNow - started > TimeSpan.FromMinutes(2)) break;      // yield so config/heartbeat stay responsive
        }
        return sentAny ? DrainOutcome.Completed : DrainOutcome.Idle;
    }

    private async Task<DrainOutcome> SendBatchAsync(string module, IReadOnlyList<OutboxRecord> batch, CancellationToken ct)
    {
        var body = BuildBody(module, batch);
        var batchKey = Hashing.Short(string.Join(",", batch.Select(b => b.IdempotencyKey)), 32);
        var result = await _api.SendBatchAsync(_deviceId(), module, body, batchKey, ct).ConfigureAwait(false);
        var ids = batch.Select(b => b.Id).ToList();
        var now = _clock.UtcNow;

        switch (result.Outcome)
        {
            case ApiOutcome.Success:
                _registration.OnAuthenticated();
                await HandleAcknowledgementAsync(batch, result.Value, ct).ConfigureAwait(false);
                _state.Set(LastSyncKey, now.ToString("o", CultureInfo.InvariantCulture));
                return DrainOutcome.Completed;

            case ApiOutcome.RateLimited:
                {
                    var wait = result.RetryAfter ?? TimeSpan.FromSeconds(60);
                    if (wait < TimeSpan.FromSeconds(5)) wait = TimeSpan.FromSeconds(5);
                    _outbox.MarkRetry(ids, now + wait, "rate limited", false);
                    _blockedFor = wait;
                    _log.LogInformation("API rate limit reached; pausing sync for {Seconds:0} s", wait.TotalSeconds);
                    return DrainOutcome.Blocked;
                }

            case ApiOutcome.Unauthorized:
                _outbox.MarkRetry(ids, now + TimeSpan.FromSeconds(30), "unauthorized", false);
                _registration.OnUnauthorized();
                _blockedFor = TimeSpan.FromSeconds(30);
                return DrainOutcome.Blocked;

            case ApiOutcome.Forbidden:
                _outbox.MarkRetry(ids, now + TimeSpan.FromMinutes(10), "forbidden", false);
                _registration.OnForbidden();
                _blockedFor = TimeSpan.FromMinutes(10);
                return DrainOutcome.Blocked;

            case ApiOutcome.Rejected:
                return await SplitAndRetryAsync(module, batch, result, ct).ConfigureAwait(false);

            case ApiOutcome.NetworkError:
                // offline is not the record's fault: do not count the attempt, retry with backoff
                _outbox.MarkRetry(ids, now + Backoff.Exponential(_failures, TimeSpan.FromSeconds(10), _maxRetryDelay), result.Error, false);
                return DrainOutcome.Failed;

            default:    // ServerError, InvalidResponse
                {
                    var maxAttempts = batch.Max(b => b.Attempts);
                    _outbox.MarkRetry(ids, now + Backoff.Exponential(maxAttempts, TimeSpan.FromSeconds(15), _maxRetryDelay),
                        result.Error ?? ("HTTP " + result.StatusCode), true);
                    _log.LogWarning("API returned {Status} for module {Module}: {Error}", result.StatusCode, module, result.Error);
                    return DrainOutcome.Failed;
                }
        }
    }

    /// <summary>A 4xx on a multi-record batch means at least one record is invalid: send them one by one so good records still get through.</summary>
    private async Task<DrainOutcome> SplitAndRetryAsync(string module, IReadOnlyList<OutboxRecord> batch, ApiResult<IngestResponse> first, CancellationToken ct)
    {
        if (batch.Count == 1)
        {
            _outbox.MarkDead(new[] { batch[0].Id }, "HTTP " + first.StatusCode + ": " + first.Error);
            _log.LogWarning("Record {Key} ({Module}) was rejected by the API and moved to the dead-letter queue: {Error}", batch[0].IdempotencyKey, module, first.Error);
            return DrainOutcome.Completed;
        }
        foreach (var rec in batch)
        {
            var single = new[] { rec };
            var r = await _api.SendBatchAsync(_deviceId(), module, BuildBody(module, single), rec.IdempotencyKey, ct).ConfigureAwait(false);
            var now = _clock.UtcNow;
            if (r.Outcome == ApiOutcome.Success)
            {
                await HandleAcknowledgementAsync(single, r.Value, ct).ConfigureAwait(false);
            }
            else if (r.Outcome == ApiOutcome.Rejected)
            {
                _outbox.MarkDead(new[] { rec.Id }, "HTTP " + r.StatusCode + ": " + r.Error);
            }
            else
            {
                _outbox.MarkRetry(new[] { rec.Id }, now + Backoff.Exponential(rec.Attempts, TimeSpan.FromSeconds(15), _maxRetryDelay), r.Error, r.Outcome != ApiOutcome.NetworkError);
                return r.Outcome == ApiOutcome.RateLimited ? DrainOutcome.Blocked : DrainOutcome.Failed;
            }
        }
        return DrainOutcome.Completed;
    }

    private async Task HandleAcknowledgementAsync(IReadOnlyList<OutboxRecord> batch, IngestResponse response, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var rejected = new Dictionary<string, RejectedRecord>();
        if (response != null && response.Rejected != null)
            foreach (var r in response.Rejected)
                if (!string.IsNullOrEmpty(r.Id)) rejected[r.Id] = r;

        var synced = new List<long>();
        var dead = new List<long>();
        var retry = new List<long>();
        foreach (var rec in batch)
        {
            RejectedRecord rr;
            if (!rejected.TryGetValue(rec.IdempotencyKey, out rr)) synced.Add(rec.Id);       // not listed as rejected = accepted
            else if (rr.Retryable == true) retry.Add(rec.Id);
            else
            {
                dead.Add(rec.Id);
                _log.LogWarning("API rejected record {Key}: {Code} {Message}", rec.IdempotencyKey, rr.Code, rr.Message);
            }
        }
        if (synced.Count > 0) _outbox.MarkSynced(synced, now);
        if (dead.Count > 0) _outbox.MarkDead(dead, "rejected by API");
        if (retry.Count > 0) _outbox.MarkRetry(retry, now + TimeSpan.FromSeconds(60), "server asked to retry", true);

        if (response == null) return;
        if (response.ConfigVersion.HasValue && response.ConfigVersion.Value > _config.Current.Version) await PullConfigAsync(ct).ConfigureAwait(false);
        if (response.Update != null && response.Update.Manifest != null)
        {
            try { await _updates.OfferAsync(response.Update, ct).ConfigureAwait(false); }
            catch (Exception ex) { _log.LogWarning(ex, "Update offer could not be processed"); }
        }
    }

    public async Task PullConfigAsync(CancellationToken ct)
    {
        if (_clock.UtcNow - _lastConfigPullUtc < TimeSpan.FromSeconds(60)) return;
        _lastConfigPullUtc = _clock.UtcNow;
        var r = await _api.GetConfigAsync(_deviceId(), ct).ConfigureAwait(false);
        if (!r.IsSuccess || r.Value == null)
        {
            _log.LogWarning("Could not download remote configuration: {Error}", r.Error);
            return;
        }
        _remoteConfig.Apply(r.Value);
    }

    /// <summary>Wire format for one module batch. The payload JSON from the outbox is embedded as-is (no re-serialization).</summary>
    public string BuildBody(string module, IReadOnlyList<OutboxRecord> batch)
    {
        var sb = new StringBuilder(batch.Sum(b => b.PayloadJson.Length) + 256 + batch.Count * 160);
        sb.Append("{\"schemaVersion\":").Append(Json.Serialize(SchemaInfo.SchemaVersion));
        sb.Append(",\"deviceId\":").Append(Json.Serialize(_deviceId()));
        sb.Append(",\"module\":").Append(Json.Serialize(module));
        sb.Append(",\"sentUtc\":").Append(Json.Serialize(_clock.UtcNow.ToString("o", CultureInfo.InvariantCulture)));
        sb.Append(",\"records\":[");
        for (int i = 0; i < batch.Count; i++)
        {
            var b = batch[i];
            if (i > 0) sb.Append(',');
            sb.Append("{\"id\":").Append(Json.Serialize(b.IdempotencyKey));
            sb.Append(",\"category\":").Append(Json.Serialize(b.Category));
            sb.Append(",\"collectedAtUtc\":").Append(Json.Serialize(b.CollectedAtUtc.ToString("o", CultureInfo.InvariantCulture)));
            sb.Append(",\"payload\":").Append(b.PayloadJson).Append('}');
        }
        sb.Append("]}");
        return sb.ToString();
    }
}
