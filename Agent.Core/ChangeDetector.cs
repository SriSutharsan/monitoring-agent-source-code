using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Agent.Models;

namespace Agent.Core;

public class InventoryItem
{
    public string Kind { get; set; }
    public string Key { get; set; }
    /// <summary>Stable human name (no volatile detail).</summary>
    public string Name { get; set; }
    /// <summary>Name plus version/detail used in summaries.</summary>
    public string Display { get; set; }
    /// <summary>Hash over the fields whose change counts as a modification.</summary>
    public string Fingerprint { get; set; }
    public string Json { get; set; }
}

/// <summary>Turns payloads into keyed inventory items. Volatile fields (free space, signal, charge) are excluded from fingerprints.</summary>
public static class InventoryItems
{
    public static List<InventoryItem> Extract(string category, object payload)
    {
        switch (category)
        {
            case Categories.Hardware:
                return FromHardware(payload as HardwareInventory);
            case Categories.Peripherals:
                return FromPeripherals(payload as HardwareInventory);
            case Categories.Storage:
                return FromStorage(payload as StorageInventory);
            case Categories.NetworkConfig:
                return FromNetwork(payload as NetworkInfo);
            case Categories.Software:
                return FromSoftware(payload as SoftwareInventory);
            case Categories.Device:
                return FromDevice(payload as DeviceInfo);
            case Categories.Security:
                return FromSecurity(payload as SecurityStatus);
            default:
                return null;
        }
    }

    private static string Fp(params object[] parts)
    {
        return Hashing.Short(string.Join("|", parts.Select(p => p == null ? "" : Convert.ToString(p, CultureInfo.InvariantCulture))), 20);
    }

    private static InventoryItem Item(string kind, string key, string name, string display, string fp, object json)
    {
        return new InventoryItem { Kind = kind, Key = key, Name = name, Display = display ?? name, Fingerprint = fp, Json = Json.Serialize(json) };
    }

    private static List<InventoryItem> FromHardware(HardwareInventory h)
    {
        if (h == null) return null;
        var r = new List<InventoryItem>();
        if (h.Processor != null)
            r.Add(Item("cpu", "cpu", h.Processor.Name, h.Processor.Name,
                Fp(h.Processor.Name, h.Processor.Cores, h.Processor.LogicalProcessors, h.Processor.Sockets, h.Processor.Manufacturer), h.Processor));
        if (h.Memory != null && h.Memory.Modules != null)
        {
            long total = 0;
            foreach (var m in h.Memory.Modules)
            {
                total += m.CapacityBytes ?? 0;
                var key = (m.Slot ?? "?") + "|" + (m.SerialNumber ?? m.PartNumber ?? "?");
                var gb = m.CapacityBytes.HasValue ? (m.CapacityBytes.Value / 1073741824.0).ToString("0.#", CultureInfo.InvariantCulture) + " GB" : "?";
                var disp = string.Format(CultureInfo.InvariantCulture, "{0} {1} {2} {3}", m.Slot, gb, m.Type, m.Manufacturer).Trim();
                r.Add(Item("memory_module", key, "RAM " + m.Slot, disp,
                    Fp(m.Slot, m.CapacityBytes, m.PartNumber, m.SerialNumber, m.Manufacturer, m.Type, m.SpeedMhz), m));
            }
            if (total > 0)
                r.Add(Item("memory_total", "ram", "Installed RAM", FormatGb(total), Fp(total), new { totalBytes = total }));
        }
        if (h.Motherboard != null)
            r.Add(Item("motherboard", "board", "Motherboard", (h.Motherboard.Manufacturer + " " + h.Motherboard.Product).Trim(),
                Fp(h.Motherboard.Manufacturer, h.Motherboard.Product, h.Motherboard.SerialNumber), h.Motherboard));
        if (h.Bios != null)
            r.Add(Item("bios", "bios", "BIOS", (h.Bios.Manufacturer + " " + h.Bios.Version).Trim(),
                Fp(h.Bios.Manufacturer, h.Bios.Version, h.Bios.ReleaseDateUtc), h.Bios));
        if (h.Gpus != null)
            foreach (var g in h.Gpus)
                r.Add(Item("gpu", g.Name ?? "gpu", g.Name, g.Name, Fp(g.Name, g.Manufacturer, g.DriverVersion), g));
        if (h.Monitors != null)
            foreach (var m in h.Monitors)
                r.Add(Item("monitor", m.InstanceId ?? (m.Model + "|" + m.SerialNumber), "Monitor " + m.Model,
                    (m.Manufacturer + " " + m.Model).Trim(), Fp(m.Manufacturer, m.Model, m.SerialNumber), m));
        if (h.Batteries != null)
            foreach (var b in h.Batteries)
                r.Add(Item("battery", (b.Name ?? "battery") + "|" + b.SerialNumber, "Battery " + b.Name,
                    (b.Manufacturer + " " + b.Name).Trim(), Fp(b.Manufacturer, b.Name, b.SerialNumber, b.DesignCapacityMwh), b));
        return r;
    }

