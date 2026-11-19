using System;
using System.Runtime.InteropServices;
using System.Text;
using Agent.Core;

namespace Agent.Collectors.Platform;

/// <summary>
/// Native Wifi API (wlanapi.dll, Vista+). Reads connection attributes only: SSID, BSSID, signal, rates, auth/cipher algorithm ids.
/// It never calls WlanGetProfile and never requests key material, so Wi-Fi passwords cannot be exposed by this agent.
/// </summary>
public sealed class WifiApi : IWifiApi
{
    private const int WLAN_CLIENT_VERSION_VISTA = 2;
    private const int wlan_intf_opcode_current_connection = 7;

    [DllImport("wlanapi.dll")] private static extern int WlanOpenHandle(int clientVersion, IntPtr reserved, out int negotiated, out IntPtr handle);
    [DllImport("wlanapi.dll")] private static extern int WlanCloseHandle(IntPtr handle, IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern int WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] private static extern int WlanQueryInterface(IntPtr handle, ref Guid iface, int opcode, IntPtr reserved, out int size, out IntPtr data, IntPtr type);
    [DllImport("wlanapi.dll")] private static extern int WlanGetNetworkBssList(IntPtr handle, ref Guid iface, IntPtr ssid, int bssType, bool security, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(IntPtr p);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WLAN_INTERFACE_INFO { public Guid Guid; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Description; public int State; }

    [StructLayout(LayoutKind.Sequential)] private struct DOT11_SSID { public uint Length; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Ssid; }
    [StructLayout(LayoutKind.Sequential)]
    private struct WLAN_ASSOCIATION_ATTRIBUTES
    {
        public DOT11_SSID Ssid; public int BssType; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] public byte[] Bssid;
        public int PhyType; public uint PhyIndex; public uint SignalQuality; public uint RxRate; public uint TxRate;
    }
    [StructLayout(LayoutKind.Sequential)] private struct WLAN_SECURITY_ATTRIBUTES { public bool SecurityEnabled; public bool OneXEnabled; public int AuthAlgorithm; public int CipherAlgorithm; }
    [StructLayout(LayoutKind.Sequential)]
    private struct WLAN_CONNECTION_ATTRIBUTES { public int State; public int Mode; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string ProfileName; public WLAN_ASSOCIATION_ATTRIBUTES Association; public WLAN_SECURITY_ATTRIBUTES Security; }

    public WifiSnapshot Read()
    {
        var snap = new WifiSnapshot();
        IntPtr handle = IntPtr.Zero;
        try
        {
            int negotiated;
            int rc = WlanOpenHandle(WLAN_CLIENT_VERSION_VISTA, IntPtr.Zero, out negotiated, out handle);
            if (rc != 0) { return snap; }                        // ERROR_SERVICE_NOT_ACTIVE (1062): WLAN AutoConfig is stopped / no Wi-Fi hardware
            snap.WlanServiceAvailable = true;

            IntPtr listPtr;
            if (WlanEnumInterfaces(handle, IntPtr.Zero, out listPtr) != 0) return snap;
            try
            {
                int count = Marshal.ReadInt32(listPtr, 0);
                if (count < 1) return snap;
                int size = Marshal.SizeOf(typeof(WLAN_INTERFACE_INFO));
                var info = (WLAN_INTERFACE_INFO)Marshal.PtrToStructure(new IntPtr(listPtr.ToInt64() + 8), typeof(WLAN_INTERFACE_INFO));
                snap.AdapterPresent = true;
                snap.AdapterGuid = info.Guid.ToString();
                snap.AdapterDescription = info.Description;
                snap.InterfaceState = StateName(info.State);
                if (info.State != 1) return snap;                // 1 = connected
                var guid = info.Guid;

                int dataSize; IntPtr data;
                if (WlanQueryInterface(handle, ref guid, wlan_intf_opcode_current_connection, IntPtr.Zero, out dataSize, out data, IntPtr.Zero) != 0) return snap;
                try
                {
                    var c = (WLAN_CONNECTION_ATTRIBUTES)Marshal.PtrToStructure(data, typeof(WLAN_CONNECTION_ATTRIBUTES));
                    snap.Connected = true;
                    snap.Ssid = Encoding.UTF8.GetString(c.Association.Ssid.Ssid, 0, (int)Math.Min(32, c.Association.Ssid.Length));
                    snap.Bssid = BitConverter.ToString(c.Association.Bssid).Replace('-', ':').ToLowerInvariant();
                    snap.SignalQuality = (int)c.Association.SignalQuality;
                    snap.RxRateKbps = c.Association.RxRate; snap.TxRateKbps = c.Association.TxRate;
                    snap.PhyType = c.Association.PhyType;
                    snap.AuthAlgorithm = c.Security.AuthAlgorithm; snap.CipherAlgorithm = c.Security.CipherAlgorithm; snap.OneXEnabled = c.Security.OneXEnabled;
                }
                finally { WlanFreeMemory(data); }

                // RSSI and centre frequency come from the BSS list entry that matches the connected BSSID
                IntPtr bssPtr;
                if (WlanGetNetworkBssList(handle, ref guid, IntPtr.Zero, 3, false, IntPtr.Zero, out bssPtr) == 0)
                {
                    try { ReadBss(bssPtr, snap); } finally { WlanFreeMemory(bssPtr); }
                }
            }
            finally { WlanFreeMemory(listPtr); }
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        catch (Exception) { }
        finally { if (handle != IntPtr.Zero) WlanCloseHandle(handle, IntPtr.Zero); }
        return snap;
    }

    // WLAN_BSS_LIST: dwTotalSize(4) dwNumberOfItems(4) then WLAN_BSS_ENTRY[] (360 bytes each on Vista+)
    private static void ReadBss(IntPtr list, WifiSnapshot snap)
    {
        const int entrySize = 360;
        int items = Marshal.ReadInt32(list, 4);
        for (int i = 0; i < items; i++)
        {
            var e = new IntPtr(list.ToInt64() + 8 + (long)i * entrySize);
            var mac = new byte[6];
            Marshal.Copy(new IntPtr(e.ToInt64() + 40), mac, 0, 6);      // dot11Bssid: after DOT11_SSID (36 bytes) and uPhyId (4)
            if (BitConverter.ToString(mac).Replace('-', ':').ToLowerInvariant() != snap.Bssid) continue;
            snap.Rssi = Marshal.ReadInt32(e, 56);                        // lRssi
            snap.FrequencyKhz = Marshal.ReadInt32(e, 92);                // ulChCenterFrequency (kHz)
            return;
        }
    }

    private static string StateName(int s)
    {
        switch (s) { case 1: return "connected"; case 2: return "ad_hoc"; case 3: return "disconnecting"; case 4: return "disconnected"; case 5: return "associating"; case 6: return "discovering"; case 7: return "authenticating"; default: return "not_ready"; }
    }
}
