using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Agent.Core;
using Agent.Models;

namespace Agent.Collectors.Hardware;

/// <summary>
/// Lightweight 5-minute scan of connected USB / Bluetooth devices. The change detector diffs consecutive scans to report
/// "new hardware connected" and "hardware removed". Internal hubs and root controllers are ignored to keep the signal clean.
/// </summary>
public sealed class PeripheralsCollector : CollectorBase<HardwareInventory>
{
    private static readonly string[] IgnoredNameFragments =
    {
        "Root Hub", "Generic USB Hub", "Host Controller", "Composite Device", "USB Root", "Enumerator", "Bluetooth Device (RFCOMM",
        "Bluetooth Radio", "Microsoft Bluetooth", "Generic Bluetooth", "Bluetooth Device (Personal Area"
    };

    private readonly IWmiClient _wmi;

    public PeripheralsCollector(IWmiClient wmi)
    {
        _wmi = wmi;
    }

    public override string Category { get { return Categories.Peripherals; } }

    public override Task<HardwareInventory> CollectTypedAsync(CollectionContext ctx, CancellationToken ct)
    {
        return Task.FromResult(Collect());
    }

    public HardwareInventory Collect()
    {
        var list = new List<PeripheralInfo>();
        var rows = _wmi.Query(@"SELECT Name, PNPClass, Manufacturer, DeviceID, Status FROM Win32_PnPEntity WHERE DeviceID LIKE 'USB\\%' OR DeviceID LIKE 'BTHENUM\\%' OR DeviceID LIKE 'BTHLE\\%'");
        foreach (var r in rows)
        {
            var name = r.Str("Name");
            var id = r.Str("DeviceID");
            if (name == null || id == null) continue;
            if (IgnoredNameFragments.Any(f => name.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
            if (id.StartsWith(@"USB\ROOT_HUB", StringComparison.OrdinalIgnoreCase)) continue;
            // interface children of composite devices (MI_xx) duplicate their parent
            if (id.IndexOf("&MI_", StringComparison.OrdinalIgnoreCase) >= 0) continue;
            list.Add(new PeripheralInfo
            {
                Name = name,
                DeviceClass = r.Str("PNPClass"),
                Manufacturer = Vendor(r.Str("Manufacturer")),
                DeviceId = id,
                Connection = id.StartsWith("USB", StringComparison.OrdinalIgnoreCase) ? "usb" : "bluetooth",
                Status = r.Str("Status")
            });
        }
        return new HardwareInventory { Scope = "peripherals", Peripherals = list.OrderBy(p => p.DeviceId, StringComparer.OrdinalIgnoreCase).ToList() };
    }

    private static string Vendor(string v)
    {
        if (v == null || v.StartsWith("(Standard", StringComparison.OrdinalIgnoreCase) || v.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) && v.Contains("Generic")) return null;
        return v;
    }
}
