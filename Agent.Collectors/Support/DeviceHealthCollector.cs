using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Agent.Core;
using Agent.Models;

namespace Agent.Collectors.Support;

/// <summary>
/// Rolls the latest data from every other collector plus local history into one 0-100 health score with explanations,
/// recurring-problem detection and a short maintenance history. Everything is derived from data the agent already collected.
/// </summary>
public sealed class DeviceHealthCollector : CollectorBase<DeviceHealth>
{
    private readonly MetricsCache _cache;
    private readonly IHistoryStore _history;
    private readonly IClock _clock;

    public DeviceHealthCollector(MetricsCache cache, IHistoryStore history, IClock clock)
    {
        _cache = cache;
        _history = history;
        _clock = clock;
    }

    public override string Category { get { return Categories.Health; } }

    public override Task<DeviceHealth> CollectTypedAsync(CollectionContext ctx, CancellationToken ct)
    {
        return Task.FromResult(Collect(ctx.Config.Thresholds ?? new ThresholdConfig()));
    }

    public DeviceHealth Collect(ThresholdConfig t)
    {
        var now = _clock.UtcNow;
        var factors = new List<HealthFactor>();
        Action<string, string, string, int> add = (name, status, detail, penalty) =>
            factors.Add(new HealthFactor { Name = name, Status = status, Detail = detail, Penalty = penalty });

        // storage
        var storage = _cache.Get<StorageInventory>(Categories.Storage);
        if (storage == null) add("storage", "unknown", "no storage data yet", 0);
        else
        {
            var worst = (storage.Volumes ?? new List<VolumeInfo>()).Where(v => v.DriveType == "Fixed" && v.UsedPercent.HasValue).OrderByDescending(v => v.UsedPercent).FirstOrDefault();
            if (worst == null) add("storage", "unknown", "no fixed volumes reported", 0);
            else if (worst.UsedPercent >= t.StorageCriticalPercent) add("storage", "critical", worst.DriveLetter + " is " + worst.UsedPercent + "% full", 20);
            else if (worst.UsedPercent >= t.StorageWarningPercent) add("storage", "warning", worst.DriveLetter + " is " + worst.UsedPercent + "% full", 10);
            else add("storage", "ok", "highest usage " + worst.UsedPercent + "% (" + worst.DriveLetter + ")", 0);

            var disks = storage.Disks ?? new List<PhysicalDiskInfo>();
            var bad = disks.FirstOrDefault(d => d.HealthStatus == "Unhealthy" || d.SmartPredictFailure == true);
            var warn = disks.FirstOrDefault(d => d.HealthStatus == "Warning");
            if (bad != null) add("disk_health", "critical", bad.Model + " reports failure", 25);
            else if (warn != null) add("disk_health", "warning", warn.Model + " reports a warning", 10);
            else if (disks.Count > 0) add("disk_health", "ok", "all disks healthy", 0);
        }

        // memory / cpu
        var cpuMem = _cache.Get<PerformanceMetrics>(Categories.PerfCpuMemory);
        if (cpuMem != null && cpuMem.Memory != null)
        {
            if (cpuMem.Memory.UsedPercent >= t.RamPercent) add("memory", "warning", "RAM " + cpuMem.Memory.UsedPercent + "% used", 10);
            else add("memory", "ok", "RAM " + cpuMem.Memory.UsedPercent + "% used", 0);
        }
        if (cpuMem != null && cpuMem.Cpu != null)
        {
            if (cpuMem.Cpu.UsagePercent >= t.CpuPercent) add("cpu", "warning", "CPU " + cpuMem.Cpu.UsagePercent + "%", 5);
            else add("cpu", "ok", "CPU " + cpuMem.Cpu.UsagePercent + "%", 0);
        }
        if (cpuMem != null && cpuMem.UptimeSeconds.HasValue && cpuMem.UptimeSeconds.Value > 30L * 86400)
            add("uptime", "warning", "not restarted for " + cpuMem.UptimeSeconds.Value / 86400 + " days", 5);

        // security
        var sec = _cache.Get<SecurityStatus>(Categories.Security);
        bool pendingRestart = false;
        if (sec == null) add("security", "unknown", "no security data yet", 0);
        else
        {
            var av = sec.Antivirus;
            bool avKnown = av != null && ((av.Products != null && av.Products.Count > 0) || (av.Defender != null && av.Defender.Present));
            bool avOn = av != null && ((av.Products != null && av.Products.Any(p => p.Enabled == true)) || (av.Defender != null && av.Defender.AntivirusEnabled == true));
            if (!avKnown) add("antivirus", "warning", "no antivirus product detected", 15);
            else if (!avOn) add("antivirus", "critical", "antivirus is disabled", 20);
            else add("antivirus", "ok", "antivirus enabled", 0);

            if (sec.Firewall != null && (sec.Firewall.PublicProfileEnabled == false || sec.Firewall.PrivateProfileEnabled == false || sec.Firewall.DomainProfileEnabled == false))
                add("firewall", "warning", "firewall disabled on at least one profile", 10);
            else if (sec.Firewall != null) add("firewall", "ok", "firewall enabled", 0);

            if (sec.WindowsUpdate != null && sec.WindowsUpdate.DaysSinceLastSuccessfulInstall > 60)
                add("windows_update", "warning", "no updates installed for " + sec.WindowsUpdate.DaysSinceLastSuccessfulInstall + " days", 10);
            else if (sec.WindowsUpdate != null) add("windows_update", "ok", "updates current", 0);

            pendingRestart = sec.PendingRestart != null && sec.PendingRestart.Pending;
            if (pendingRestart) add("pending_restart", "warning", "restart pending: " + string.Join(", ", sec.PendingRestart.Reasons ?? new List<string>()), 5);
        }

        // battery
        var battery = _cache.Get<PerformanceMetrics>(Categories.Battery);
        if (battery != null && battery.Battery != null && battery.Battery.Present && battery.Battery.HealthPercent.HasValue)
        {
            if (battery.Battery.HealthPercent.Value < t.BatteryHealthLowPercent) add("battery", "warning", "battery holds " + battery.Battery.HealthPercent + "% of design capacity", 5);
            else add("battery", "ok", "battery health " + battery.Battery.HealthPercent + "%", 0);
        }

        // stability history
        var since30 = now.AddDays(-30);
        int shutdowns = SafeCount("unexpected_shutdown", since30);
        int bugchecks = SafeCount("bsod", since30);
        if (bugchecks >= 3) add("stability", "critical", bugchecks + " blue screens in 30 days", 25);
        else if (bugchecks >= 1) add("stability", "warning", bugchecks + " blue screen(s) in 30 days", 15);
        else if (shutdowns >= 3) add("stability", "warning", shutdowns + " unexpected shutdowns in 30 days", 15);
        else if (shutdowns >= 1) add("stability", "warning", shutdowns + " unexpected shutdown(s) in 30 days", 5);
        else add("stability", "ok", "no crashes in 30 days", 0);

        // recurring problems
        IReadOnlyList<RecurringProblem> recurring = new RecurringProblem[0];
        try { recurring = _history.RecurringProblems(now.AddHours(-Math.Max(1, t.RecurringProblemWindowHours)), Math.Max(2, t.RecurringProblemCount)); }
        catch (Exception) { }
        if (recurring.Count > 0) add("recurring_problems", "warning", recurring.Count + " problem(s) keep recurring", Math.Min(15, recurring.Count * 5));

        int score = Num.Clamp(100 - factors.Sum(f => f.Penalty), 0, 100);
        IReadOnlyList<MaintenanceEntry> maintenance = new MaintenanceEntry[0];
        try { maintenance = _history.RecentMaintenance(30, 25); } catch (Exception) { }

        var device = _cache.Get<DeviceInfo>(Categories.Device);
        var health = new DeviceHealth
        {
            Score = score,
            Status = score >= 80 ? "healthy" : score >= 50 ? "warning" : "critical",
            Factors = factors,
            RecurringProblems = recurring.ToList(),
            UnexpectedShutdownsLast30Days = shutdowns,
            BugChecksLast30Days = bugchecks,
            LastBootUtc = device != null ? device.LastBootUtc : null,
            PendingRestart = pendingRestart,
            RecentMaintenance = maintenance.ToList()
        };
        try { _history.AddHealthSnapshot(health, now); } catch (Exception) { }
        return health;
    }

    private int SafeCount(string prefix, DateTime since)
    {
        try { return _history.CountProblems(prefix, since); } catch (Exception) { return 0; }
    }
}
