using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Agent.Configuration;
using Agent.Core;
using Agent.Models;
using Microsoft.Extensions.Logging;

namespace Agent.Communication;

/// <summary>Supplies the full inventory sent with the very first registration request. Implemented by the service host using the real collectors.</summary>
public interface IRegistrationInventoryProvider
{
    Task<RegistrationInventory> CollectAsync(CancellationToken ct);
    DeviceFingerprint GetFingerprint();
}

/// <summary>
/// Registration state machine: unregistered -> pending (waiting for administrator approval) -> approved (credential issued).
/// The device-specific credential is only issued by the server after approval and is stored with DPAPI.
/// Collection continues while pending so no data is lost; it is synchronized once approved.
/// </summary>
public sealed class RegistrationManager
{
    private const string StateKey = "registration.status";
    private readonly IApiClient _api;
    private readonly ICredentialStore _credentials;
    private readonly IStateStore _state;
    private readonly DeviceIdentityService _identity;
    private readonly IRegistrationInventoryProvider _inventory;
    private readonly RegistrationOptions _options;
    private readonly string _agentVersion;
    private readonly IClock _clock;
    private readonly ILogger _log;
    private int _unauthorizedCount;
    private bool _inventorySent;
    private volatile string _status;

    public RegistrationManager(IApiClient api, ICredentialStore credentials, IStateStore state, DeviceIdentityService identity,
        IRegistrationInventoryProvider inventory, RegistrationOptions options, string agentVersion, IClock clock, ILogger<RegistrationManager> log)
    {
        _api = api;
        _credentials = credentials;
        _state = state;
        _identity = identity;
        _inventory = inventory;
        _options = options;
        _agentVersion = agentVersion;
        _clock = clock;
        _log = log;
    }

    public string Status { get { return _status ?? RegistrationStates.Unregistered; } }
    public bool IsApproved { get { return Status == RegistrationStates.Approved; } }

    public void Initialize()
    {
        if (_identity.WasReset) _credentials.Delete();         // cloned image: never reuse another machine's credential
        _status = _credentials.Load() != null ? RegistrationStates.Approved : (_state.Get(StateKey) ?? RegistrationStates.Unregistered);
        if (_status == RegistrationStates.Approved && _credentials.Load() == null) _status = RegistrationStates.Unregistered;
        SetStatus(_status);
    }

    private void SetStatus(string s)
    {
        _status = s;
        _state.Set(StateKey, s);
    }

    /// <summary>Called by the sync engine when the API answers 401: after repeated failures the credential is discarded and registration restarts.</summary>
    public void OnUnauthorized()
    {
        if (Interlocked.Increment(ref _unauthorizedCount) < 3) return;
        _log.LogWarning("The API rejected the device credential repeatedly; re-registering");
        ResetCredential();
    }

    public void OnForbidden()
    {
        _log.LogWarning("The API reports this device is revoked or blocked");
        _credentials.Delete();
        _inventorySent = false;
        SetStatus(RegistrationStates.Rejected);
    }

    public void OnAuthenticated()
    {
        Interlocked.Exchange(ref _unauthorizedCount, 0);
    }

    private void ResetCredential()
    {
        _credentials.Delete();
        _inventorySent = false;
        Interlocked.Exchange(ref _unauthorizedCount, 0);
        SetStatus(RegistrationStates.Unregistered);
    }

    /// <summary>Runs until the device is approved or cancelled.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        int failures = 0;
        while (!ct.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                if (IsApproved)
                {
                    delay = TimeSpan.FromSeconds(30);          // watch for revocation / re-registration
                }
                else
                {
                    delay = await AttemptAsync(ct).ConfigureAwait(false);
                    failures = delay == TimeSpan.Zero ? 0 : failures;
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                failures++;
                _log.LogWarning(ex, "Registration attempt failed");
                delay = Backoff.Exponential(failures, TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(30));
            }
            try { await Task.Delay(delay == TimeSpan.Zero ? TimeSpan.FromSeconds(1) : delay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>One registration or approval-poll attempt. Returns how long to wait before the next one.</summary>
    public async Task<TimeSpan> AttemptAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.Token) && Status == RegistrationStates.Unregistered)
        {
            _log.LogError("No registration token is configured; the device cannot register. Re-run the installer with -RegistrationToken.");
            return TimeSpan.FromMinutes(10);
        }

        var request = new RegistrationRequest
        {
            DeviceId = _identity.DeviceId,
            RegistrationToken = _options.Token,
            AgentVersion = _agentVersion,
            Hostname = Environment.MachineName,
            Metadata = BuildMetadata(),
            Fingerprint = _inventory.GetFingerprint(),
            RequestedUtc = _clock.UtcNow
        };
        if (!_inventorySent) request.Inventory = await _inventory.CollectAsync(ct).ConfigureAwait(false);

        var result = await _api.RegisterAsync(request, ct).ConfigureAwait(false);
        switch (result.Outcome)
        {
            case ApiOutcome.Success:
                break;
            case ApiOutcome.RateLimited:
                return result.RetryAfter ?? TimeSpan.FromMinutes(2);
            case ApiOutcome.Unauthorized:
            case ApiOutcome.Forbidden:
                _log.LogError("Registration was refused ({Code}); check the registration token. {Error}", result.StatusCode, result.Error);
                return TimeSpan.FromMinutes(15);
            case ApiOutcome.Rejected:
                _log.LogError("Registration request was rejected by the API: {Error}", result.Error);
                return TimeSpan.FromMinutes(15);
            default:
                _log.LogWarning("Registration could not reach the API: {Error}", result.Error);
                return TimeSpan.FromSeconds(Math.Max(30, _options.PollSeconds));
        }

        var resp = result.Value;
        if (resp == null || string.IsNullOrEmpty(resp.Status))
        {
            _log.LogWarning("Registration response was empty or invalid");
            return TimeSpan.FromSeconds(Math.Max(30, _options.PollSeconds));
        }
        _inventorySent = true;
        var poll = TimeSpan.FromSeconds(Num.Clamp(resp.PollAfterSeconds ?? _options.PollSeconds, 10, 3600));

        switch (resp.Status.ToLowerInvariant())
        {
            case "approved":
                if (resp.Credential == null || string.IsNullOrEmpty(resp.Credential.Secret))
                {
                    _log.LogWarning("Registration was approved but no credential was issued");
                    return poll;
                }
                _credentials.Save(resp.Credential);
                _identity.MarkRegistered();
                Interlocked.Exchange(ref _unauthorizedCount, 0);
                SetStatus(RegistrationStates.Approved);
                _log.LogInformation("Device registration approved");
                return TimeSpan.Zero;
            case "rejected":
                SetStatus(RegistrationStates.Rejected);
                _log.LogWarning("Device registration was rejected by an administrator: {Message}", resp.Message);
                return TimeSpan.FromHours(1);
            default:
                if (Status != RegistrationStates.Pending) _log.LogInformation("Device registered; waiting for administrator approval");
                SetStatus(RegistrationStates.Pending);
                return poll;
        }
    }

    private Dictionary<string, string> BuildMetadata()
    {
        var md = new Dictionary<string, string>(_options.Metadata ?? new Dictionary<string, string>());
        if (!string.IsNullOrWhiteSpace(_options.Group)) md["group"] = _options.Group;
        return md;
    }
}
