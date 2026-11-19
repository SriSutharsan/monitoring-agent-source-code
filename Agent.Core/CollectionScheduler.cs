using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Agent.Models;
using Microsoft.Extensions.Logging;

namespace Agent.Core;

/// <summary>Replaceable "something changed" signal used to wake sleeping collector loops.</summary>
internal sealed class ChangeSignal
{
    private TaskCompletionSource<bool> _tcs = NewTcs();

    private static TaskCompletionSource<bool> NewTcs()
    {
        return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public Task Current { get { return Volatile.Read(ref _tcs).Task; } }

    public void Signal()
    {
        var old = Interlocked.Exchange(ref _tcs, NewTcs());
        old.TrySetResult(true);
    }
}

/// <summary>
/// Runs every enabled collector on its own interval. Failures are isolated per collector and backed off exponentially;
/// heavy (WMI) collectors share a small concurrency gate so the agent never spikes CPU.
/// </summary>
public sealed class CollectionScheduler : ICollectorStatusSource
{
    private readonly IReadOnlyList<ICollector> _collectors;
    private readonly IConfigProvider _config;
    private readonly ICollectionSink _sink;
    private readonly IClock _clock;
    private readonly Func<string> _deviceId;
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<string, CollectorStatus> _status = new ConcurrentDictionary<string, CollectorStatus>();
    private readonly ChangeSignal _changed = new ChangeSignal();
    private readonly SemaphoreSlim _gate;
    private readonly DateTime _startedUtc;

    public CollectionScheduler(IEnumerable<ICollector> collectors, IConfigProvider config, ICollectionSink sink, IClock clock,
        Func<string> deviceId, ILogger<CollectionScheduler> log, int maxConcurrentCollections = 2)
    {
        _collectors = collectors.ToList();
        _config = config;
        _sink = sink;
        _clock = clock;
        _deviceId = deviceId;
        _log = log;
        _gate = new SemaphoreSlim(Math.Max(1, maxConcurrentCollections));
        _startedUtc = clock.UtcNow;
        _config.Changed += delegate { _changed.Signal(); };
        foreach (var c in _collectors)
            _status[c.Category] = new CollectorStatus { Category = c.Category, Enabled = true };
    }

    public IReadOnlyList<CollectorStatus> Snapshot()
    {
        return _status.Values.OrderBy(s => s.Category).Select(s => new CollectorStatus
        {
            Category = s.Category,
            LastSuccessUtc = s.LastSuccessUtc,
            LastAttemptUtc = s.LastAttemptUtc,
            ConsecutiveFailures = s.ConsecutiveFailures,
            LastError = s.LastError,
            LastDurationMs = s.LastDurationMs,
            Enabled = s.Enabled
        }).ToList();
    }

    public Task RunAsync(CancellationToken ct)
    {
        var loops = new List<Task>();
        int index = 0;
        foreach (var collector in _collectors)
        {
            // Heartbeat starts immediately; everything else is staggered so start-up does not stampede WMI.
            var delay = collector.Category == Categories.Heartbeat
                ? TimeSpan.Zero
                : TimeSpan.FromMilliseconds(1500 * (++index) + Backoff.Next(1500));
            var c = collector;
            loops.Add(Task.Run(() => LoopAsync(c, delay, ct), ct));
        }
        return Task.WhenAll(loops);
    }

    private async Task LoopAsync(ICollector collector, TimeSpan startDelay, CancellationToken ct)
    {
        var category = collector.Category;
        try
        {
            if (startDelay > TimeSpan.Zero) await Task.Delay(startDelay, ct).ConfigureAwait(false);
            DateTime? lastRun = null;
            double jitterSeconds = 0;

            while (!ct.IsCancellationRequested)
            {
                var signal = _changed.Current;
                var cfg = _config.Current;
                var status = _status[category];
                status.Enabled = cfg.IsEnabled(category);

                if (!status.Enabled)
                {
                    await WaitAsync(TimeSpan.FromSeconds(30), signal, ct).ConfigureAwait(false);
                    continue;
                }

                var interval = cfg.IntervalFor(category);
                var effective = status.ConsecutiveFailures > 0
                    ? Backoff.Exponential(status.ConsecutiveFailures - 1, interval, TimeSpan.FromMinutes(Math.Max(15, interval.TotalMinutes)))
                    : interval;
                var due = (lastRun ?? DateTime.MinValue) + effective + TimeSpan.FromSeconds(jitterSeconds);
                var wait = due - _clock.UtcNow;
                if (lastRun.HasValue && wait > TimeSpan.Zero)
                {
                    // sleep in slices so clock changes / config changes are noticed promptly
                    await WaitAsync(wait > TimeSpan.FromSeconds(60) ? TimeSpan.FromSeconds(60) : wait, signal, ct).ConfigureAwait(false);
                    continue;
                }

                await RunOnceAsync(collector, cfg, ct).ConfigureAwait(false);
                lastRun = _clock.UtcNow;
                jitterSeconds = interval.TotalSeconds * 0.05 * Backoff.NextDouble();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogCritical(ex, "Collector loop {Category} terminated unexpectedly", category);
        }
    }

    private static async Task WaitAsync(TimeSpan delay, Task signal, CancellationToken ct)
    {
        if (delay <= TimeSpan.Zero) return;
        await Task.WhenAny(Task.Delay(delay, ct), signal).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
    }

    private async Task RunOnceAsync(ICollector collector, RuntimeConfig cfg, CancellationToken ct)
    {
        var category = collector.Category;
        var status = _status[category];
        var sw = Stopwatch.StartNew();
        bool useGate = category != Categories.Heartbeat;
        if (useGate) await _gate.WaitAsync(ct).ConfigureAwait(false);
        status.LastAttemptUtc = _clock.UtcNow;
        try
        {
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(collector.Timeout);
                var ctx = new CollectionContext { DeviceId = _deviceId(), NowUtc = _clock.UtcNow, Config = cfg };
                object payload;
                try
                {
                    payload = await collector.CollectAsync(ctx, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException("collector exceeded " + collector.Timeout.TotalSeconds.ToString("0") + "s");
                }

                if (payload != null) await _sink.HandleAsync(category, payload, ct).ConfigureAwait(false);
                status.ConsecutiveFailures = 0;
                status.LastError = null;
                status.LastSuccessUtc = _clock.UtcNow;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            status.ConsecutiveFailures++;
            status.LastError = Text.Truncate(ex.GetType().Name + ": " + ex.Message, 300);
            if (status.ConsecutiveFailures <= 3 || status.ConsecutiveFailures % 20 == 0)
                _log.LogWarning(ex, "Collector {Category} failed (consecutive failures: {Failures})", category, status.ConsecutiveFailures);
        }
        finally
        {
            sw.Stop();
            status.LastDurationMs = sw.ElapsedMilliseconds;
            if (useGate) _gate.Release();
        }
    }
}
