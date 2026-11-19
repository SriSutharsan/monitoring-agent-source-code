using System;
using System.Collections.Generic;
using System.Linq;
using Agent.Core;
using Agent.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent.Verify;

public sealed class FakeClock : IClock
{
    public DateTime Now = new DateTime(2026, 11, 18, 10, 0, 0, DateTimeKind.Utc);
    public DateTime UtcNow { get { return Now; } }
}

public sealed class MemState : IStateStore
{
    public readonly Dictionary<string, string> D = new Dictionary<string, string>();
    public string Get(string k) { string v; return D.TryGetValue(k, out v) ? v : null; }
    public void Set(string k, string v) { D[k] = v; }
    public void Delete(string k) { D.Remove(k); }
}

public sealed class MemSnapshots : ISnapshotStore
{
    public readonly Dictionary<string, KeyValuePair<string, string>> D = new Dictionary<string, KeyValuePair<string, string>>();
    public string GetJson(string c) { KeyValuePair<string, string> v; return D.TryGetValue(c, out v) ? v.Key : null; }
    public string GetHash(string c) { KeyValuePair<string, string> v; return D.TryGetValue(c, out v) ? v.Value : null; }
    public void Save(string c, string json, string hash, DateTime now) { D[c] = new KeyValuePair<string, string>(json, hash); }
}

public sealed class StaticConfig : IConfigProvider
{
    public RuntimeConfig Current { get; set; }
    public event Action<RuntimeConfig> Changed;
    public StaticConfig() { Current = RuntimeConfig.CreateDefault(); }
    public void Raise() { var h = Changed; if (h != null) h(Current); }
}

public sealed class FakeFingerprint : IDeviceFingerprintProvider
{
    public DeviceFingerprint Value = new DeviceFingerprint { SystemUuid = "4C4C4544-0042-3510-8051-B2C04F595A32", SerialNumber = "ABC1234" };
    public DeviceFingerprint Get() { return Value; }
}

public sealed class MemOutbox : IOutbox
{
    private sealed class Row { public OutboxRecord R; public int Status; public int Attempts; public DateTime? Next; public string Error; }
    private readonly List<Row> _rows = new List<Row>();
    private long _id;
    private readonly object _g = new object();
    public IClock Clock;
    public MemOutbox(IClock clock) { Clock = clock; }

    public bool Enqueue(OutboxRecord r)
    {
        lock (_g)
        {
            if (_rows.Any(x => x.R.IdempotencyKey == r.IdempotencyKey)) return false;
            r.Id = ++_id;
            _rows.Add(new Row { R = r });
            return true;
        }
    }
    private bool Ready(Row x, DateTime now) { return x.Status == 0 && (x.Next == null || x.Next <= now); }
    public IReadOnlyList<string> ModulesWithReadyRecords(DateTime now)
    {
        lock (_g) return _rows.Where(x => Ready(x, now)).GroupBy(x => x.R.Module).OrderBy(g => g.Min(x => x.R.Priority)).ThenBy(g => g.Min(x => x.R.Id)).Select(g => g.Key).ToList();
    }
    public IReadOnlyList<OutboxRecord> Dequeue(string module, int max, DateTime now)
    {
        lock (_g) return _rows.Where(x => Ready(x, now) && x.R.Module == module).OrderBy(x => x.R.Priority).ThenBy(x => x.R.Id).Take(max)
            .Select(x => { x.R.Attempts = x.Attempts; return x.R; }).ToList();
    }
    private void Each(IEnumerable<long> ids, Action<Row> a) { lock (_g) foreach (var id in ids.ToList()) { var r = _rows.FirstOrDefault(x => x.R.Id == id); if (r != null) a(r); } }
    public void MarkSynced(IEnumerable<long> ids, DateTime now) { Each(ids, r => r.Status = 1); }
    public void MarkRetry(IEnumerable<long> ids, DateTime next, string error, bool count) { Each(ids, r => { if (r.Status != 0) return; if (count) r.Attempts++; r.Next = next; r.Error = error; }); }
    public void MarkDead(IEnumerable<long> ids, string error) { Each(ids, r => { r.Status = 2; r.Error = error; }); }
    public OutboxStats GetStats() { lock (_g) return new OutboxStats { Pending = _rows.Count(x => x.Status == 0), Dead = _rows.Count(x => x.Status == 2), Synced = _rows.Count(x => x.Status == 1) }; }
    public int Prune(RetentionConfig r, DateTime now) { return 0; }
    public int Count(int status) { lock (_g) return _rows.Count(x => x.Status == status); }
    public int MaxAttempts { get { lock (_g) return _rows.Count == 0 ? 0 : _rows.Max(x => x.Attempts); } }
}

