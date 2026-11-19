using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Agent.Collectors.Network;
using Agent.Core;
using Agent.Models;

namespace Agent.Collectors.Performance;

/// <summary>
/// Throughput, utilization, latency, packet loss, internet reachability, VPN state and connect/disconnect history.
/// Totals are the adapter byte counters since the adapter came up (typically since boot).
/// </summary>
public sealed class NetworkPerfCollector : CollectorBase<PerformanceMetrics>
{
    private const int PingsPerTarget = 4;

    private readonly INetworkInterfaceSource _source;
    private readonly INetworkProbe _probe;
    private readonly IClock _clock;
    private readonly string _apiHost;
    private readonly int _apiPort;
    private Dictionary<string, KeyValuePair<long, long>> _lastBytes = new Dictionary<string, KeyValuePair<long, long>>();
    private DateTime _lastSampleUtc;
    private Dictionary<string, KeyValuePair<string, bool>> _lastAdapters;
    private bool? _lastInternet;

    public NetworkPerfCollector(INetworkInterfaceSource source, INetworkProbe probe, IClock clock, string apiHost, int apiPort)
    {
        _source = source;
        _probe = probe;
        _clock = clock;
        _apiHost = apiHost;
        _apiPort = apiPort;
    }

    public override string Category { get { return Categories.PerfNetwork; } }
    public override TimeSpan Timeout { get { return TimeSpan.FromSeconds(60); } }

    public override Task<PerformanceMetrics> CollectTypedAsync(CollectionContext ctx, CancellationToken ct)
    {
        return Task.Run(() => Collect(ctx.Config.NetworkProbes ?? new NetworkProbeConfig()), ct);
    }

    private static bool CountsForThroughput(RawAdapter a)
    {
        if (!a.IsUp || a.InterfaceType == "Loopback") return false;
        var t = AdapterClassifier.Classify(a);
        return t == "Ethernet" || t == "Wi-Fi" || t == "Mobile";
    }

