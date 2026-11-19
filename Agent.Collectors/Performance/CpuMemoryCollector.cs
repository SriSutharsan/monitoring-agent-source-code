using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Agent.Core;
using Agent.Models;

namespace Agent.Collectors.Performance;

/// <summary>
/// CPU %, RAM, uptime, process count and top consumers. Uses GetSystemTimes / GlobalMemoryStatusEx and one process snapshot,
/// so a 30-second cycle costs a few milliseconds and needs no performance counters (which break on localized Windows installs).
/// </summary>
public sealed class CpuMemoryCollector : CollectorBase<PerformanceMetrics>
{
    private const int TopCount = 5;
    private static readonly TimeSpan TemperatureInterval = TimeSpan.FromMinutes(5);

    private readonly INativeSystem _native;
    private readonly IProcessSource _processes;
    private readonly IWmiClient _wmi;
    private readonly IClock _clock;
    private ulong _lastIdle, _lastKernel, _lastUser;
    private bool _haveCpuBaseline;
    private Dictionary<int, KeyValuePair<string, TimeSpan>> _lastProcessCpu = new Dictionary<int, KeyValuePair<string, TimeSpan>>();
    private DateTime _lastProcessSampleUtc;
    private double? _temperature;
    private DateTime _temperatureAt = DateTime.MinValue;
    private readonly int _logicalProcessors = Math.Max(1, Environment.ProcessorCount);

    public CpuMemoryCollector(INativeSystem native, IProcessSource processes, IWmiClient wmi, IClock clock)
    {
        _native = native;
        _processes = processes;
        _wmi = wmi;
        _clock = clock;
    }

    public override string Category { get { return Categories.PerfCpuMemory; } }

    public override async Task<PerformanceMetrics> CollectTypedAsync(CollectionContext ctx, CancellationToken ct)
    {
        if (!_haveCpuBaseline)
        {
            // first cycle: take a short second sample so the very first report already has a real CPU figure
            Sample(out _lastIdle, out _lastKernel, out _lastUser);
            _haveCpuBaseline = true;
            await Task.Delay(300, ct).ConfigureAwait(false);
        }
        return Collect();
    }

    private void Sample(out ulong idle, out ulong kernel, out ulong user)
    {
        if (!_native.TryGetCpuTimes(out idle, out kernel, out user)) { idle = kernel = user = 0; }
    }

    public PerformanceMetrics Collect()
    {
        var m = new PerformanceMetrics { Sections = new List<string>() };

        ulong idle, kernel, user;
        Sample(out idle, out kernel, out user);
        if (kernel + user > 0 && _lastKernel + _lastUser > 0)
        {
            double total = (double)((kernel + user) - (_lastKernel + _lastUser));
            double busy = total - (double)(idle - _lastIdle);
            if (total > 0) m.Cpu = new CpuMetrics { UsagePercent = Num.Round(Num.Clamp(100.0 * busy / total, 0, 100)), LogicalProcessors = _logicalProcessors };
        }
        _lastIdle = idle; _lastKernel = kernel; _lastUser = user;
        if (m.Cpu != null) m.Sections.Add("cpu");

        SystemMemoryStatus mem;
        if (_native.TryGetMemory(out mem) && mem.TotalPhysical > 0)
        {
            var used = mem.TotalPhysical - mem.AvailablePhysical;
            m.Memory = new MemoryMetrics
            {
                TotalBytes = mem.TotalPhysical,
                AvailableBytes = mem.AvailablePhysical,
                UsedBytes = used,
                UsedPercent = Num.Round(100.0 * used / mem.TotalPhysical),
                CommitTotalBytes = mem.TotalCommit > 0 ? mem.TotalCommit : (long?)null,
                CommitUsedBytes = mem.TotalCommit > 0 ? mem.TotalCommit - mem.AvailableCommit : (long?)null
            };
            m.Sections.Add("memory");
        }

        var ms = _native.TickCount64;
        if (ms > 0) { m.UptimeSeconds = ms / 1000; m.Sections.Add("uptime"); }

        ReadProcesses(m);
        ReadTemperature(m);
        return m.Sections.Count == 0 ? null : m;
    }

    private void ReadProcesses(PerformanceMetrics m)
    {
        var now = _clock.UtcNow;
        IReadOnlyList<RawProcess> snap;
        try { snap = _processes.Snapshot(); }
        catch (Exception) { return; }
        if (snap == null || snap.Count == 0) return;

        var elapsed = (now - _lastProcessSampleUtc).TotalSeconds;
        var usage = new List<ProcessUsage>(snap.Count);
        var current = new Dictionary<int, KeyValuePair<string, TimeSpan>>(snap.Count);
        foreach (var p in snap)
        {
            if (p.Pid == 0) continue;                                  // System Idle Process
            current[p.Pid] = new KeyValuePair<string, TimeSpan>(p.Name, p.CpuTime);
            double cpu = 0;
            KeyValuePair<string, TimeSpan> prev;
            if (_lastProcessSampleUtc != default(DateTime) && elapsed > 1 && _lastProcessCpu.TryGetValue(p.Pid, out prev) && prev.Key == p.Name)
            {
                var delta = (p.CpuTime - prev.Value).TotalSeconds;
                if (delta > 0) cpu = Num.Clamp(100.0 * delta / (elapsed * _logicalProcessors), 0, 100);
            }
            usage.Add(new ProcessUsage { Pid = p.Pid, Name = p.Name, CpuPercent = Num.Round(cpu), WorkingSetBytes = p.WorkingSetBytes, PrivateBytes = p.PrivateBytes });
        }
        bool haveDeltas = _lastProcessSampleUtc != default(DateTime) && elapsed > 1;
        _lastProcessCpu = current;
        _lastProcessSampleUtc = now;

        m.ProcessCount = snap.Count;
        m.TopMemoryProcesses = usage.OrderByDescending(u => u.WorkingSetBytes).Take(TopCount).ToList();
        if (haveDeltas) m.TopCpuProcesses = usage.Where(u => u.CpuPercent > 0).OrderByDescending(u => u.CpuPercent).Take(TopCount).ToList();
        m.Sections.Add("processes");
    }

    private void ReadTemperature(PerformanceMetrics m)
    {
        if (_clock.UtcNow - _temperatureAt >= TemperatureInterval)
        {
            _temperatureAt = _clock.UtcNow;
            _temperature = null;
            try
            {
                string error;
                double? max = null;
                foreach (var z in _wmi.TryQuery("SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature", @"root\wmi", out error))
                {
                    var t = z.Dbl("CurrentTemperature");
                    if (!t.HasValue) continue;
                    var c = t.Value / 10.0 - 273.15;                  // tenths of a Kelvin
                    if (c > 0 && c < 150 && (!max.HasValue || c > max.Value)) max = c;
                }
                _temperature = max.HasValue ? Num.Round(max.Value) : (double?)null;
            }
            catch (Exception) { }
        }
        if (_temperature.HasValue)
        {
            m.TemperatureC = _temperature;
            m.TemperatureSource = "acpi-thermal-zone";
            m.Sections.Add("temperature");
        }
    }
}