public sealed class MemCredentials : ICredentialStore
{
    public DeviceCredential Value;
    public DeviceCredential Load() { return Value; }
    public void Save(DeviceCredential c) { Value = c; }
    public void Delete() { Value = null; }
}

public sealed class FakeInventory : Agent.Communication.IRegistrationInventoryProvider
{
    public System.Threading.Tasks.Task<RegistrationInventory> CollectAsync(System.Threading.CancellationToken ct)
    {
        return System.Threading.Tasks.Task.FromResult(new RegistrationInventory { Device = new DeviceInfo { Hostname = "TEST-PC" } });
    }
    public DeviceFingerprint GetFingerprint() { return new DeviceFingerprint { SystemUuid = "u", SerialNumber = "s" }; }
}

// ---------------------------------------------------------------- platform fakes for collector tests

public sealed class FakeWmiObject : IWmiObject
{
    public Dictionary<string, object> Props = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<IWmiObject>> Related = new Dictionary<string, List<IWmiObject>>(StringComparer.OrdinalIgnoreCase);
    public object this[string p] { get { object v; return Props.TryGetValue(p, out v) ? v : null; } }
    public IReadOnlyList<IWmiObject> GetRelated(string c) { List<IWmiObject> l; return Related.TryGetValue(c, out l) ? l : new List<IWmiObject>(); }
}

