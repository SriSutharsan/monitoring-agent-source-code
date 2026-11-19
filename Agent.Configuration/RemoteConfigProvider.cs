using System;
using System.IO;
using System.Text;
using Agent.Core;
using Agent.Models;
using Microsoft.Extensions.Logging;

namespace Agent.Configuration;

public enum ConfigApplyResult { Applied, Stale, Rejected }

/// <summary>
/// Effective runtime configuration: defaults, then local appsettings overrides, then the latest verified remote config.
/// Remote config is only ever applied after its signature, expiry, device binding and version monotonicity are verified,
/// and the last good document is cached on disk so the agent starts with it while offline.
/// </summary>
public sealed class RemoteConfigProvider : IConfigProvider
{
    private readonly AgentOptions _options;
    private readonly SignedEnvelopeVerifier _verifier;
    private readonly string _cachePath;
    private readonly Func<string> _deviceId;
    private readonly IClock _clock;
    private readonly ILogger _log;
    private readonly object _gate = new object();
    private volatile RuntimeConfig _current;
    private readonly RuntimeConfig _local;

    public event Action<RuntimeConfig> Changed;
    public RuntimeConfig Current { get { return _current; } }

    public RemoteConfigProvider(AgentOptions options, SignedEnvelopeVerifier verifier, string cachePath, Func<string> deviceId,
        IClock clock, ILogger<RemoteConfigProvider> log)
    {
        _options = options;
        _verifier = verifier;
        _cachePath = cachePath;
        _deviceId = deviceId;
        _clock = clock;
        _log = log;
        _local = BuildLocal(options);
        _current = _local;
    }

    private static RuntimeConfig BuildLocal(AgentOptions o)
    {
        var c = RuntimeConfig.CreateDefault();
        var col = o.Collection;
        foreach (var kv in col.IntervalsSeconds)
            c.IntervalsSeconds[kv.Key] = Num.Clamp(kv.Value, RuntimeConfig.MinIntervalSeconds, RuntimeConfig.MaxIntervalSeconds);
        foreach (var kv in col.CollectorsEnabled) c.CollectorsEnabled[kv.Key] = kv.Value;
        c.SyncIntervalSeconds = Num.Clamp(col.SyncIntervalSeconds, 5, 3600);
        c.BatchSize = Num.Clamp(col.BatchSize, 1, 500);
        c.Retention.SyncedRetentionDays = Math.Max(1, col.SyncedRetentionDays);
        c.Events.CollectSuccessfulLogons = col.CollectSuccessfulLogons;
        if (col.PingTargets != null && col.PingTargets.Count > 0) c.NetworkProbes.PingTargets = col.PingTargets;
        return c;
    }

    /// <summary>Loads the cached remote config, if any. Called once at start-up.</summary>
    public void LoadCached()
    {
        try
        {
            if (!File.Exists(_cachePath)) return;
            SignedEnvelope env;
            if (!Json.TryDeserialize(File.ReadAllText(_cachePath, Encoding.UTF8), out env)) return;
            Apply(env, persist: false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Cached configuration could not be loaded; using local settings");
        }
    }

    public ConfigApplyResult Apply(SignedEnvelope envelope, bool persist = true)
    {
        lock (_gate)
        {
            AgentConfigDocument doc;
            var result = _verifier.VerifyConfig(envelope, _deviceId(), _clock.UtcNow, out doc);
            if (result != VerifyResult.Valid)
            {
                _log.LogWarning("Remote configuration rejected: {Reason}", result);
                return ConfigApplyResult.Rejected;
            }
            // Expired cached documents are rejected above, so a stale-but-valid-version check is all that is left.
            if (doc.Version <= _current.Version && _current.Version > 0)
                return ConfigApplyResult.Stale;

            _current = _local.Merge(doc);
            if (persist)
            {
                try { AtomicFile.WriteAllText(_cachePath, Json.Serialize(envelope)); }
                catch (Exception ex) { _log.LogWarning(ex, "Could not cache remote configuration"); }
            }
            _log.LogInformation("Applied remote configuration version {Version}", doc.Version);
            var handler = Changed;
            if (handler != null) handler(_current);
            return ConfigApplyResult.Applied;
        }
    }
}
