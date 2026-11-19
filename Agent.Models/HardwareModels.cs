using System;
using System.Collections.Generic;

namespace Agent.Models;

public class HardwareInventory
{
    /// <summary>"full" (24h inventory) or "peripherals" (5 min PnP scan used for connect/disconnect detection).</summary>
    public string Scope { get; set; }
    public ProcessorInfo Processor { get; set; }
    public MemoryInfo Memory { get; set; }
    public MotherboardInfo Motherboard { get; set; }
    public BiosInfo Bios { get; set; }
    public List<GpuInfo> Gpus { get; set; }
    public List<MonitorInfo> Monitors { get; set; }
    public List<BatteryInfo> Batteries { get; set; }
    public List<PeripheralInfo> Peripherals { get; set; }
}

public class ProcessorInfo
{
    public string Manufacturer { get; set; }
    public string Name { get; set; }
    public string Architecture { get; set; }
    public int? Sockets { get; set; }
    public int? Cores { get; set; }
    public int? LogicalProcessors { get; set; }
    public int? MaxClockMhz { get; set; }
    public int? CurrentClockMhz { get; set; }
    public int? L2CacheKb { get; set; }
    public int? L3CacheKb { get; set; }
    public double? CurrentUtilizationPercent { get; set; }
}

public class MemoryInfo
{
    public long? TotalBytes { get; set; }
    public long? AvailableBytes { get; set; }
    public long? UsedBytes { get; set; }
    public double? UsedPercent { get; set; }
    public int? SlotsTotal { get; set; }
    public int? SlotsUsed { get; set; }
    public long? MaxCapacityBytes { get; set; }
    public List<MemoryModuleInfo> Modules { get; set; }
}

public class MemoryModuleInfo
{
    public string Slot { get; set; }
    public string BankLabel { get; set; }
    public string Manufacturer { get; set; }
    public string PartNumber { get; set; }
    public string SerialNumber { get; set; }
    public long? CapacityBytes { get; set; }
    public string Type { get; set; }
    public string FormFactor { get; set; }
    public int? SpeedMhz { get; set; }
    public int? ConfiguredSpeedMhz { get; set; }
}

public class MotherboardInfo
{
    public string Manufacturer { get; set; }
    public string Product { get; set; }
    public string Version { get; set; }
    public string SerialNumber { get; set; }
}

public class BiosInfo
{
    public string Manufacturer { get; set; }
    public string Version { get; set; }
    public string SmbiosVersion { get; set; }
    public DateTime? ReleaseDateUtc { get; set; }
}

public class GpuInfo
{
    public string Name { get; set; }
    public string Manufacturer { get; set; }
    public long? MemoryBytes { get; set; }
    public bool? MemoryMayBeCapped { get; set; }
    public string DriverVersion { get; set; }
    public DateTime? DriverDateUtc { get; set; }
    public int? CurrentHorizontalResolution { get; set; }
    public int? CurrentVerticalResolution { get; set; }
    public int? RefreshRateHz { get; set; }
    public string Status { get; set; }
}

public class MonitorInfo
{
    public string Manufacturer { get; set; }
    public string Model { get; set; }
    public string SerialNumber { get; set; }
    public int? YearOfManufacture { get; set; }
    public double? DiagonalInches { get; set; }
    public string InstanceId { get; set; }
}

public class BatteryInfo
{
    public string Name { get; set; }
    public string Manufacturer { get; set; }
    public string SerialNumber { get; set; }
    public string Chemistry { get; set; }
    public long? DesignCapacityMwh { get; set; }
    public long? FullChargeCapacityMwh { get; set; }
    public double? HealthPercent { get; set; }
    public int? CycleCount { get; set; }
    public int? ChargePercent { get; set; }
    public string ChargingStatus { get; set; }
}

public class PeripheralInfo
{
    public string Name { get; set; }
    public string DeviceClass { get; set; }
    public string Manufacturer { get; set; }
    public string DeviceId { get; set; }
    public string Connection { get; set; }         // usb | bluetooth
    public string Status { get; set; }
}
