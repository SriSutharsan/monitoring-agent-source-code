using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Agent.Core;
using Agent.Models;

namespace Agent.Collectors.Hardware;

/// <summary>
/// Full hardware inventory (processor, memory modules, board, BIOS, GPU, monitors, batteries).
/// Runs on the 24-hour schedule; everything is read live from WMI/registry, nothing is assumed.
/// </summary>
public sealed class HardwareInventoryCollector : CollectorBase<HardwareInventory>
{
    private const string DisplayClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    private readonly IWmiClient _wmi;
    private readonly IRegistryReader _registry;
    private readonly INativeSystem _native;

    public HardwareInventoryCollector(IWmiClient wmi, IRegistryReader registry, INativeSystem native)
    {
        _wmi = wmi;
        _registry = registry;
        _native = native;
    }

    public override string Category { get { return Categories.Hardware; } }
    public override TimeSpan Timeout { get { return TimeSpan.FromMinutes(3); } }

    public override Task<HardwareInventory> CollectTypedAsync(CollectionContext ctx, CancellationToken ct)
    {
        return Task.FromResult(Collect());
    }

    public HardwareInventory Collect()
    {
        return new HardwareInventory
        {
            Scope = "full",
            Processor = ReadProcessor(),
            Memory = ReadMemory(),
            Motherboard = ReadBoard(),
            Bios = ReadBios(),
            Gpus = ReadGpus(),
            Monitors = ReadMonitors(),
            Batteries = BatteryReader.Read(_wmi)
        };
    }

    // ------------------------------------------------------------- processor

    public static string ArchitectureName(int? code)
    {
        switch (code)
        {
            case 0: return "x86";
            case 5: return "ARM";
            case 9: return "x64";
            case 12: return "ARM64";
            default: return code.HasValue ? "unknown(" + code.Value + ")" : null;
        }
    }

    private ProcessorInfo ReadProcessor()
    {
        var cpus = _wmi.Query("SELECT Name, Manufacturer, Architecture, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, CurrentClockSpeed, L2CacheSize, L3CacheSize, LoadPercentage FROM Win32_Processor");
        if (cpus.Count == 0) return null;
        var first = cpus[0];
        int? cores = SumOrNull(cpus.Select(c => c.Int("NumberOfCores")));
        int? logical = SumOrNull(cpus.Select(c => c.Int("NumberOfLogicalProcessors")));
        return new ProcessorInfo
        {
            Name = Collapse(first.Str("Name")),
            Manufacturer = NormalizeCpuVendor(first.Str("Manufacturer")),
            Architecture = ArchitectureName(first.Int("Architecture")),
            Sockets = cpus.Count,
            Cores = cores,
            LogicalProcessors = logical,
            MaxClockMhz = first.Int("MaxClockSpeed"),
            CurrentClockMhz = first.Int("CurrentClockSpeed"),
            L2CacheKb = first.Int("L2CacheSize"),
            L3CacheKb = first.Int("L3CacheSize"),
            CurrentUtilizationPercent = first.Dbl("LoadPercentage")
        };
    }

    private static int? SumOrNull(IEnumerable<int?> values)
    {
        int sum = 0; bool any = false;
        foreach (var v in values) if (v.HasValue) { sum += v.Value; any = true; }
        return any ? sum : (int?)null;
    }

    public static string NormalizeCpuVendor(string v)
    {
        if (v == null) return null;
        if (v.IndexOf("GenuineIntel", StringComparison.OrdinalIgnoreCase) >= 0) return "Intel";
        if (v.IndexOf("AuthenticAMD", StringComparison.OrdinalIgnoreCase) >= 0) return "AMD";
        return v;
    }

    private static string Collapse(string s)
    {
        return s == null ? null : System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
    }

    // ---------------------------------------------------------------- memory

    public static string MemoryTypeName(int? smbios, int? legacy)
    {
        switch (smbios.GetValueOrDefault())
        {
            case 18: return "DDR"; case 19: return "DDR2"; case 20: return "DDR"; case 21: return "DDR2"; case 24: return "DDR3";
            case 26: return "DDR4"; case 27: return "LPDDR"; case 28: return "LPDDR2"; case 29: return "LPDDR3"; case 30: return "LPDDR4";
            case 34: return "DDR5"; case 35: return "LPDDR5";
        }
        switch (legacy.GetValueOrDefault())
        {
            case 20: return "DDR"; case 21: return "DDR2"; case 22: return "DDR2 FB-DIMM"; case 24: return "DDR3"; case 26: return "DDR4";
        }
        return "Unknown";
    }

