using System;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Agent.Core;

namespace Agent.Collectors.Platform;

/// <summary>Latency probes. Only the targets configured by the administrator are contacted (ICMP echo / TCP connect), nothing else.</summary>
public sealed class NetworkProbe : INetworkProbe
{
    public PingSample Ping(string host, int timeoutMs)
    {
        try
        {
            using (var p = new Ping())
            {
                var r = p.Send(host, timeoutMs);
                return new PingSample { Success = r.Status == IPStatus.Success, RoundTripMs = r.Status == IPStatus.Success ? r.RoundtripTime : 0 };
            }
        }
        catch (Exception) { return new PingSample { Success = false }; }
    }

    public bool TcpConnect(string host, int port, int timeoutMs)
    {
        try
        {
            using (var c = new TcpClient())
            {
                var ar = c.BeginConnect(host, port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) return false;
                c.EndConnect(ar);
                return c.Connected;
            }
        }
        catch (Exception) { return false; }
    }

    /// <summary>Windows Network List Manager (Vista+) knows whether Windows believes the internet is reachable.</summary>
    public bool? IsInternetConnected()
    {
        try
        {
            var t = Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"));   // NetworkListManager
            if (t == null) return null;
            object nlm = Activator.CreateInstance(t);
            try { return (bool)t.InvokeMember("IsConnectedToInternet", System.Reflection.BindingFlags.GetProperty, null, nlm, null); }
            finally { if (System.Runtime.InteropServices.Marshal.IsComObject(nlm)) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(nlm); }
        }
        catch (Exception) { return null; }
    }
}
