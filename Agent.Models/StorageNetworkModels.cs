using System;
using System.Collections.Generic;

namespace Agent.Models;

public class StorageInventory
{
    public List<PhysicalDiskInfo> Disks { get; set; }
    public List<VolumeInfo> Volumes { get; set; }
}

public class PhysicalDiskInfo
{
    public string DeviceId { get; set; }
    public int? Index { get; set; }
    public string Manufacturer { get; set; }
    public string Model { get; set; }
    public string SerialNumber { get; set; }
    public string MediaType { get; set; }            // HDD | SSD | NVMe | Unknown
    public string BusType { get; set; }
    public long? SizeBytes { get; set; }
    public string HealthStatus { get; set; }         // Healthy | Warning | Unhealthy | Unknown
    public string OperationalStatus { get; set; }
    public bool? SmartPredictFailure { get; set; }
    public int? TemperatureC { get; set; }
    public long? PowerOnHours { get; set; }
    public int? WearPercent { get; set; }
    public string SmartSource { get; set; }          // storage-reliability | ata-smart | none
    public string FirmwareVersion { get; set; }
    public List<PartitionInfo> Partitions { get; set; }
}

public class PartitionInfo
{
    public int? Index { get; set; }
    public string Type { get; set; }
    public long? SizeBytes { get; set; }
    public long? StartOffsetBytes { get; set; }
    public bool? IsBootPartition { get; set; }
    public bool? IsPrimary { get; set; }
    public List<string> DriveLetters { get; set; }
}

public class VolumeInfo
{
    public string DriveLetter { get; set; }
    public string Label { get; set; }
    public string FileSystem { get; set; }
    public string DriveType { get; set; }
    public long? TotalBytes { get; set; }
    public long? FreeBytes { get; set; }
    public long? UsedBytes { get; set; }
    public double? UsedPercent { get; set; }
    public bool? IsSystemDrive { get; set; }
    public string PhysicalDiskId { get; set; }
}

public class NetworkInfo
{
    /// <summary>"configuration" (adapters + IP config) or "wifi" (Wi-Fi only).</summary>
    public string Scope { get; set; }
    public string Hostname { get; set; }
    public string PrimaryIPv4 { get; set; }
    public List<AdapterInfo> Adapters { get; set; }
    public WifiInfo Wifi { get; set; }
}

public class AdapterInfo
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string Description { get; set; }
    public string Manufacturer { get; set; }
    public string Model { get; set; }
    public string MacAddress { get; set; }
    public string AdapterType { get; set; }           // Ethernet | Wi-Fi | VPN | Virtual | Loopback | Bluetooth | Mobile | Other
    public string Status { get; set; }                // Up | Down | ...
    public string ConnectionStatus { get; set; }
    public bool? IsPhysical { get; set; }
    public string DriverVersion { get; set; }
    public DateTime? DriverDateUtc { get; set; }
    public string DriverProvider { get; set; }
    public long? SpeedBitsPerSecond { get; set; }
    public List<string> IPv4Addresses { get; set; }
    public List<string> IPv6Addresses { get; set; }
    public List<string> SubnetMasks { get; set; }
    public List<string> DefaultGateways { get; set; }
    public List<string> DnsServers { get; set; }
    public bool? DhcpEnabled { get; set; }
    public string DhcpServer { get; set; }
    public string DnsSuffix { get; set; }
}

public class WifiInfo
{
    public bool AdapterPresent { get; set; }
    public bool Connected { get; set; }
    public string AdapterName { get; set; }
    public string AdapterModel { get; set; }
    public string Ssid { get; set; }
    public string Bssid { get; set; }
    public int? SignalQualityPercent { get; set; }
    public int? SignalStrengthDbm { get; set; }
    public int? FrequencyMhz { get; set; }
    public int? Channel { get; set; }
    public string Band { get; set; }                 // 2.4 GHz | 5 GHz | 6 GHz
    public string RadioType { get; set; }            // 802.11n / ac / ax / be
    public string WifiStandard { get; set; }         // Wi-Fi 4 / 5 / 6 / 6E / 7
    public long? ReceiveRateMbps { get; set; }
    public long? TransmitRateMbps { get; set; }
    public string Authentication { get; set; }       // e.g. WPA2-Personal, WPA3-Personal, WPA2-Enterprise
    public string Cipher { get; set; }
    public DateTime? ConnectedSinceUtc { get; set; }
    public long? ConnectionDurationSeconds { get; set; }
    public string State { get; set; }
}
