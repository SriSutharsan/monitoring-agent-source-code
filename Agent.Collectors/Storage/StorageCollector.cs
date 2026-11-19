using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Agent.Core;
using Agent.Models;

namespace Agent.Collectors.Storage;

/// <summary>Parses the 512-byte ATA SMART attribute table (MSStorageDriver_ATAPISmartData.VendorSpecific).</summary>
public static class SmartParser
{
    public class Attribute
    {
        public int Id { get; set; }
        public int Value { get; set; }
        public int Worst { get; set; }
        public long Raw { get; set; }
    }

    /// <summary>Layout: 2-byte revision, then 30 entries of 12 bytes: id, flags(2), value, worst, raw(6), reserved.</summary>
    public static Dictionary<int, Attribute> Parse(byte[] data)
    {
        var result = new Dictionary<int, Attribute>();
        if (data == null || data.Length < 14) return result;
        for (int off = 2; off + 12 <= data.Length && off < 2 + 30 * 12; off += 12)
        {
            int id = data[off];
            if (id == 0) continue;
            long raw = 0;
            for (int i = 5; i >= 0; i--) raw = (raw << 8) | data[off + 5 + i];
            result[id] = new Attribute { Id = id, Value = data[off + 3], Worst = data[off + 4], Raw = raw };
        }
        return result;
    }

    public static int? Temperature(Dictionary<int, Attribute> a)
    {
        Attribute t;
        if (a.TryGetValue(194, out t) || a.TryGetValue(190, out t))
        {
            int c = (int)(t.Raw & 0xFF);
            if (c > 0 && c < 120) return c;
        }
        return null;
    }

    public static long? PowerOnHours(Dictionary<int, Attribute> a)
    {
        Attribute t;
        return a.TryGetValue(9, out t) ? t.Raw & 0xFFFFFFFF : (long?)null;
    }

    /// <summary>Remaining-life style attributes used by SSD vendors (177 wear leveling, 231 life left, 233 media wearout) -> percent worn.</summary>
    public static int? WearPercent(Dictionary<int, Attribute> a)
    {
        Attribute t;
        foreach (var id in new[] { 231, 233, 177 })
            if (a.TryGetValue(id, out t) && t.Value > 0 && t.Value <= 100) return 100 - t.Value;
        return null;
    }
}

/// <summary>
/// Physical disks, partitions and logical volumes.
/// Windows 8+ uses the modern Storage Management WMI classes (media type, bus type, health, reliability counters).
/// Windows 7 falls back to Win32_DiskDrive, a seek-penalty ioctl for HDD/SSD detection and ATA SMART data from root\wmi.
/// SMART, temperature and reliability data need administrator rights (the service runs as LocalSystem) and drive/driver support.
/// </summary>
public sealed class StorageCollector : CollectorBase<StorageInventory>
{
    private const string StorageNs = @"root\Microsoft\Windows\Storage";
    private const string WmiNs = @"root\wmi";

    private readonly IWmiClient _wmi;
    private readonly IDiskIoctl _ioctl;

    public StorageCollector(IWmiClient wmi, IDiskIoctl ioctl)
    {
        _wmi = wmi;
        _ioctl = ioctl;
    }

    public override string Category { get { return Categories.Storage; } }
    public override TimeSpan Timeout { get { return TimeSpan.FromMinutes(3); } }

    public override Task<StorageInventory> CollectTypedAsync(CollectionContext ctx, CancellationToken ct)
    {
        return Task.FromResult(Collect());
    }

    public static string BusTypeName(int? code)
    {
        switch (code.GetValueOrDefault(-1))
        {
            case 1: return "SCSI"; case 2: return "ATAPI"; case 3: return "ATA"; case 4: return "1394"; case 5: return "SSA"; case 6: return "Fibre Channel";
            case 7: return "USB"; case 8: return "RAID"; case 9: return "iSCSI"; case 10: return "SAS"; case 11: return "SATA"; case 12: return "SD";
            case 13: return "MMC"; case 14: return "Virtual"; case 15: return "File-backed virtual"; case 16: return "Storage Spaces"; case 17: return "NVMe";
            default: return null;
        }
    }

    public static string HealthName(int? code)
    {
        switch (code.GetValueOrDefault(-1))
        {
            case 0: return "Healthy"; case 1: return "Warning"; case 2: return "Unhealthy"; default: return "Unknown";
        }
    }