    public static string FormFactorName(int? code)
    {
        switch (code.GetValueOrDefault())
        {
            case 8: return "DIMM"; case 12: return "SODIMM"; case 9: return "RIMM"; case 13: return "SRIMM"; case 11: return "Proprietary card";
            case 0: return "Unknown"; default: return "Other";
        }
    }

    private MemoryInfo ReadMemory()
    {
        var info = new MemoryInfo { Modules = new List<MemoryModuleInfo>() };
        SystemMemoryStatus status;
        if (_native.TryGetMemory(out status))
        {
            info.TotalBytes = status.TotalPhysical;
            info.AvailableBytes = status.AvailablePhysical;
            info.UsedBytes = status.TotalPhysical - status.AvailablePhysical;
            info.UsedPercent = status.TotalPhysical > 0 ? Num.Round(100.0 * info.UsedBytes.Value / status.TotalPhysical) : (double?)null;
        }

        foreach (var m in _wmi.Query("SELECT BankLabel, DeviceLocator, Capacity, Manufacturer, PartNumber, SerialNumber, SMBIOSMemoryType, MemoryType, FormFactor, Speed, ConfiguredClockSpeed FROM Win32_PhysicalMemory"))
        {
            info.Modules.Add(new MemoryModuleInfo
            {
                Slot = m.Str("DeviceLocator"),
                BankLabel = m.Str("BankLabel"),
                Manufacturer = CleanVendor(m.Str("Manufacturer")),
                PartNumber = m.Str("PartNumber"),
                SerialNumber = CleanSerial(m.Str("SerialNumber")),
                CapacityBytes = m.Long("Capacity"),
                Type = MemoryTypeName(m.Int("SMBIOSMemoryType"), m.Int("MemoryType")),
                FormFactor = FormFactorName(m.Int("FormFactor")),
                SpeedMhz = m.Int("Speed"),
                ConfiguredSpeedMhz = m.Int("ConfiguredClockSpeed")
            });
        }
        info.SlotsUsed = info.Modules.Count;

        var array = _wmi.Query("SELECT MemoryDevices, MaxCapacity, MaxCapacityEx FROM Win32_PhysicalMemoryArray").FirstOrDefault();
        if (array != null)
        {
            info.SlotsTotal = array.Int("MemoryDevices");
            var exKb = array.Long("MaxCapacityEx");
            var kb = exKb.HasValue && exKb.Value > 0 ? exKb : array.Long("MaxCapacity");
            if (kb.HasValue && kb.Value > 0) info.MaxCapacityBytes = kb.Value * 1024;
        }
        return info;
    }

    private static string CleanVendor(string s)
    {
        if (DeviceIdentityService.IsPlaceholder(s)) return null;
        return s.Trim();
    }

    private static string CleanSerial(string s)
    {
        if (DeviceIdentityService.IsPlaceholder(s)) return null;
        var t = s.Trim();
        return t.Length == 0 || t.All(c => c == '0') ? null : t;
    }

    // ---------------------------------------------------------- board & BIOS

    private MotherboardInfo ReadBoard()
    {
        var b = _wmi.Query("SELECT Manufacturer, Product, Version, SerialNumber FROM Win32_BaseBoard").FirstOrDefault();
        if (b == null) return null;
        return new MotherboardInfo
        {
            Manufacturer = CleanVendor(b.Str("Manufacturer")),
            Product = CleanVendor(b.Str("Product")),
            Version = CleanVendor(b.Str("Version")),
            SerialNumber = CleanSerial(b.Str("SerialNumber"))
        };
    }

    private BiosInfo ReadBios()
    {
        var b = _wmi.Query("SELECT Manufacturer, SMBIOSBIOSVersion, Version, ReleaseDate, SMBIOSMajorVersion, SMBIOSMinorVersion FROM Win32_BIOS").FirstOrDefault();
        if (b == null) return null;
        var major = b.Int("SMBIOSMajorVersion");
        var minor = b.Int("SMBIOSMinorVersion");
        return new BiosInfo
        {
            Manufacturer = b.Str("Manufacturer"),
            Version = b.Str("SMBIOSBIOSVersion") ?? b.Str("Version"),
            ReleaseDateUtc = b.Dmtf("ReleaseDate"),
            SmbiosVersion = major.HasValue ? major + "." + minor.GetValueOrDefault() : null
        };
    }

    // ------------------------------------------------------------------- GPU

