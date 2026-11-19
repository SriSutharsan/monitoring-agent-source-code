using System;
using System.Collections.Generic;
using System.Linq;
using Agent.Core;
using Agent.Models;

namespace Agent.Collectors.Platform;

/// <summary>Hardware identity used for registration and clone detection: SMBIOS UUID + serial + physical NIC MACs.</summary>
public sealed class DeviceFingerprintProvider : IDeviceFingerprintProvider
{
    private readonly IWmiClient _wmi;
    private readonly INetworkInterfaceSource _nics;

    public DeviceFingerprintProvider(IWmiClient wmi, INetworkInterfaceSource nics)
    {
        _wmi = wmi;
        _nics = nics;
    }

    public DeviceFingerprint Get()
    {
        var fp = new DeviceFingerprint { MacAddresses = new List<string>() };
        var prod = _wmi.Query("SELECT UUID, IdentifyingNumber, Vendor, Name FROM Win32_ComputerSystemProduct").FirstOrDefault();
        if (prod != null)
        {
            fp.SystemUuid = Convert.ToString(prod["UUID"]);
            fp.SerialNumber = Convert.ToString(prod["IdentifyingNumber"]);
            fp.Manufacturer = Convert.ToString(prod["Vendor"]);
            fp.Model = Convert.ToString(prod["Name"]);
        }
        if (string.IsNullOrWhiteSpace(fp.SerialNumber))
        {
            var bios = _wmi.Query("SELECT SerialNumber FROM Win32_BIOS").FirstOrDefault();
            if (bios != null) fp.SerialNumber = Convert.ToString(bios["SerialNumber"]);
        }
        foreach (var a in _nics.GetAdapters())
            if ((a.InterfaceType == "Ethernet" || a.InterfaceType == "Wireless80211") && !string.IsNullOrEmpty(a.Mac)
                && !(a.Description ?? "").ToLowerInvariant().Contains("virtual") && !(a.Description ?? "").ToLowerInvariant().Contains("vpn"))
                fp.MacAddresses.Add(a.Mac);
        fp.MacAddresses = fp.MacAddresses.Distinct().OrderBy(m => m).ToList();
        return fp;
    }
}
