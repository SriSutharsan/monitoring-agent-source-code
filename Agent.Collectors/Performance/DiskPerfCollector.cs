using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Agent.Core;
using Agent.Models;

namespace Agent.Collectors.Performance;

/// <summary>
/// Disk throughput, IOPS, busy % and queue length from the RAW performance classes (Win32_PerfRawData_*), computing deltas ourselves.
/// Unlike the "formatted" classes this does not make WMI sample twice, and unlike PerformanceCounter it is not affected by
/// localized counter names, so it behaves the same on every Windows language.
/// </summary>
public sealed class DiskPerfCollector : CollectorBase<PerformanceMetrics>
{
    private sealed class Sample
    {
        public double Read, Write, Reads, Writes, Busy, Latency, LatencyBase, Time100ns;
    }

    private readonly IWmiClient _wmi;
    private Dictionary<string, Sample> _previous;

    public DiskPerfCollector(IWmiClient wmi)
    {
        _wmi = wmi;
    }

    public override string Category { get { return Categories.PerfDisk; } }

    public override Task<PerformanceMetrics> CollectTypedAsync(CollectionContext ctx, CancellationToken ct)
    {
        return Task.FromResult(Collect());
    }

    public PerformanceMetrics Collect()
    {
        var rows = _wmi.Query("SELECT Name, DiskReadBytesPerSec, DiskWriteBytesPerSec, DiskReadsPerSec, DiskWritesPerSec, PercentDiskTime, CurrentDiskQueueLength, " +
                              "AvgDisksecPerTransfer, AvgDisksecPerTransfer_Base, Frequency_PerfTime, Timestamp_Sys100NS FROM Win32_PerfRawData_PerfDisk_PhysicalDisk");
        if (rows.Count == 0) return null;

        var current = new Dictionary<string, Sample>(StringComparer.OrdinalIgnoreCase);
        var queue = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        double frequency = 0;
        foreach (var r in rows)
        {
            var name = r.Str("Name");
            if (name == null) continue;
            current[name] = new Sample
            {
                Read = r.Dbl("DiskReadBytesPerSec") ?? 0, Write = r.Dbl("DiskWriteBytesPerSec") ?? 0,
                Reads = r.Dbl("DiskReadsPerSec") ?? 0, Writes = r.Dbl("DiskWritesPerSec") ?? 0,
                Busy = r.Dbl("PercentDiskTime") ?? 0,
                Latency = r.Dbl("AvgDisksecPerTransfer") ?? 0, LatencyBase = r.Dbl("AvgDisksecPerTransfer_Base") ?? 0,
                Time100ns = r.Dbl("Timestamp_Sys100NS") ?? 0
            };
            queue[name] = r.Dbl("CurrentDiskQueueLength") ?? 0;
            var f = r.Dbl("Frequency_PerfTime");
            if (f > 0) frequency = f.Value;
        }

        var previous = _previous;
        _previous = current;
        if (previous == null) return null;                    // first sample only establishes the baseline

        var disks = new List<DiskPerfMetrics>();
        foreach (var kv in current)
        {
            Sample p;
            if (!previous.TryGetValue(kv.Key, out p)) continue;
            var c = kv.Value;
            double seconds = (c.Time100ns - p.Time100ns) / 1e7;
            if (seconds <= 0.5) continue;
            double dRead = c.Read - p.Read, dWrite = c.Write - p.Write;
            if (dRead < 0 || dWrite < 0) continue;            // counter reset (driver reload, resume from sleep)
            double busy = Num.Clamp(100.0 * (c.Busy - p.Busy) / (c.Time100ns - p.Time100ns), 0, 100);
            double? latencyMs = null;
            double dBase = c.LatencyBase - p.LatencyBase;
            if (dBase > 0 && frequency > 0) latencyMs = Num.Round(((c.Latency - p.Latency) / dBase) / frequency * 1000.0, 2);
            disks.Add(new DiskPerfMetrics
            {
                Name = kv.Key == "_Total" ? "Total" : kv.Key,
                ReadBytesPerSec = Num.Round(dRead / seconds, 0),
                WriteBytesPerSec = Num.Round(dWrite / seconds, 0),
                ReadIops = Num.Round(Math.Max(0, c.Reads - p.Reads) / seconds),
                WriteIops = Num.Round(Math.Max(0, c.Writes - p.Writes) / seconds),
                BusyPercent = Num.Round(busy),
                QueueLength = Num.Round(queue[kv.Key], 2),
                AvgLatencyMs = latencyMs
            });
        }
        if (disks.Count == 0) return null;
        return new PerformanceMetrics { Sections = new List<string> { "disk" }, Disks = disks.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList() };
    }
}