    private static List<InventoryItem> FromPeripherals(HardwareInventory h)
    {
        if (h == null || h.Peripherals == null) return null;
        return h.Peripherals.Select(p => Item("peripheral", p.DeviceId ?? p.Name, p.Name,
            (p.Name + (string.IsNullOrEmpty(p.Manufacturer) ? "" : " (" + p.Manufacturer + ")")),
            Fp(p.Name, p.Manufacturer, p.DeviceClass), p)).ToList();
    }

    private static List<InventoryItem> FromStorage(StorageInventory s)
    {
        if (s == null) return null;
        var r = new List<InventoryItem>();
        if (s.Disks != null)
            foreach (var d in s.Disks)
            {
                var parts = d.Partitions == null ? "" : string.Join(",", d.Partitions.Select(p => p.Index + ":" + p.SizeBytes));
                r.Add(Item("disk", !string.IsNullOrEmpty(d.SerialNumber) ? d.SerialNumber : (d.Model + "|" + d.SizeBytes + "|" + d.Index),
                    d.Model, d.Model + " " + FormatGb(d.SizeBytes ?? 0),
                    Fp(d.Model, d.SerialNumber, d.SizeBytes, d.MediaType, d.BusType, parts), d));
            }
        if (s.Volumes != null)
            foreach (var v in s.Volumes.Where(v => v.DriveType == "Fixed" || v.DriveType == "Removable"))
                r.Add(Item("volume", v.DriveLetter, "Volume " + v.DriveLetter, v.DriveLetter + " " + v.FileSystem + " " + FormatGb(v.TotalBytes ?? 0),
                    Fp(v.FileSystem, v.TotalBytes, v.DriveType), v));
        return r;
    }

    private static List<InventoryItem> FromNetwork(NetworkInfo n)
    {
        if (n == null || n.Adapters == null) return null;
        var r = new List<InventoryItem>();
        foreach (var a in n.Adapters)
        {
            var key = a.MacAddress ?? a.Id ?? a.Name;
            r.Add(Item("network_adapter", key, a.Name, a.Name + " (" + a.Description + ")",
                Fp(a.Description, a.Manufacturer, a.AdapterType, a.DriverVersion, a.IsPhysical), a));
            r.Add(Item("ip_config", key, "IP configuration " + a.Name,
                a.Name + ": " + string.Join(",", a.IPv4Addresses ?? new List<string>()),
                Fp(Join(a.IPv4Addresses), Join(a.DefaultGateways), Join(a.DnsServers), a.DhcpEnabled),
                new { a.Name, a.IPv4Addresses, a.DefaultGateways, a.DnsServers, a.DhcpEnabled }));
        }
        return r;
    }

    private static string Join(List<string> l)
    {
        return l == null ? "" : string.Join(",", l);
    }

