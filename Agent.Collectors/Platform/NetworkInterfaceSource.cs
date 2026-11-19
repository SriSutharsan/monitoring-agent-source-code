using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Agent.Core;

namespace Agent.Collectors.Platform;

public sealed class NetworkInterfaceSource : INetworkInterfaceSource
{
    public IReadOnlyList<RawAdapter> GetAdapters()
    {
        var list = new List<RawAdapter>();
        NetworkInterface[] all;
        try { all = NetworkInterface.GetAllNetworkInterfaces(); } catch (Exception) { return list; }
        foreach (var nic in all)
        {
            try
            {
                var props = nic.GetIPProperties();
                var raw = new RawAdapter
                {
                    Id = nic.Id, Name = nic.Name, Description = nic.Description,
                    Mac = FormatMac(nic.GetPhysicalAddress()),
                    InterfaceType = nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wireless80211"
                        : nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ? "Loopback"
                        : nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel ? "Tunnel"
                        : nic.NetworkInterfaceType == NetworkInterfaceType.Ppp ? "Ppp"
                        : nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet || nic.NetworkInterfaceType == NetworkInterfaceType.GigabitEthernet ? "Ethernet" : "Other",
                    IsUp = nic.OperationalStatus == OperationalStatus.Up,
                    SpeedBitsPerSecond = nic.Speed,
                    IPv4 = new List<string>(), IPv6 = new List<string>(), Masks = new List<string>(), Gateways = new List<string>(), Dns = new List<string>(),
                    DnsSuffix = props.DnsSuffix
                };
                foreach (var u in props.UnicastAddresses)
                {
                    if (u.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        raw.IPv4.Add(u.Address.ToString());
                        try { raw.Masks.Add(u.IPv4Mask == null ? null : u.IPv4Mask.ToString()); } catch (Exception) { raw.Masks.Add(null); }
                    }
                    else if (u.Address.AddressFamily == AddressFamily.InterNetworkV6 && !u.Address.IsIPv6LinkLocal) raw.IPv6.Add(u.Address.ToString());
                }
                foreach (var g in props.GatewayAddresses) if (g.Address.AddressFamily == AddressFamily.InterNetwork) raw.Gateways.Add(g.Address.ToString());
                foreach (var d in props.DnsAddresses) if (d.AddressFamily == AddressFamily.InterNetwork) raw.Dns.Add(d.ToString());
                if (nic.Supports(NetworkInterfaceComponent.IPv4))
                {
                    try
                    {
                        var v4 = props.GetIPv4Properties();
                        raw.DhcpEnabled = v4.IsDhcpEnabled;
                        var s = nic.GetIPv4Statistics();
                        raw.BytesReceived = s.BytesReceived; raw.BytesSent = s.BytesSent;
                        raw.DhcpServer = props.DhcpServerAddresses.Select(a => a.ToString()).FirstOrDefault();
                    }
                    catch (Exception) { }
                }
                list.Add(raw);
            }
            catch (Exception) { }
        }
        return list;
    }

    private static string FormatMac(PhysicalAddress a)
    {
        var b = a == null ? null : a.GetAddressBytes();
        return b == null || b.Length == 0 ? null : string.Join("-", b.Select(x => x.ToString("X2")).ToArray());
    }
}