    public PerformanceMetrics Collect(NetworkProbeConfig probes)
    {
        var now = _clock.UtcNow;
        var adapters = _source.GetAdapters();
        var net = new NetworkPerfMetrics { VpnAdapters = new List<string>(), Events = new List<NetworkEvent>() };

        // ---- throughput
        var counted = adapters.Where(CountsForThroughput).ToList();
        double seconds = _lastSampleUtc == default(DateTime) ? 0 : (now - _lastSampleUtc).TotalSeconds;
        long dRx = 0, dTx = 0;
        var bytes = new Dictionary<string, KeyValuePair<long, long>>();
        foreach (var a in counted)
        {
            bytes[a.Id] = new KeyValuePair<long, long>(a.BytesReceived, a.BytesSent);
            KeyValuePair<long, long> prev;
            if (seconds > 0 && _lastBytes.TryGetValue(a.Id, out prev))
            {
                if (a.BytesReceived >= prev.Key) dRx += a.BytesReceived - prev.Key;
                if (a.BytesSent >= prev.Value) dTx += a.BytesSent - prev.Value;
            }
        }
        _lastBytes = bytes;
        _lastSampleUtc = now;
        net.TotalDownloadedBytes = counted.Sum(a => a.BytesReceived);
        net.TotalUploadedBytes = counted.Sum(a => a.BytesSent);
        if (seconds > 0)
        {
            net.DownloadBytesPerSec = Num.Round(dRx / seconds, 0);
            net.UploadBytesPerSec = Num.Round(dTx / seconds, 0);
            long speed = counted.Where(a => a.SpeedBitsPerSecond > 0).Select(a => a.SpeedBitsPerSecond).DefaultIfEmpty(0).Max();
            if (speed > 0) net.UtilizationPercent = Num.Round(Num.Clamp(Math.Max(net.DownloadBytesPerSec, net.UploadBytesPerSec) * 8.0 / speed * 100.0, 0, 100));
        }

        // ---- identity of the active adapter + VPN
        var active = counted.Where(a => a.Gateways != null && a.Gateways.Count > 0).OrderByDescending(a => a.SpeedBitsPerSecond).FirstOrDefault()
                     ?? counted.OrderByDescending(a => a.SpeedBitsPerSecond).FirstOrDefault();
        net.ActiveAdapter = active != null ? active.Name : null;
        foreach (var a in adapters.Where(x => x.IsUp && AdapterClassifier.IsVpn(x))) net.VpnAdapters.Add(a.Name);
        net.VpnConnected = net.VpnAdapters.Count > 0;

        // ---- reachability, latency, loss
        var targets = new List<string>();
        if (probes.PingTargets != null) targets.AddRange(probes.PingTargets.Where(t => !string.IsNullOrWhiteSpace(t)));
        if (targets.Count == 0 && !string.IsNullOrEmpty(_apiHost)) targets.Add(_apiHost);
        string gateway = active != null && active.Gateways != null ? active.Gateways.FirstOrDefault(g => g.IndexOf(':') < 0) : null;

        if (net.ActiveAdapter != null)
        {
            var external = MeasureTargets(targets, probes.PingTimeoutMs > 0 ? probes.PingTimeoutMs : 1500);
            if (external != null)
            {
                net.LatencyMs = external.Latency;
                net.PacketLossPercent = external.Loss;
                net.LatencyTarget = external.Target;
            }
            else if (probes.PingGateway && gateway != null)
            {
                var g = MeasureTargets(new List<string> { gateway }, probes.PingTimeoutMs > 0 ? probes.PingTimeoutMs : 1500);
                if (g != null) { net.LatencyMs = g.Latency; net.PacketLossPercent = g.Loss; net.LatencyTarget = "gateway"; }
            }
            net.ApiReachable = !string.IsNullOrEmpty(_apiHost) && _probe.TcpConnect(_apiHost, _apiPort, 2500);
        }
        var nla = _probe.IsInternetConnected();
        net.InternetReachable = nla ?? (net.ApiReachable || (net.LatencyMs.HasValue && net.LatencyTarget != "gateway"));
        if (net.ActiveAdapter == null) net.InternetReachable = false;

        // ---- connection / disconnection history
        var state = adapters.Where(a => a.InterfaceType != "Loopback").ToDictionary(a => a.Id, a => new KeyValuePair<string, bool>(a.Name, a.IsUp));
        if (_lastAdapters != null)
        {
            foreach (var kv in state)
            {
                KeyValuePair<string, bool> old;
                if (!_lastAdapters.TryGetValue(kv.Key, out old))
                    net.Events.Add(new NetworkEvent { TimestampUtc = now, Type = "adapter_added", Adapter = kv.Value.Key, Detail = kv.Value.Value ? "up" : "down" });
                else if (old.Value != kv.Value.Value)
                    net.Events.Add(new NetworkEvent { TimestampUtc = now, Type = kv.Value.Value ? "adapter_up" : "adapter_down", Adapter = kv.Value.Key });
            }
            foreach (var kv in _lastAdapters)
                if (!state.ContainsKey(kv.Key))
                    net.Events.Add(new NetworkEvent { TimestampUtc = now, Type = "adapter_removed", Adapter = kv.Value.Key });
        }
        _lastAdapters = state;
        if (_lastInternet.HasValue && _lastInternet.Value != net.InternetReachable)
            net.Events.Add(new NetworkEvent { TimestampUtc = now, Type = net.InternetReachable ? "internet_restored" : "internet_lost" });
        _lastInternet = net.InternetReachable;
        if (net.Events.Count == 0) net.Events = null;
        if (net.VpnAdapters.Count == 0) net.VpnAdapters = null;

        return new PerformanceMetrics { Sections = new List<string> { "network" }, Network = net };
    }

    private sealed class Measurement { public double? Latency; public double Loss; public string Target; }

    /// <summary>Pings each target; uses the first target that answers at least once, else reports 100% loss for the first.</summary>
    private Measurement MeasureTargets(List<string> targets, int timeoutMs)
    {
        Measurement firstFailed = null;
        foreach (var host in targets.Take(3))
        {
            int ok = 0; long total = 0;
            for (int i = 0; i < PingsPerTarget; i++)
            {
                PingSample s = null;
                try { s = _probe.Ping(host, timeoutMs); } catch (Exception) { }
                if (s != null && s.Success) { ok++; total += s.RoundTripMs; }
            }
            if (ok > 0)
                return new Measurement { Latency = Num.Round((double)total / ok), Loss = Num.Round(100.0 * (PingsPerTarget - ok) / PingsPerTarget), Target = host };
            if (firstFailed == null) firstFailed = new Measurement { Latency = null, Loss = 100, Target = host };
        }
        return firstFailed;
    }
}
