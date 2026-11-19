using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Agent.Collectors.Hardware;
using Agent.Core;
using Agent.Models;

namespace Agent.Collectors.Performance;

/// <summary>Battery charge, charging state, health (full-charge vs design capacity) and cycle count where the firmware reports it.</summary>
public sealed class BatteryCollector : CollectorBase<PerformanceMetrics>
{
    private readonly INativeSystem _native;
    private readonly IWmiClient _wmi;

    public BatteryCollector(INativeSystem native, IWmiClient wmi)
    {
        _native = native;
        _wmi = wmi;
    }

    public override string Category { get { return Categories.Battery; } }

    public override Task<PerformanceMetrics> CollectTypedAsync(CollectionContext ctx, CancellationToken ct)
    {
        return Task.FromResult(Collect());
    }

    public static string ChargingState(bool batteryPresent, bool? onAc, bool charging, int? percent)
    {
        if (!batteryPresent) return "None";
        if (charging) return "Charging";
        if (onAc == true) return percent.HasValue && percent.Value >= 95 ? "Full" : "Not charging";
        if (onAc == false) return "Discharging";
        return "Unknown";
    }

    public PerformanceMetrics Collect()
    {
        var p = _native.GetPowerStatus();
        var info = new BatteryStatusInfo { Present = p.BatteryPresent };
        if (p.BatteryPresent)
        {
            info.ChargePercent = p.ChargePercent;
            info.OnAcPower = p.OnAcPower;
            info.ChargingStatus = ChargingState(true, p.OnAcPower, p.Charging, p.ChargePercent);
            if (p.SecondsRemaining.HasValue && p.SecondsRemaining.Value > 0 && p.OnAcPower != true) info.EstimatedMinutesRemaining = p.SecondsRemaining.Value / 60;
            var b = BatteryReader.Read(_wmi).FirstOrDefault();
            if (b != null)
            {
                info.HealthPercent = b.HealthPercent;
                info.CycleCount = b.CycleCount;
                if (!info.ChargePercent.HasValue) info.ChargePercent = b.ChargePercent;
            }
        }
        return new PerformanceMetrics { Sections = new List<string> { "battery" }, Battery = info };
    }
}
