using System;
using System.Collections.Generic;
using Agent.Models;

namespace Agent.Core;

/// <summary>
/// Effective configuration used by the scheduler, collectors and alert rules.
/// Layering: built-in defaults &lt; appsettings.json overrides &lt; signed remote configuration from the API.
/// </summary>
public class RuntimeConfig
{
    public const int MinIntervalSeconds = 5;
    public const int MaxIntervalSeconds = 7 * 24 * 3600;

    public long Version { get; set; }
    public Dictionary<string, int> IntervalsSeconds { get; set; }
    public Dictionary<string, bool> CollectorsEnabled { get; set; }
    public int SyncIntervalSeconds { get; set; }
    public int BatchSize { get; set; }
    public ThresholdConfig Thresholds { get; set; }
    public RetentionConfig Retention { get; set; }
    public SoftwarePolicy SoftwarePolicy { get; set; }
    public EventConfig Events { get; set; }
    public NetworkProbeConfig NetworkProbes { get; set; }
    public DiagnosticRequest Diagnostics { get; set; }

    public static RuntimeConfig CreateDefault()
    {
        return new RuntimeConfig
        {
            Version = 0,
            IntervalsSeconds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                { Categories.Heartbeat, 60 },
                { Categories.PerfCpuMemory, 30 },
                { Categories.PerfDisk, 30 },
                { Categories.PerfNetwork, 30 },
                { Categories.NetworkConfig, 300 },
                { Categories.Wifi, 120 },
                { Categories.Hardware, 24 * 3600 },
                { Categories.Device, 24 * 3600 },
                { Categories.Peripherals, 300 },
                { Categories.Storage, 3600 },
                { Categories.Software, 12 * 3600 },
                { Categories.Security, 1800 },
                { Categories.Events, 60 },
                { Categories.Sessions, 30 },
                { Categories.Battery, 300 },
                { Categories.Health, 300 },
                { Categories.Diagnostics, 60 }
            },
            CollectorsEnabled = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase),
            SyncIntervalSeconds = 15,
            BatchSize = 100,
            Thresholds = new ThresholdConfig(),
            Retention = new RetentionConfig(),
            SoftwarePolicy = new SoftwarePolicy(),
            Events = new EventConfig(),
            NetworkProbes = new NetworkProbeConfig(),
            Diagnostics = null
        };
    }

    public TimeSpan IntervalFor(string category)
    {
        int s;
        if (!IntervalsSeconds.TryGetValue(category, out s)) s = 300;
        return TimeSpan.FromSeconds(Num.Clamp(s, MinIntervalSeconds, MaxIntervalSeconds));
    }

    public bool IsEnabled(string category)
    {
        bool b;
        return !CollectorsEnabled.TryGetValue(category, out b) || b;
    }

    public RuntimeConfig Clone()
    {
        return new RuntimeConfig
        {
            Version = Version,
            IntervalsSeconds = new Dictionary<string, int>(IntervalsSeconds, StringComparer.OrdinalIgnoreCase),
            CollectorsEnabled = new Dictionary<string, bool>(CollectorsEnabled, StringComparer.OrdinalIgnoreCase),
            SyncIntervalSeconds = SyncIntervalSeconds,
            BatchSize = BatchSize,
            Thresholds = Thresholds,
            Retention = Retention,
            SoftwarePolicy = SoftwarePolicy,
            Events = Events,
            NetworkProbes = NetworkProbes,
            Diagnostics = Diagnostics
        };
    }

    /// <summary>Returns a new config with the (already signature-verified and validated) document applied.</summary>
    public RuntimeConfig Merge(AgentConfigDocument doc)
    {
        var c = Clone();
        c.Version = doc.Version;
        if (doc.IntervalsSeconds != null)
        {
            foreach (var kv in doc.IntervalsSeconds)
            {
                if (Array.IndexOf(Categories.AllCollectorCategories, kv.Key) < 0) continue;
                c.IntervalsSeconds[kv.Key] = Num.Clamp(kv.Value, MinIntervalSeconds, MaxIntervalSeconds);
            }
        }
        if (doc.CollectorsEnabled != null)
        {
            foreach (var kv in doc.CollectorsEnabled)
            {
                if (Array.IndexOf(Categories.AllCollectorCategories, kv.Key) < 0) continue;
                if (kv.Key == Categories.Heartbeat && !kv.Value) continue;     // heartbeat can never be disabled remotely
                c.CollectorsEnabled[kv.Key] = kv.Value;
            }
        }
        if (doc.SyncIntervalSeconds.HasValue) c.SyncIntervalSeconds = Num.Clamp(doc.SyncIntervalSeconds.Value, 5, 3600);
        if (doc.BatchSize.HasValue) c.BatchSize = Num.Clamp(doc.BatchSize.Value, 1, 500);
        if (doc.Thresholds != null) c.Thresholds = doc.Thresholds;
        if (doc.Retention != null) c.Retention = doc.Retention;
        if (doc.SoftwarePolicy != null) c.SoftwarePolicy = doc.SoftwarePolicy;
        if (doc.Events != null) c.Events = doc.Events;
        if (doc.NetworkProbes != null) c.NetworkProbes = doc.NetworkProbes;
        c.Diagnostics = doc.Diagnostics;
        return c;
    }
}

public static class SyncPriority
{
    /// <summary>Lower number = sent first, pruned last.</summary>
    public static int For(string module)
    {
        switch (module)
        {
            case Modules.Alerts: return 1;
            case Modules.Events:
            case Modules.Changes: return 2;
            case Modules.Security:
            case Modules.Device:
            case Modules.Hardware:
            case Modules.Storage: return 3;
            case Modules.Software:
            case Modules.Network:
            case Modules.Health:
            case Modules.Diagnostics: return 4;
            case Modules.Heartbeat: return 5;
            case Modules.Performance: return 6;
            default: return 4;
        }
    }
}