    private List<GpuInfo> ReadGpus()
    {
        var list = new List<GpuInfo>();
        foreach (var g in _wmi.Query("SELECT Name, AdapterCompatibility, AdapterRAM, DriverVersion, DriverDate, CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate, Status, PNPDeviceID FROM Win32_VideoController"))
        {
            var name = g.Str("Name");
            if (name == null || name.IndexOf("Remote Display", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Mirror Driver", StringComparison.OrdinalIgnoreCase) >= 0) continue;
            var gpu = new GpuInfo
            {
                Name = name,
                Manufacturer = g.Str("AdapterCompatibility"),
                MemoryBytes = g.Long("AdapterRAM"),
                DriverVersion = g.Str("DriverVersion"),
                DriverDateUtc = g.Dmtf("DriverDate"),
                CurrentHorizontalResolution = g.Int("CurrentHorizontalResolution"),
                CurrentVerticalResolution = g.Int("CurrentVerticalResolution"),
                RefreshRateHz = NullIfZero(g.Int("CurrentRefreshRate")),
                Status = g.Str("Status")
            };
            // Win32_VideoController.AdapterRAM is a 32-bit field: it is wrong for GPUs with 4 GB or more. The registry holds the real size.
            var real = RegistryVideoMemory(name);
            if (real.HasValue) { gpu.MemoryBytes = real; gpu.MemoryMayBeCapped = false; }
            else if (gpu.MemoryBytes.HasValue && gpu.MemoryBytes.Value >= 4000000000L) gpu.MemoryMayBeCapped = true;
            if (gpu.MemoryBytes.HasValue && gpu.MemoryBytes.Value <= 0) gpu.MemoryBytes = null;
            list.Add(gpu);
        }
        return list;
    }

    private static int? NullIfZero(int? v)
    {
        return v.HasValue && v.Value > 1 ? v : null;       // 0/1 mean "default adapter rate"
    }

    private long? RegistryVideoMemory(string adapterName)
    {
        try
        {
            foreach (var sub in _registry.GetSubKeyNames(RegistryHiveKind.LocalMachine, RegistryViewKind.View64, DisplayClassKey))
            {
                if (sub.Length != 4 || !sub.All(char.IsDigit)) continue;
                var key = DisplayClassKey + "\\" + sub;
                var desc = Text.ToStringOrNull(_registry.GetValue(RegistryHiveKind.LocalMachine, RegistryViewKind.View64, key, "DriverDesc"));
                if (desc == null || !string.Equals(desc, adapterName, StringComparison.OrdinalIgnoreCase)) continue;
                var raw = _registry.GetValue(RegistryHiveKind.LocalMachine, RegistryViewKind.View64, key, "HardwareInformation.qwMemorySize");
                if (raw is byte[] bytes && bytes.Length >= 8) return BitConverter.ToInt64(bytes, 0);
                var n = Text.ToLong(raw);
                if (n.HasValue && n.Value > 0) return n;
            }
        }
        catch (Exception) { }
        return null;
    }

    // -------------------------------------------------------------- monitors

    private static readonly Dictionary<string, string> MonitorVendors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        { "DEL", "Dell" }, { "HWP", "HP" }, { "HPN", "HP" }, { "LEN", "Lenovo" }, { "SAM", "Samsung" }, { "GSM", "LG Electronics" },
        { "LGD", "LG Display" }, { "ACI", "ASUS" }, { "AUS", "ASUS" }, { "AOC", "AOC" }, { "BNQ", "BenQ" }, { "ACR", "Acer" }, { "PHL", "Philips" },
        { "MSI", "MSI" }, { "VSC", "ViewSonic" }, { "SNY", "Sony" }, { "NEC", "NEC" }, { "CMN", "Chimei Innolux" }, { "BOE", "BOE" },
        { "AUO", "AU Optronics" }, { "SDC", "Samsung Display" }, { "IVO", "InfoVision" }, { "APP", "Apple" }, { "MEI", "Panasonic" },
        { "EIZ", "Eizo" }, { "GBT", "Gigabyte" }, { "HKC", "HKC" }, { "SHP", "Sharp" }, { "TSB", "Toshiba" }, { "MSX", "Microsoft" }
    };

    public static string MonitorVendorName(string code)
    {
        if (string.IsNullOrEmpty(code)) return null;
        string name;
        return MonitorVendors.TryGetValue(code.Trim(), out name) ? name : code.Trim();
    }

    private List<MonitorInfo> ReadMonitors()
    {
        const string ns = @"root\wmi";
        string error;
        var ids = _wmi.TryQuery("SELECT InstanceName, ManufacturerName, UserFriendlyName, SerialNumberID, YearOfManufacture FROM WmiMonitorID", ns, out error);
        var sizes = new Dictionary<string, IWmiObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in _wmi.Query("SELECT InstanceName, MaxHorizontalImageSize, MaxVerticalImageSize FROM WmiMonitorBasicDisplayParams", ns))
        {
            var n = p.Str("InstanceName");
            if (n != null) sizes[n] = p;
        }
        var list = new List<MonitorInfo>();
        foreach (var m in ids)
        {
            var instance = m.Str("InstanceName");
            var mon = new MonitorInfo
            {
                InstanceId = instance,
                Manufacturer = MonitorVendorName(Wmi.UShortArrayToString(m["ManufacturerName"])),
                Model = Wmi.UShortArrayToString(m["UserFriendlyName"]),
                SerialNumber = Wmi.UShortArrayToString(m["SerialNumberID"]),
                YearOfManufacture = NullIfZero(m.Int("YearOfManufacture"))
            };
            IWmiObject size;
            if (instance != null && sizes.TryGetValue(instance, out size))
            {
                var w = size.Dbl("MaxHorizontalImageSize");
                var h = size.Dbl("MaxVerticalImageSize");
                if (w > 0 && h > 0) mon.DiagonalInches = Num.Round(Math.Sqrt(w.Value * w.Value + h.Value * h.Value) / 2.54);
            }
            list.Add(mon);
        }
        return list;
    }
}

