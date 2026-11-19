using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Agent.Core;
using Agent.Models;

namespace Agent.Collectors.Network;

/// <summary>
/// Wi-Fi status through the Native WiFi API (Windows 7+). Reads SSID, BSSID, signal, rate, channel and security type only.
/// It never reads saved profiles, pre-shared keys or credentials.
/// </summary>
public sealed class WifiCollector : CollectorBase<NetworkInfo>
{
    private readonly IWifiApi _api;
    private readonly IClock _clock;
    private string _sessionKey;
    private DateTime _sessionStartUtc;

    public WifiCollector(IWifiApi api, IClock clock)
    {
        _api = api;
        _clock = clock;
    }

    public override string Category { get { return Categories.Wifi; } }

    public override Task<NetworkInfo> CollectTypedAsync(CollectionContext ctx, CancellationToken ct)
    {
        return Task.FromResult(Collect());
    }

    public static string PhyName(int phy)
    {
        switch (phy)
        {
            case 4: return "802.11a"; case 5: return "802.11b"; case 6: return "802.11g"; case 7: return "802.11n"; case 8: return "802.11ac";
            case 9: return "802.11ad"; case 10: return "802.11ax"; case 11: return "802.11be"; default: return phy > 0 ? "phy-" + phy : null;
        }
    }

    public static string Standard(int phy, int? frequencyMhz)
    {
        switch (phy)
        {
            case 7: return "Wi-Fi 4";
            case 8: return "Wi-Fi 5";
            case 10: return frequencyMhz.HasValue && frequencyMhz.Value >= 5925 ? "Wi-Fi 6E" : "Wi-Fi 6";
            case 11: return "Wi-Fi 7";
            case 4: case 5: case 6: return "Legacy (802.11a/b/g)";
            default: return null;
        }
    }

    public static string Band(int? mhz)
    {
        if (!mhz.HasValue) return null;
        if (mhz.Value >= 2400 && mhz.Value < 2500) return "2.4 GHz";
        if (mhz.Value >= 4900 && mhz.Value < 5900) return "5 GHz";
        if (mhz.Value >= 5925 && mhz.Value <= 7125) return "6 GHz";
        return null;
    }

    public static int? Channel(int? mhz)
    {
        if (!mhz.HasValue) return null;
        int f = mhz.Value;
        if (f == 2484) return 14;
        if (f >= 2412 && f <= 2472) return (f - 2407) / 5;
        if (f >= 5160 && f <= 5885) return (f - 5000) / 5;
        if (f >= 5955 && f <= 7115) return (f - 5950) / 5;          // 6 GHz
        return null;
    }

    public static string AuthName(int algo, bool oneX)
    {
        switch (algo)
        {
            case 1: return "Open";
            case 2: return "WEP (shared key)";
            case 3: return "WPA-Enterprise";
            case 4: return "WPA-Personal";
            case 5: return "WPA-None";
            case 6: return "WPA2-Enterprise";
            case 7: return "WPA2-Personal";
            case 8: return "WPA3-Enterprise 192-bit";
            case 9: return "WPA3-Personal";
            case 10: return "OWE (Enhanced Open)";
            case 11: return "WPA3-Enterprise";
            default: return algo > 0 ? "auth-" + algo : (oneX ? "802.1X" : null);
        }
    }

    public static string CipherName(int c)
    {
        switch (c)
        {
            case 0: return "None"; case 1: return "WEP-40"; case 2: return "TKIP"; case 4: return "CCMP (AES)"; case 5: return "WEP-104";
            case 0x100: return "WPA-GCMP"; case 0x8: return "GCMP"; default: return c > 0 ? "cipher-" + c : null;
        }
    }

    public NetworkInfo Collect()
    {
        var snap = _api.Read();
        var wifi = new WifiInfo { AdapterPresent = snap.AdapterPresent, Connected = false, State = snap.InterfaceState };
        if (!snap.WlanServiceAvailable)
        {
            wifi.State = "wlan-service-unavailable";
        }
        else if (snap.AdapterPresent)
        {
            wifi.AdapterName = snap.AdapterDescription;
            wifi.AdapterModel = snap.AdapterDescription;
            wifi.Connected = snap.Connected;
            if (snap.Connected)
            {
                int? mhz = snap.FrequencyKhz.HasValue ? snap.FrequencyKhz.Value / 1000 : (int?)null;
                wifi.Ssid = snap.Ssid;
                wifi.Bssid = snap.Bssid;
                wifi.SignalQualityPercent = snap.SignalQuality;
                wifi.SignalStrengthDbm = snap.Rssi;
                wifi.FrequencyMhz = mhz;
                wifi.Channel = Channel(mhz);
                wifi.Band = Band(mhz);
                wifi.RadioType = PhyName(snap.PhyType);
                wifi.WifiStandard = Standard(snap.PhyType, mhz);
                wifi.ReceiveRateMbps = snap.RxRateKbps.HasValue ? snap.RxRateKbps.Value / 1000 : (long?)null;
                wifi.TransmitRateMbps = snap.TxRateKbps.HasValue ? snap.TxRateKbps.Value / 1000 : (long?)null;
                wifi.Authentication = AuthName(snap.AuthAlgorithm, snap.OneXEnabled);
                wifi.Cipher = CipherName(snap.CipherAlgorithm);

                var key = (snap.Ssid ?? "") + "|" + (snap.Bssid ?? "");
                var ssidKey = snap.Ssid ?? "";
                // duration is tracked per SSID so roaming between access points does not reset it
                if (_sessionKey != ssidKey) { _sessionKey = ssidKey; _sessionStartUtc = _clock.UtcNow; }
                wifi.ConnectedSinceUtc = _sessionStartUtc;
                wifi.ConnectionDurationSeconds = (long)(_clock.UtcNow - _sessionStartUtc).TotalSeconds;
            }
            else _sessionKey = null;
        }
        return new NetworkInfo { Scope = "wifi", Hostname = Environment.MachineName, Wifi = wifi };
    }
}
