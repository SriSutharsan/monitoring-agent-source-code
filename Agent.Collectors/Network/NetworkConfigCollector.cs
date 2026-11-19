using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Agent.Core;
using Agent.Models;

namespace Agent.Collectors.Network;

public static class AdapterClassifier
{
    private static readonly Regex VpnPattern = new Regex(
        @"vpn|tap-windows|wintun|wireguard|openvpn|anyconnect|forti|globalprotect|pulse secure|juniper|cisco|zscaler|nordlynx|sonicwall|checkpoint|ras |pptp|l2tp|sstp|ipsec",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex VirtualPattern = new Regex(
        @"virtual|vmware|hyper-v|vethernet|virtualbox|vbox|loopback|docker|wsl|npcap|teredo|isatap|6to4|bluetooth device \(personal|miniport|wan |kernel debug|hosted network|wi-fi direct",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsVpn(RawAdapter a)
    {
        var text = (a.Name ?? "") + " " + (a.Description ?? "");
        if (a.InterfaceType == "Ppp") return true;
        return VpnPattern.IsMatch(text) && !(a.InterfaceType == "Wireless80211");
    }

    public static string Classify(RawAdapter a)
    {
        var text = (a.Name ?? "") + " " + (a.Description ?? "");
        if (a.InterfaceType == "Loopback") return "Loopback";
        if (IsVpn(a)) return "VPN";
        if (a.InterfaceType == "Wireless80211") return "Wi-Fi";
        if (text.IndexOf("bluetooth", StringComparison.OrdinalIgnoreCase) >= 0) return "Bluetooth";
        if (a.InterfaceType == "Tunnel" || VirtualPattern.IsMatch(text)) return "Virtual";
        if (a.InterfaceType == "Ethernet") return "Ethernet";
        if (text.IndexOf("mobile", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("wwan", StringComparison.OrdinalIgnoreCase) >= 0 ||
            text.IndexOf("cellular", StringComparison.OrdinalIgnoreCase) >= 0) return "Mobile";
        return "Other";
    }
}

/// <summary>Adapters and IP configuration. Driver and vendor details come from WMI and are cached because they almost never change.</summary>
public sealed class NetworkConfigCollector : CollectorBase<NetworkInfo>
{
    private readonly INetworkInterfaceSource _source;
    private readonly IWmiClient _wmi;
    private readonly IClock _clock;
    private Dictionary<string, AdapterDetails> _details = new Dictionary<string, AdapterDetails>(StringComparer.OrdinalIgnoreCase);
    private DateTime _detailsAt = DateTime.MinValue;

    private sealed class AdapterDetails
    {
        public string Manufacturer, Model, DriverVersion, DriverProvider, Status;
        public DateTime? DriverDate;
        public bool? Physical;
    }

    public NetworkConfigCollector(INetworkInterfaceSource source, IWmiClient wmi, IClock clock)
    {
        _source = source;
        _wmi = wmi;
        _clock = clock;
    }

    public override string Category { get { return Categories.NetworkConfig; } }

    public override Task<NetworkInfo> CollectTypedAsync(CollectionContext ctx, CancellationToken ct)
    {
        return Task.FromResult(Collect());
    }

    public NetworkInfo Collect()
    {
        RefreshDetails();
        var adapters = new List<AdapterInfo>();
        foreach (var a in _source.GetAdapters())
        {
            if (a.InterfaceType == "Loopback") continue;
            AdapterDetails d;
            if (a.Mac == null || !_details.TryGetValue(NormalizeMac(a.Mac), out d)) d = null;
            var type = AdapterClassifier.Classify(a);
            adapters.Add(new AdapterInfo
            {
                Id = a.Id,
                Name = a.Name,
                Description = a.Description,
                Manufacturer = d != null ? d.Manufacturer : null,
                Model = a.Description,
                MacAddress = FormatMac(a.Mac),
                AdapterType = type,
                Status = a.IsUp ? "Up" : "Down",
                ConnectionStatus = a.IsUp ? "Connected" : "Disconnected",
                IsPhysical = d != null ? d.Physical : (type == "Ethernet" || type == "Wi-Fi" ? true : (bool?)null),
                DriverVersion = d != null ? d.DriverVersion : null,
                DriverDateUtc = d != null ? d.DriverDate : null,
                DriverProvider = d != null ? d.DriverProvider : null,
                SpeedBitsPerSecond = a.SpeedBitsPerSecond > 0 ? a.SpeedBitsPerSecond : (long?)null,
                IPv4Addresses = a.IPv4 ?? new List<string>(),
                IPv6Addresses = a.IPv6 ?? new List<string>(),
                SubnetMasks = a.Masks ?? new List<string>(),
                DefaultGateways = a.Gateways ?? new List<string>(),
                DnsServers = a.Dns ?? new List<string>(),
                DhcpEnabled = a.DhcpEnabled,
                DhcpServer = a.DhcpServer,
                DnsSuffix = a.DnsSuffix
            });
        }
        return new NetworkInfo
        {
            Scope = "configuration",
            Hostname = Environment.MachineName,
            PrimaryIPv4 = PrimaryIPv4(adapters),
            Adapters = adapters.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList()
        };
    }

    public static string PrimaryIPv4(IEnumerable<AdapterInfo> adapters)
    {
        var best = adapters.Where(a => a.Status == "Up" && a.IPv4Addresses.Count > 0 && a.DefaultGateways.Count > 0 &&
                                       a.AdapterType != "Virtual" && a.AdapterType != "VPN")
            .OrderByDescending(a => a.SpeedBitsPerSecond ?? 0).FirstOrDefault();
        if (best == null) best = adapters.FirstOrDefault(a => a.Status == "Up" && a.IPv4Addresses.Count > 0 && a.AdapterType != "Virtual");
        return best != null ? best.IPv4Addresses[0] : null;
    }

    public static string NormalizeMac(string mac)
    {
        return (mac ?? "").Replace(":", "").Replace("-", "").ToUpperInvariant();
    }

    public static string FormatMac(string mac)
    {
        var n = NormalizeMac(mac);
        if (n.Length != 12) return Text.NullIfEmpty(mac);
        return string.Join(":", Enumerable.Range(0, 6).Select(i => n.Substring(i * 2, 2)));
    }

    private void RefreshDetails()
    {
        if (_clock.UtcNow - _detailsAt < TimeSpan.FromHours(1) && _details.Count > 0) return;
        var map = new Dictionary<string, AdapterDetails>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var drivers = new Dictionary<string, IWmiObject>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in _wmi.Query("SELECT DeviceID, DriverVersion, DriverDate, DriverProviderName FROM Win32_PnPSignedDriver WHERE DeviceClass = 'NET'"))
            {
                var id = d.Str("DeviceID");
                if (id != null) drivers[id] = d;
            }
            foreach (var n in _wmi.Query("SELECT MACAddress, Manufacturer, PNPDeviceID, PhysicalAdapter FROM Win32_NetworkAdapter WHERE MACAddress IS NOT NULL"))
            {
                var mac = n.Str("MACAddress");
                if (mac == null) continue;
                var det = new AdapterDetails { Manufacturer = n.Str("Manufacturer"), Physical = n.Bool("PhysicalAdapter") };
                IWmiObject drv;
                var pnp = n.Str("PNPDeviceID");
                if (pnp != null && drivers.TryGetValue(pnp, out drv))
                {
                    det.DriverVersion = drv.Str("DriverVersion");
                    det.DriverDate = drv.Dmtf("DriverDate");
                    det.DriverProvider = drv.Str("DriverProviderName");
                }
                map[NormalizeMac(mac)] = det;
            }
            _details = map;
            _detailsAt = _clock.UtcNow;
        }
        catch (Exception)
        {
            _detailsAt = _clock.UtcNow;      // do not hammer WMI when it is unhealthy; try again next hour
        }
    }
}