/// <summary>Battery static data (design/full-charge capacity, cycle count). Used by the hardware inventory and the battery collector.</summary>
public static class BatteryReader
{
    public static string ChemistryName(int? code)
    {
        switch (code.GetValueOrDefault())
        {
            case 3: return "Lead acid"; case 4: return "NiCd"; case 5: return "NiMH"; case 6: return "Li-ion"; case 7: return "Zinc air"; case 8: return "Li-polymer";
            default: return null;
        }
    }

    public static string StatusName(int? code)
    {
        switch (code.GetValueOrDefault())
        {
            case 1: return "Discharging"; case 2: return "AC (not charging)"; case 3: return "Full"; case 4: return "Low"; case 5: return "Critical";
            case 6: case 7: case 8: case 9: return "Charging"; default: return "Unknown";
        }
    }

    public static List<BatteryInfo> Read(IWmiClient wmi)
    {
        const string ns = @"root\wmi";
        var list = new List<BatteryInfo>();
        var win32 = wmi.Query("SELECT Name, DeviceID, Chemistry, EstimatedChargeRemaining, BatteryStatus, DesignCapacity, FullChargeCapacity FROM Win32_Battery");
        if (win32.Count == 0) return list;                     // desktop / no battery

        var statics = wmi.Query("SELECT DesignedCapacity, ManufactureName, SerialNumber, DeviceName FROM BatteryStaticData", ns);
        var full = wmi.Query("SELECT FullChargedCapacity FROM BatteryFullChargedCapacity", ns);
        var cycles = wmi.Query("SELECT CycleCount FROM BatteryCycleCount", ns);

        for (int i = 0; i < win32.Count; i++)
        {
            var w = win32[i];
            var s = i < statics.Count ? statics[i] : null;
            var f = i < full.Count ? full[i] : null;
            var c = i < cycles.Count ? cycles[i] : null;

            long? design = s != null ? s.Long("DesignedCapacity") : null;
            if (!(design > 0)) design = w.Long("DesignCapacity");
            long? fullCharge = f != null ? f.Long("FullChargedCapacity") : null;
            if (!(fullCharge > 0)) fullCharge = w.Long("FullChargeCapacity");
            if (!(design > 0)) design = null;
            if (!(fullCharge > 0)) fullCharge = null;
            int? cycle = c != null ? c.Int("CycleCount") : null;
            if (cycle.HasValue && cycle.Value <= 0) cycle = null;      // 0 means "not reported" on most firmware

            list.Add(new BatteryInfo
            {
                Name = w.Str("Name") ?? (s != null ? s.Str("DeviceName") : null),
                Manufacturer = s != null ? s.Str("ManufactureName") : null,
                SerialNumber = s != null ? s.Str("SerialNumber") : null,
                Chemistry = ChemistryName(w.Int("Chemistry")),
                DesignCapacityMwh = design,
                FullChargeCapacityMwh = fullCharge,
                HealthPercent = design.HasValue && fullCharge.HasValue ? Num.Round(Num.Clamp(100.0 * fullCharge.Value / design.Value, 0, 100)) : (double?)null,
                CycleCount = cycle,
                ChargePercent = w.Int("EstimatedChargeRemaining"),
                ChargingStatus = StatusName(w.Int("BatteryStatus"))
            });
        }
        return list;
    }
}