    private static readonly KeyValuePair<string, string>[] VendorPrefixes =
    {
        new KeyValuePair<string, string>("samsung", "Samsung"), new KeyValuePair<string, string>("wdc", "Western Digital"),
        new KeyValuePair<string, string>("wd ", "Western Digital"), new KeyValuePair<string, string>("western digital", "Western Digital"),
        new KeyValuePair<string, string>("st", "Seagate"), new KeyValuePair<string, string>("seagate", "Seagate"),
        new KeyValuePair<string, string>("crucial", "Crucial"), new KeyValuePair<string, string>("ct", "Crucial"),
        new KeyValuePair<string, string>("sandisk", "SanDisk"), new KeyValuePair<string, string>("kingston", "Kingston"),
        new KeyValuePair<string, string>("intel", "Intel"), new KeyValuePair<string, string>("toshiba", "Toshiba"),
        new KeyValuePair<string, string>("kioxia", "Kioxia"), new KeyValuePair<string, string>("kxg", "Kioxia"), new KeyValuePair<string, string>("micron", "Micron"),
        new KeyValuePair<string, string>("hfs", "SK hynix"), new KeyValuePair<string, string>("sk hynix", "SK hynix"),
        new KeyValuePair<string, string>("hitachi", "Hitachi"), new KeyValuePair<string, string>("hgst", "HGST"), new KeyValuePair<string, string>("adata", "ADATA"),
        new KeyValuePair<string, string>("lite-on", "Lite-On"), new KeyValuePair<string, string>("liteon", "Lite-On"), new KeyValuePair<string, string>("lexar", "Lexar"),
        new KeyValuePair<string, string>("transcend", "Transcend"), new KeyValuePair<string, string>("patriot", "Patriot"), new KeyValuePair<string, string>("pny", "PNY"),
        new KeyValuePair<string, string>("netac", "Netac"), new KeyValuePair<string, string>("phison", "Phison"), new KeyValuePair<string, string>("sabrent", "Sabrent")
    };

    /// <summary>Win32_DiskDrive.Manufacturer is usually the useless "(Standard disk drives)", so infer the brand from the model string.</summary>
    public static string InferVendor(string manufacturer, string model)
    {
        if (!string.IsNullOrEmpty(manufacturer) && !manufacturer.StartsWith("(Standard", StringComparison.OrdinalIgnoreCase) &&
            !manufacturer.Equals("NVMe", StringComparison.OrdinalIgnoreCase) && !manufacturer.Equals("ATA", StringComparison.OrdinalIgnoreCase)) return manufacturer;
        var m = (model ?? "").Trim().ToLowerInvariant();
        foreach (var p in VendorPrefixes)
        {
            if (p.Key.Length <= 2)
            {
                // short prefixes (ST, CT) must be followed by a digit, e.g. ST1000LM035, CT500MX500SSD1
                if (m.StartsWith(p.Key, StringComparison.Ordinal) && m.Length > p.Key.Length && char.IsDigit(m[p.Key.Length])) return p.Value;
            }
            else if (m.StartsWith(p.Key, StringComparison.Ordinal)) return p.Value;
        }
        return null;
    }

    public static string NormalizeSerial(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s.Trim().TrimEnd('.');
        return t.Length == 0 || t.All(c => c == '0') ? null : t;
    }