    private static List<InventoryItem> FromSoftware(SoftwareInventory s)
    {
        if (s == null) return null;
        var r = new List<InventoryItem>();
        if (s.Applications != null)
            foreach (var a in s.Applications)
                r.Add(Item("application", a.Key, a.Name, (a.Name + " " + a.Version).Trim(), Fp(a.Version, a.Publisher), a));
        if (s.Updates != null)
            foreach (var u in s.Updates)
                r.Add(Item("windows_update", u.Id, u.Id, u.Id + " " + u.Description, Fp(u.InstalledOnUtc), u));
        if (s.Os != null)
            r.Add(Item("os", "os", "Operating system", s.Os.Caption + " " + s.Os.DisplayVersion + " (build " + s.Os.BuildNumber + "." + s.Os.Revision + ")",
                Fp(s.Os.Caption, s.Os.Edition, s.Os.BuildNumber, s.Os.Revision, s.Os.DisplayVersion), s.Os));
        return r;
    }

    private static List<InventoryItem> FromDevice(DeviceInfo d)
    {
        if (d == null) return null;
        var r = new List<InventoryItem>
        {
            Item("device", "device", "Device identity", d.Hostname + " (" + d.Manufacturer + " " + d.Model + ")",
                Fp(d.Hostname, d.Manufacturer, d.Model, d.SerialNumber, d.SystemUuid, d.Domain), new { d.Hostname, d.Manufacturer, d.Model, d.SerialNumber, d.SystemUuid, d.Domain, d.DomainType })
        };
        if (!string.IsNullOrEmpty(d.AssignedUser))
            r.Add(Item("assigned_user", "user", "Assigned user", d.AssignedUser, Fp(d.AssignedUser), new { d.AssignedUser }));
        return r;
    }

    private static List<InventoryItem> FromSecurity(SecurityStatus s)
    {
        if (s == null) return null;
        var settings = new List<KeyValuePair<string, string>>();
        Action<string, object> add = (k, v) => settings.Add(new KeyValuePair<string, string>(k, v == null ? "unknown" : Convert.ToString(v, CultureInfo.InvariantCulture)));
        if (s.Antivirus != null && s.Antivirus.Defender != null)
        {
            add("defender.antivirus_enabled", s.Antivirus.Defender.AntivirusEnabled);
            add("defender.realtime_protection", s.Antivirus.Defender.RealTimeProtectionEnabled);
        }
        if (s.Firewall != null)
        {
            add("firewall.domain", s.Firewall.DomainProfileEnabled);
            add("firewall.private", s.Firewall.PrivateProfileEnabled);
            add("firewall.public", s.Firewall.PublicProfileEnabled);
        }
        add("secure_boot", s.SecureBoot);
        if (s.Tpm != null) { add("tpm.present", s.Tpm.Present); add("tpm.enabled", s.Tpm.Enabled); }
        if (s.Uac != null) add("uac.level", s.Uac.Level);
        if (s.Encryption != null && s.Encryption.Volumes != null)
            foreach (var v in s.Encryption.Volumes) add("bitlocker." + v.DriveLetter, v.ProtectionStatus);
        return settings.Select(kv => Item("security_setting", kv.Key, kv.Key, kv.Key + " = " + kv.Value, Fp(kv.Value), new { setting = kv.Key, value = kv.Value })).ToList();
    }

    public static string FormatGb(long bytes)
    {
        return (bytes / 1073741824.0).ToString("0.#", CultureInfo.InvariantCulture) + " GB";
    }
}

public sealed class ChangeDetector
{
    private readonly ISnapshotStore _store;

    public ChangeDetector(ISnapshotStore store)
    {
        _store = store;
    }