public sealed class FakeWmi : IWmiClient
{
    public readonly Dictionary<string, List<IWmiObject>> Data = new Dictionary<string, List<IWmiObject>>(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, string> Errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public readonly List<string> Queries = new List<string>();

    public FakeWmiObject Add(string cls, Dictionary<string, object> props)
    {
        var o = new FakeWmiObject { Props = new Dictionary<string, object>(props, StringComparer.OrdinalIgnoreCase) };
        List<IWmiObject> l;
        if (!Data.TryGetValue(cls, out l)) Data[cls] = l = new List<IWmiObject>();
        l.Add(o);
        return o;
    }

    private static string ClassOf(string wql)
    {
        var m = System.Text.RegularExpressions.Regex.Match(wql, @"\bFROM\s+(\w+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : "";
    }

    public IReadOnlyList<IWmiObject> Query(string wql, string ns = @"root\cimv2") { string e; return TryQuery(wql, ns, out e); }

    public IReadOnlyList<IWmiObject> TryQuery(string wql, string ns, out string error)
    {
        Queries.Add(wql);
        var cls = ClassOf(wql);
        string err;
        if (Errors.TryGetValue(cls, out err)) { error = err; return new List<IWmiObject>(); }
        error = null;
        List<IWmiObject> l;
        return Data.TryGetValue(cls, out l) ? l : new List<IWmiObject>();
    }
}

public static class P
{
    public static Dictionary<string, object> D(params object[] kv)
    {
        var d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
        return d;
    }
}

public sealed class FakeRegistry : IRegistryReader
{
    // key: "HIVE|VIEW|path" (view normalised: View32/View64/Default) -> values
    public readonly Dictionary<string, Dictionary<string, object>> Keys = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
    private static string K(RegistryHiveKind h, RegistryViewKind v, string p) { return h + "|" + v + "|" + p.Trim('\\'); }
    public FakeRegistry Set(RegistryHiveKind h, RegistryViewKind v, string path, string name, object value)
    {
        Dictionary<string, object> d;
        if (!Keys.TryGetValue(K(h, v, path), out d)) Keys[K(h, v, path)] = d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        if (name != null) d[name] = value;
        return this;
    }
    public FakeRegistry Lm(string path, string name, object value) { return Set(RegistryHiveKind.LocalMachine, RegistryViewKind.View64, path, name, value); }
    public object GetValue(RegistryHiveKind h, RegistryViewKind v, string sub, string name)
    {
        Dictionary<string, object> d; object o;
        return Keys.TryGetValue(K(h, v, sub), out d) && d.TryGetValue(name, out o) ? o : null;
    }
    public IReadOnlyList<string> GetSubKeyNames(RegistryHiveKind h, RegistryViewKind v, string sub)
    {
        var prefix = K(h, v, sub) + "\\";
        if (sub == "") prefix = h + "|" + v + "|";
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in Keys.Keys)
            if (k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var rest = k.Substring(prefix.Length);
                if (rest.Length > 0) set.Add(rest.Split('\\')[0]);
            }
        return set.ToList();
    }
    public IReadOnlyList<string> GetValueNames(RegistryHiveKind h, RegistryViewKind v, string sub)
    {
        Dictionary<string, object> d; return Keys.TryGetValue(K(h, v, sub), out d) ? d.Keys.ToList() : new List<string>();
    }
    public bool KeyExists(RegistryHiveKind h, RegistryViewKind v, string sub)
    {
        var k = K(h, v, sub);
        return Keys.ContainsKey(k) || Keys.Keys.Any(x => x.StartsWith(k + "\\", StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class FakeNative : INativeSystem
{
    public ulong Idle, Kernel, User;
    public SystemMemoryStatus Mem = new SystemMemoryStatus { TotalPhysical = 16L << 30, AvailablePhysical = 4L << 30 };
    public long Ticks = 3 * 3600 * 1000L;
    public PowerStatus Power = new PowerStatus { BatteryPresent = false };
    public List<WtsSession> Sessions = new List<WtsSession>();
    public bool Elevated = true;
    public bool TryGetCpuTimes(out ulong idle, out ulong kernel, out ulong user) { idle = Idle; kernel = Kernel; user = User; return true; }
    public bool TryGetMemory(out SystemMemoryStatus s) { s = Mem; return true; }
    public long TickCount64 { get { return Ticks; } }
    public PowerStatus GetPowerStatus() { return Power; }
    public IReadOnlyList<WtsSession> GetSessions() { return Sessions; }
    public AccountPolicyRaw GetAccountPolicy() { return new AccountPolicyRaw { MinPasswordLength = 8, LockoutThreshold = 5 }; }
    public bool IsElevated() { return Elevated; }
    public bool IsWow64OperatingSystem() { return false; }
}

public sealed class FakeIoctl : IDiskIoctl
{
    public Dictionary<int, DiskIoctlInfo> Map = new Dictionary<int, DiskIoctlInfo>();
    public DiskIoctlInfo Query(int i) { DiskIoctlInfo v; return Map.TryGetValue(i, out v) ? v : null; }
}

public sealed class FakeProcesses : IProcessSource
{
    public List<RawProcess> List = new List<RawProcess>();
    public IReadOnlyList<RawProcess> Snapshot() { return List.Select(p => new RawProcess { Pid = p.Pid, Name = p.Name, CpuTime = p.CpuTime, WorkingSetBytes = p.WorkingSetBytes, PrivateBytes = p.PrivateBytes }).ToList(); }
}

public sealed class FakeAdapters : INetworkInterfaceSource
{
    public List<RawAdapter> List = new List<RawAdapter>();
    public IReadOnlyList<RawAdapter> GetAdapters() { return List.Select(a => a).ToList(); }
}

public sealed class FakeProbe : INetworkProbe
{
    public bool PingOk = true; public long Rtt = 20; public bool Tcp = true; public bool? Nla = true;
    public PingSample Ping(string host, int t) { return new PingSample { Success = PingOk, RoundTripMs = Rtt }; }
    public bool TcpConnect(string h, int p, int t) { return Tcp; }
    public bool? IsInternetConnected() { return Nla; }
}

public sealed class FakeWifi : IWifiApi
{
    public WifiSnapshot Value = new WifiSnapshot { WlanServiceAvailable = true };
    public WifiSnapshot Read() { return Value; }
}

public sealed class FakeFirewall : IFirewallPolicyReader
{
    public bool?[] States = new bool?[] { true, true, true };
    public bool?[] ReadProfileStates(out string active) { active = "Private"; return States; }
}

public sealed class FakeWua : IWindowsUpdateClient
{
    public WindowsUpdateInfo Value = new WindowsUpdateInfo { Available = false };
    public WindowsUpdateInfo Read(int n) { return Value; }
}

public sealed class FakeEventReader : IEventLogReader
{
    public readonly Dictionary<string, List<RawEventRecord>> Logs = new Dictionary<string, List<RawEventRecord>>();
    public readonly Dictionary<string, string> Unreadable = new Dictionary<string, string>();
    public readonly List<EventQuerySpec> Specs = new List<EventQuerySpec>();
    public void Add(string log, RawEventRecord e) { List<RawEventRecord> l; if (!Logs.TryGetValue(log, out l)) Logs[log] = l = new List<RawEventRecord>(); l.Add(e); }
    public IReadOnlyList<RawEventRecord> Read(EventQuerySpec spec, out string error)
    {
        Specs.Add(spec);
        string err;
        if (Unreadable.TryGetValue(spec.LogName, out err)) { error = err; return new List<RawEventRecord>(); }
        error = null;
        List<RawEventRecord> l;
        if (!Logs.TryGetValue(spec.LogName, out l)) return new List<RawEventRecord>();
        return l.Where(e => e.RecordId > spec.MinRecordIdExclusive && (spec.SinceUtc == null || e.TimestampUtc >= spec.SinceUtc)).OrderBy(e => e.RecordId).Take(spec.MaxEvents).ToList();
    }
    public long GetLastRecordId(string log) { List<RawEventRecord> l; return Logs.TryGetValue(log, out l) && l.Count > 0 ? l.Max(e => e.RecordId) : 0; }
}

public sealed class MemHistory : IHistoryStore
{
    public List<ChangeRecord> Changes = new List<ChangeRecord>();
    public List<KeyValuePair<string, DateTime>> Problems = new List<KeyValuePair<string, DateTime>>();
    public List<DeviceHealth> Health = new List<DeviceHealth>();
    public void AddChange(ChangeRecord c) { Changes.Add(c); }
    public IReadOnlyList<MaintenanceEntry> RecentMaintenance(int days, int max) { return Changes.Select(c => new MaintenanceEntry { TimestampUtc = c.DetectedUtc, Type = c.ChangeClass, Summary = c.Summary }).Take(max).ToList(); }
    public void RecordProblem(string key, string source, int id, string d, DateTime at) { Problems.Add(new KeyValuePair<string, DateTime>(key, at)); }
    public IReadOnlyList<RecurringProblem> RecurringProblems(DateTime since, int min)
    {
        return Problems.Where(p => p.Value >= since).GroupBy(p => p.Key).Where(g => g.Count() >= min)
            .Select(g => new RecurringProblem { Key = g.Key, Occurrences = g.Count(), Description = g.Key, FirstSeenUtc = g.Min(x => x.Value), LastSeenUtc = g.Max(x => x.Value) }).ToList();
    }
    public int CountProblems(string prefix, DateTime since) { return Problems.Count(p => p.Key.StartsWith(prefix, StringComparison.Ordinal) && p.Value >= since); }
    public void AddHealthSnapshot(DeviceHealth h, DateTime now) { Health.Add(h); }
    public void Prune(RetentionConfig r, DateTime now) { }
}
