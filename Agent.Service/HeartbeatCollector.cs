using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Agent.Communication;
using Agent.Core;
using Agent.Models;

namespace Agent.Service;

/// <summary>
/// Liveness + self-health record. It lives in the service project because it needs the outbox, scheduler status and clock-skew
/// tracker. A heartbeat is queued like any other record, so gaps in connectivity are visible afterwards from the timestamps.
/// </summary>
public sealed class HeartbeatCollector : CollectorBase<Heartbeat>
{
    private readonly IOutbox _outbox;
    private readonly IStateStore _state;
    private readonly IConfigProvider _config;
    private readonly INativeSystem _native;
    private readonly MetricsCache _cache;
    private readonly ClockSkewTracker _skew;
    private readonly Func<ICollectorStatusSource> _status;
    private readonly IClock _clock;
    private readonly string _agentVersion;
    private readonly DateTime _startedUtc;
    private TimeSpan _lastCpu;
    private DateTime _lastCpuAt;

    public HeartbeatCollector(IOutbox outbox, IStateStore state, IConfigProvider config, INativeSystem native, MetricsCache cache,
        ClockSkewTracker skew, Func<ICollectorStatusSource> status, IClock clock, string agentVersion)
    {
        _outbox = outbox;
        _state = state;
        _config = config;
        _native = native;
        _cache = cache;
        _skew = skew;
        _status = status;
        _clock = clock;
        _agentVersion = agentVersion;
        _startedUtc = clock.UtcNow;
        _lastCpuAt = clock.UtcNow;
        try { _lastCpu = Process.GetCurrentProcess().TotalProcessorTime; } catch (Exception) { }
    }

    public override string Category { get { return Categories.Heartbeat; } }
    public override TimeSpan Timeout { get { return TimeSpan.FromSeconds(15); } }

    public override Task<Heartbeat> CollectTypedAsync(CollectionContext ctx, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var stats = _outbox.GetStats();
        var collectors = _status().Snapshot().ToList();
        var failing = collectors.Count(c => c.Enabled && c.ConsecutiveFailures >= 3);

        DateTime lastSync;
        DateTime? lastSyncUtc = DateTime.TryParse(_state.Get(SyncEngine.LastSyncKey), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out lastSync) ? lastSync : (DateTime?)null;

        var net = _cache.Get<NetworkInfo>(Categories.NetworkConfig);
        var device = _cache.Get<DeviceInfo>(Categories.Device);

        double? cpu = null; long? ws = null;
        try
        {
            var p = Process.GetCurrentProcess();
            var total = p.TotalProcessorTime;
            var wall = (now - _lastCpuAt).TotalSeconds;
            if (wall > 1) { cpu = Math.Round(Math.Max(0, (total - _lastCpu).TotalSeconds / wall / Environment.ProcessorCount * 100.0), 2); _lastCpu = total; _lastCpuAt = now; }
            ws = p.WorkingSet64;
        }
        catch (Exception) { }

        var hb = new Heartbeat
        {
            DeviceId = ctx.DeviceId,
            Hostname = Environment.MachineName,
            AgentVersion = _agentVersion,
            Status = failing > 0 || stats.Dead > 0 ? "degraded" : "ok",
            AgentUptimeSeconds = (long)(now - _startedUtc).TotalSeconds,
            SystemUptimeSeconds = _native.TickCount64 / 1000,
            ConfigVersion = _config.Current.Version,
            LastSuccessfulSyncUtc = lastSyncUtc,
            ClockSkewMs = _skew.SkewMs,
            PendingRecords = stats.Pending,
            DeadLetterRecords = stats.Dead,
            PrimaryIPv4 = net != null ? net.PrimaryIPv4 : null,
            OsVersion = device != null ? device.OsVersion : Environment.OSVersion.Version.ToString(),
            Elevated = _native.IsElevated(),
            AgentCpuPercent = cpu,
            AgentWorkingSetBytes = ws,
            Collectors = collectors
        };
        return Task.FromResult(hb);
    }
}
