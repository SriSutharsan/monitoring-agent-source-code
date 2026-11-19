using System;
using System.Collections.Generic;

namespace Agent.Models;

/// <summary>
/// Partial snapshot: each performance collector fills only its own section(s); "sections" lists which.
/// </summary>
public class PerformanceMetrics
{
    public List<string> Sections { get; set; }
    public CpuMetrics Cpu { get; set; }
    public MemoryMetrics Memory { get; set; }
    public List<DiskPerfMetrics> Disks { get; set; }
    public NetworkPerfMetrics Network { get; set; }
    public BatteryStatusInfo Battery { get; set; }
    public long? UptimeSeconds { get; set; }
    public int? ProcessCount { get; set; }
    public List<ProcessUsage> TopCpuProcesses { get; set; }
    public List<ProcessUsage> TopMemoryProcesses { get; set; }
    public double? TemperatureC { get; set; }
    public string TemperatureSource { get; set; }
}

public class CpuMetrics
{
    public double UsagePercent { get; set; }
    public int? LogicalProcessors { get; set; }
    public int? CurrentClockMhz { get; set; }
}

public class MemoryMetrics
{
    public long TotalBytes { get; set; }
    public long AvailableBytes { get; set; }
    public long UsedBytes { get; set; }
    public double UsedPercent { get; set; }
    public long? CommitTotalBytes { get; set; }
    public long? CommitUsedBytes { get; set; }
}

public class DiskPerfMetrics
{
    public string Name { get; set; }
    public double ReadBytesPerSec { get; set; }
    public double WriteBytesPerSec { get; set; }
    public double ReadIops { get; set; }
    public double WriteIops { get; set; }
    public double BusyPercent { get; set; }
    public double QueueLength { get; set; }
    public double? AvgLatencyMs { get; set; }
}

public class NetworkPerfMetrics
{
    public double DownloadBytesPerSec { get; set; }
    public double UploadBytesPerSec { get; set; }
    public long TotalDownloadedBytes { get; set; }
    public long TotalUploadedBytes { get; set; }
    public double? UtilizationPercent { get; set; }
    public double? LatencyMs { get; set; }
    public double? PacketLossPercent { get; set; }
    public string LatencyTarget { get; set; }
    public bool InternetReachable { get; set; }
    public bool ApiReachable { get; set; }
    public bool VpnConnected { get; set; }
    public List<string> VpnAdapters { get; set; }
    public string ActiveAdapter { get; set; }
    public List<NetworkEvent> Events { get; set; }
}

public class NetworkEvent
{
    public DateTime TimestampUtc { get; set; }
    public string Type { get; set; }                 // adapter_up | adapter_down | internet_lost | internet_restored | adapter_added | adapter_removed
    public string Adapter { get; set; }
    public string Detail { get; set; }
}

public class BatteryStatusInfo
{
    public bool Present { get; set; }
    public int? ChargePercent { get; set; }
    public string ChargingStatus { get; set; }       // Charging | Discharging | Full | Unknown
    public bool? OnAcPower { get; set; }
    public int? EstimatedMinutesRemaining { get; set; }
    public double? HealthPercent { get; set; }
    public int? CycleCount { get; set; }
}

public class ProcessUsage
{
    public int Pid { get; set; }
    public string Name { get; set; }
    public double CpuPercent { get; set; }
    public long WorkingSetBytes { get; set; }
    public long PrivateBytes { get; set; }
}

// ---------------------------------------------------------------- software

public class SoftwareInventory
{
    public OperatingSystemInfo Os { get; set; }
    public List<InstalledApplication> Applications { get; set; }
    public List<InstalledUpdate> Updates { get; set; }
    public WindowsUpdateSummary UpdateSummary { get; set; }
}

public class OperatingSystemInfo
{
    public string Caption { get; set; }
    public string Edition { get; set; }
    public string Version { get; set; }
    public string DisplayVersion { get; set; }
    public string BuildNumber { get; set; }
    public int? Revision { get; set; }
    public string Architecture { get; set; }
    public string ServicePack { get; set; }
    public DateTime? InstallDateUtc { get; set; }
    public string ActivationStatus { get; set; }     // Licensed | Unlicensed | Grace | Notification | Unknown
    public string ActivationDetail { get; set; }
}

public class InstalledApplication
{
    public string Key { get; set; }                  // stable id: scope|arch|registry key
    public string Name { get; set; }
    public string Version { get; set; }
    public string Publisher { get; set; }
    public DateTime? InstallDateUtc { get; set; }
    public string InstallLocation { get; set; }
    public string Architecture { get; set; }         // x86 | x64
    public string Scope { get; set; }                // machine | user
    public string UninstallCommand { get; set; }
    public string QuietUninstallCommand { get; set; }
    public bool? IsUnauthorized { get; set; }
    public string PolicyMatch { get; set; }
    public bool? IsOutdated { get; set; }
    public string MinimumRequiredVersion { get; set; }
    public bool? IsPotentiallyUnwanted { get; set; }
}

public class InstalledUpdate
{
    public string Id { get; set; }                   // KB number
    public string Description { get; set; }
    public DateTime? InstalledOnUtc { get; set; }
    public string InstalledBy { get; set; }
    public bool? IsSecurityUpdate { get; set; }
}

public class WindowsUpdateSummary
{
    public string LatestSecurityUpdateId { get; set; }
    public DateTime? LatestSecurityUpdateUtc { get; set; }
    public DateTime? LastUpdateInstalledUtc { get; set; }
    public int? PendingUpdateCount { get; set; }
    public List<string> PendingUpdateTitles { get; set; }
    public string PendingDetection { get; set; }     // wua-cached | unavailable
    public List<UpdateHistoryEntry> RecentFailures { get; set; }
}

public class UpdateHistoryEntry
{
    public string Title { get; set; }
    public DateTime? DateUtc { get; set; }
    public string Result { get; set; }
    public string HResult { get; set; }
}