    public IReadOnlyList<ChangeRecord> Detect(string category, object payload, DateTime nowUtc)
    {
        var items = InventoryItems.Extract(category, payload);
        if (items == null) return new ChangeRecord[0];

        // de-duplicate keys defensively (two identical RAM sticks etc.)
        var seen = new HashSet<string>();
        foreach (var it in items)
        {
            var original = it.Key;
            int n = 1;
            while (!seen.Add(it.Kind + "\u0001" + it.Key)) it.Key = original + "#" + (++n);
        }

        var previousJson = _store.GetJson(category);
        var newJson = Json.Serialize(items);
        List<InventoryItem> previous = null;
        if (!string.IsNullOrEmpty(previousJson)) Json.TryDeserialize(previousJson, out previous);

        _store.Save(category, newJson, Hashing.Sha256Hex(newJson), nowUtc);
        if (previous == null) return new ChangeRecord[0];     // first run = baseline, no change records

        var before = previous.ToDictionary(i => i.Kind + "\u0001" + i.Key, i => i);
        var after = items.ToDictionary(i => i.Kind + "\u0001" + i.Key, i => i);
        var result = new List<ChangeRecord>();

        foreach (var kv in after)
        {
            InventoryItem old;
            if (!before.TryGetValue(kv.Key, out old))
                result.Add(Make(category, kv.Value, null, "added", nowUtc));
            else if (old.Fingerprint != kv.Value.Fingerprint)
                result.Add(Make(category, kv.Value, old, "modified", nowUtc));
        }
        foreach (var kv in before)
            if (!after.ContainsKey(kv.Key))
                result.Add(Make(category, kv.Value, kv.Value, "removed", nowUtc));

        // synthetic summaries
        if (category == Categories.Storage
            && result.Any(c => c.ItemKind == "disk" && c.ChangeType == "added")
            && result.Any(c => c.ItemKind == "disk" && c.ChangeType == "removed"))
        {
            result.Add(new ChangeRecord
            {
                Category = "storage", ChangeClass = "storage_replaced", ChangeType = "modified", ItemKind = "disk", ItemKey = "replacement",
                DisplayName = "Storage device replaced", DetectedUtc = nowUtc,
                Summary = "A physical disk was removed and another was added: " +
                          string.Join("; ", result.Where(c => c.ItemKind == "disk").Select(c => c.Summary))
            });
        }
        return result;
    }

    private static ChangeRecord Make(string category, InventoryItem current, InventoryItem old, string type, DateTime now)
    {
        var cls = ClassOf(current.Kind, type, old, current);
        var rec = new ChangeRecord
        {
            Category = CategoryGroup(category),
            ChangeClass = cls,
            ChangeType = type,
            ItemKind = current.Kind,
            ItemKey = current.Key,
            DisplayName = current.Name,
            DetectedUtc = now,
            Before = type == "added" ? null : (old != null ? old.Json : null),
            After = type == "removed" ? null : current.Json
        };
        rec.Summary = Summarize(type, current, old, cls);
        return rec;
    }

    private static string CategoryGroup(string category)
    {
        if (category.StartsWith("hardware", StringComparison.Ordinal)) return "hardware";
        if (category.StartsWith("network", StringComparison.Ordinal)) return "network";
        return category;
    }

    private static string ClassOf(string kind, string type, InventoryItem old, InventoryItem current)
    {
        switch (kind)
        {
            case "memory_module": return type == "added" ? "ram_added" : type == "removed" ? "ram_removed" : "ram_changed";
            case "memory_total": return "ram_capacity_changed";
            case "peripheral": return type == "added" ? "hardware_connected" : type == "removed" ? "hardware_disconnected" : "hardware_changed";
            case "disk": return type == "added" ? "storage_added" : type == "removed" ? "storage_removed" : "storage_changed";
            case "volume": return type == "added" ? "volume_added" : type == "removed" ? "volume_removed" : "volume_changed";
            case "network_adapter": return type == "added" ? "network_adapter_added" : type == "removed" ? "network_adapter_removed" : "network_adapter_changed";
            case "ip_config": return "network_config_changed";
            case "application":
                if (type == "added") return "software_installed";
                if (type == "removed") return "software_uninstalled";
                return old != null && old.Display != current.Display ? "software_version_changed" : "software_modified";
            case "windows_update": return type == "added" ? "windows_update_installed" : "windows_update_removed";
            case "os": return "os_changed";
            case "device": return "device_identity_changed";
            case "assigned_user": return "assigned_user_changed";
            case "security_setting": return "security_config_changed";
            case "bios": return "bios_changed";
            default: return type == "added" ? "hardware_added" : type == "removed" ? "hardware_removed" : "hardware_changed";
        }
    }

    private static string Summarize(string type, InventoryItem current, InventoryItem old, string cls)
    {
        switch (type)
        {
            case "added": return "Added: " + current.Display;
            case "removed": return "Removed: " + current.Display;
            default:
                return "Changed: " + (old != null ? old.Display : "?") + " -> " + current.Display;
        }
    }
}