    public StorageInventory Collect()
    {
        var disks = new List<PhysicalDiskInfo>();
        var driveRows = _wmi.Query("SELECT Index, DeviceID, Model, Manufacturer, SerialNumber, Size, InterfaceType, MediaType, FirmwareRevision, Status, PNPDeviceID FROM Win32_DiskDrive");

        string err;
        var msft = new Dictionary<int, IWmiObject>();
        foreach (var d in _wmi.TryQuery("SELECT DeviceId, FriendlyName, Manufacturer, Model, SerialNumber, MediaType, BusType, HealthStatus, OperationalStatus, Size, FirmwareVersion FROM MSFT_PhysicalDisk", StorageNs, out err))
        {
            int idx;
            if (int.TryParse(d.Str("DeviceId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out idx)) msft[idx] = d;
        }

        var reliability = new Dictionary<int, IWmiObject>();
        foreach (var r in _wmi.TryQuery("SELECT DeviceId, Temperature, Wear, PowerOnHours FROM MSFT_StorageReliabilityCounter", StorageNs, out err))
        {
            int idx;
            if (int.TryParse(r.Str("DeviceId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out idx)) reliability[idx] = r;
        }

        var predictFailure = _wmi.TryQuery("SELECT InstanceName, PredictFailure FROM MSStorageDriver_FailurePredictStatus", WmiNs, out err);
        var smartData = _wmi.TryQuery("SELECT InstanceName, VendorSpecific FROM MSStorageDriver_ATAPISmartData", WmiNs, out err);

        var letterToDisk = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in driveRows)
        {
            int index = row.Int("Index") ?? -1;
            IWmiObject ms; msft.TryGetValue(index, out ms);
            IWmiObject rel; reliability.TryGetValue(index, out rel);
            var model = Clean(ms != null ? ms.Str("FriendlyName") : null) ?? Clean(row.Str("Model"));

            var disk = new PhysicalDiskInfo
            {
                DeviceId = row.Str("DeviceID"),
                Index = index >= 0 ? index : (int?)null,
                Model = model,
                Manufacturer = InferVendor(ms != null ? ms.Str("Manufacturer") : null, model) ?? InferVendor(row.Str("Manufacturer"), model),
                SerialNumber = NormalizeSerial(ms != null ? ms.Str("SerialNumber") : null) ?? NormalizeSerial(row.Str("SerialNumber")),
                SizeBytes = (ms != null ? ms.Long("Size") : null) ?? row.Long("Size"),
                FirmwareVersion = (ms != null ? ms.Str("FirmwareVersion") : null) ?? row.Str("FirmwareRevision"),
                Partitions = new List<PartitionInfo>()
            };

            // bus type + media type
            int? busCode = ms != null ? ms.Int("BusType") : null;
            DiskIoctlInfo io = null;
            if (index >= 0 && (busCode == null || ms == null || !(ms.Int("MediaType") == 3 || ms.Int("MediaType") == 4 || ms.Int("MediaType") == 5)))
            {
                try { io = _ioctl.Query(index); } catch (Exception) { }
                if (busCode == null && io != null) busCode = io.BusType;
            }
            disk.BusType = BusTypeName(busCode) ?? row.Str("InterfaceType");
            disk.MediaType = ResolveMediaType(ms != null ? ms.Int("MediaType") : null, busCode, io, model, row.Str("PNPDeviceID"));

            // health
            if (ms != null && ms.Int("HealthStatus").HasValue)
            {
                disk.HealthStatus = HealthName(ms.Int("HealthStatus"));
                var op = ms["OperationalStatus"] as System.Collections.IEnumerable;
                if (op != null && !(op is string)) disk.OperationalStatus = string.Join(",", op.Cast<object>().Select(o => Convert.ToString(o, CultureInfo.InvariantCulture)));
            }
            else
            {
                var st = row.Str("Status");
                disk.OperationalStatus = st;
                disk.HealthStatus = st == null ? "Unknown" : st.Equals("OK", StringComparison.OrdinalIgnoreCase) ? "Healthy"
                    : st.StartsWith("Pred", StringComparison.OrdinalIgnoreCase) ? "Unhealthy" : st.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ? "Unknown" : "Warning";
            }

            // SMART predict-failure, temperature, wear, power-on hours
            var pnp = row.Str("PNPDeviceID");
            var pf = pnp == null ? null : predictFailure.FirstOrDefault(p => InstanceMatches(p.Str("InstanceName"), pnp));
            if (pf != null)
            {
                disk.SmartPredictFailure = pf.Bool("PredictFailure");
                disk.SmartSource = "ata-smart";
                if (disk.SmartPredictFailure == true) disk.HealthStatus = "Unhealthy";
            }
            if (rel != null)
            {
                disk.TemperatureC = PositiveOrNull(rel.Int("Temperature"));
                disk.WearPercent = rel.Int("Wear");
                disk.PowerOnHours = PositiveOrNull(rel.Long("PowerOnHours"));
                if (disk.TemperatureC.HasValue || disk.WearPercent.HasValue || disk.PowerOnHours.HasValue) disk.SmartSource = "storage-reliability";
            }
            if (!disk.TemperatureC.HasValue && pnp != null)
            {
                var sm = smartData.FirstOrDefault(p => InstanceMatches(p.Str("InstanceName"), pnp));
                var bytes = sm != null ? sm["VendorSpecific"] as byte[] : null;
                if (bytes != null)
                {
                    var attrs = SmartParser.Parse(bytes);
                    disk.TemperatureC = SmartParser.Temperature(attrs);
                    disk.PowerOnHours = disk.PowerOnHours ?? SmartParser.PowerOnHours(attrs);
                    disk.WearPercent = disk.WearPercent ?? SmartParser.WearPercent(attrs);
                    if (disk.SmartSource == null) disk.SmartSource = "ata-smart";
                }
            }
            if (disk.SmartSource == null) disk.SmartSource = "none";

            ReadPartitions(row, disk, letterToDisk);
            disks.Add(disk);
        }

        return new StorageInventory { Disks = disks, Volumes = ReadVolumes(letterToDisk) };
    }

    private static int? PositiveOrNull(int? v) { return v.HasValue && v.Value > 0 ? v : null; }
    private static long? PositiveOrNull(long? v) { return v.HasValue && v.Value > 0 ? v : null; }

    private static bool InstanceMatches(string instance, string pnpDeviceId)
    {
        if (string.IsNullOrEmpty(instance) || string.IsNullOrEmpty(pnpDeviceId)) return false;
        return instance.StartsWith(pnpDeviceId, StringComparison.OrdinalIgnoreCase);
    }

    private static string Clean(string s)
    {
        return Text.NullIfEmpty(s);
    }

    public static string ResolveMediaType(int? msftMediaType, int? busCode, DiskIoctlInfo io, string model, string pnp)
    {
        bool nvme = busCode == 17 || (pnp ?? "").IndexOf("NVME", StringComparison.OrdinalIgnoreCase) >= 0 || (model ?? "").IndexOf("NVMe", StringComparison.OrdinalIgnoreCase) >= 0;
        if (msftMediaType == 3) return "HDD";
        if (msftMediaType == 4 || msftMediaType == 5) return nvme ? "NVMe" : "SSD";
        if (nvme) return "NVMe";
        if (io != null && io.IncursSeekPenalty.HasValue) return io.IncursSeekPenalty.Value ? "HDD" : "SSD";
        if ((model ?? "").IndexOf("SSD", StringComparison.OrdinalIgnoreCase) >= 0) return "SSD";
        return "Unknown";
    }

    private void ReadPartitions(IWmiObject driveRow, PhysicalDiskInfo disk, Dictionary<string, string> letterToDisk)
    {
        IReadOnlyList<IWmiObject> parts;
        try { parts = driveRow.GetRelated("Win32_DiskPartition"); }
        catch (Exception) { return; }
        foreach (var p in parts)
        {
            var info = new PartitionInfo
            {
                Index = p.Int("Index"),
                Type = p.Str("Type"),
                SizeBytes = p.Long("Size"),
                StartOffsetBytes = p.Long("StartingOffset"),
                IsBootPartition = p.Bool("BootPartition"),
                IsPrimary = p.Bool("PrimaryPartition"),
                DriveLetters = new List<string>()
            };
            try
            {
                foreach (var ld in p.GetRelated("Win32_LogicalDisk"))
                {
                    var letter = ld.Str("DeviceID");
                    if (letter == null) continue;
                    info.DriveLetters.Add(letter);
                    letterToDisk[letter] = disk.DeviceId;
                }
            }
            catch (Exception) { }
            disk.Partitions.Add(info);
        }
    }

    private List<VolumeInfo> ReadVolumes(Dictionary<string, string> letterToDisk)
    {
        var systemDrive = Environment.GetEnvironmentVariable("SystemDrive");
        var list = new List<VolumeInfo>();
        // DriveType 2 = removable, 3 = fixed. Network drives (4) are excluded on purpose: a disconnected share can hang WMI.
        foreach (var v in _wmi.Query("SELECT DeviceID, VolumeName, FileSystem, Size, FreeSpace, DriveType FROM Win32_LogicalDisk WHERE DriveType = 2 OR DriveType = 3"))
        {
            var letter = v.Str("DeviceID");
            if (letter == null) continue;
            var total = v.Long("Size");
            var free = v.Long("FreeSpace");
            var vol = new VolumeInfo
            {
                DriveLetter = letter,
                Label = v.Str("VolumeName"),
                FileSystem = v.Str("FileSystem"),
                DriveType = v.Int("DriveType") == 2 ? "Removable" : "Fixed",
                TotalBytes = total,
                FreeBytes = free,
                IsSystemDrive = systemDrive != null ? string.Equals(letter, systemDrive, StringComparison.OrdinalIgnoreCase) : (bool?)null
            };
            if (total > 0 && free.HasValue)
            {
                vol.UsedBytes = total.Value - free.Value;
                vol.UsedPercent = Num.Round(100.0 * vol.UsedBytes.Value / total.Value);
            }
            string diskId;
            if (letterToDisk.TryGetValue(letter, out diskId)) vol.PhysicalDiskId = diskId;
            list.Add(vol);
        }
        return list;
    }
}
