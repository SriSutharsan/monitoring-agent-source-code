using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Agent.Models;

namespace Agent.Core;

public interface IClock
{
    DateTime UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTime UtcNow { get { return DateTime.UtcNow; } }
}

public class CollectionContext
{
    public string DeviceId { get; set; }
    public DateTime NowUtc { get; set; }
    public RuntimeConfig Config { get; set; }
}

/// <summary>One monitoring category. Collectors never touch the network or the database.</summary>
public interface ICollector
{
    string Category { get; }
    TimeSpan Timeout { get; }
    /// <summary>Returns the payload to queue, or null when there is nothing to report this cycle.</summary>
    Task<object> CollectAsync(CollectionContext context, CancellationToken ct);
}

public interface ICollector<T> : ICollector where T : class
{
    Task<T> CollectTypedAsync(CollectionContext context, CancellationToken ct);
}

public abstract class CollectorBase<T> : ICollector<T> where T : class
{
    public abstract string Category { get; }
    public virtual TimeSpan Timeout { get { return TimeSpan.FromSeconds(120); } }

    public abstract Task<T> CollectTypedAsync(CollectionContext context, CancellationToken ct);

    public async Task<object> CollectAsync(CollectionContext context, CancellationToken ct)
    {
        return await CollectTypedAsync(context, ct).ConfigureAwait(false);
    }
}

/// <summary>Receives every successful collection result (the pipeline persists it to the outbox).</summary>
public interface ICollectionSink
{
    Task HandleAsync(string category, object payload, CancellationToken ct);
}

public interface ICollectorStatusSource
{
    IReadOnlyList<CollectorStatus> Snapshot();
}

public interface IConfigProvider
{
    RuntimeConfig Current { get; }
    event Action<RuntimeConfig> Changed;
}

public interface ICredentialStore
{
    DeviceCredential Load();
    void Save(DeviceCredential credential);
    void Delete();
}

// ------------------------------------------------------------------ storage

public class OutboxRecord
{
    public long Id { get; set; }
    public string Module { get; set; }
    public string Category { get; set; }
    public string IdempotencyKey { get; set; }
    public DateTime CollectedAtUtc { get; set; }
    public string PayloadJson { get; set; }
    public int Priority { get; set; }
    public int Attempts { get; set; }
    public DateTime? NextAttemptUtc { get; set; }
}

public class OutboxStats
{
    public int Pending { get; set; }
    public int Dead { get; set; }
    public int Synced { get; set; }
    public DateTime? OldestPendingUtc { get; set; }
}

public interface IOutbox
{
    /// <summary>Returns false when a record with the same idempotency key already exists.</summary>
    bool Enqueue(OutboxRecord record);
    /// <summary>Modules that currently have ready records, most important first.</summary>
    IReadOnlyList<string> ModulesWithReadyRecords(DateTime nowUtc);
    IReadOnlyList<OutboxRecord> Dequeue(string module, int max, DateTime nowUtc);
    void MarkSynced(IEnumerable<long> ids, DateTime nowUtc);
    void MarkRetry(IEnumerable<long> ids, DateTime nextAttemptUtc, string error, bool countAttempt);
    void MarkDead(IEnumerable<long> ids, string error);
    OutboxStats GetStats();
    /// <summary>Applies the retention policy; returns the number of rows removed.</summary>
    int Prune(RetentionConfig retention, DateTime nowUtc);
}

public interface IStateStore
{
    string Get(string key);
    void Set(string key, string value);
    void Delete(string key);
}

public interface ISnapshotStore
{
    string GetJson(string category);
    string GetHash(string category);
    void Save(string category, string json, string hash, DateTime nowUtc);
}

public interface IHistoryStore
{
    void AddChange(ChangeRecord change);
    IReadOnlyList<MaintenanceEntry> RecentMaintenance(int days, int max);
    void RecordProblem(string key, string source, int eventId, string description, DateTime occurredUtc);
    IReadOnlyList<RecurringProblem> RecurringProblems(DateTime sinceUtc, int minOccurrences);
    int CountProblems(string categoryPrefix, DateTime sinceUtc);
    void AddHealthSnapshot(DeviceHealth health, DateTime nowUtc);
    void Prune(RetentionConfig retention, DateTime nowUtc);
}

// ----------------------------------------------------------------- platform

/// <summary>Thin seam over WMI/CIM. The only production implementation is a System.Management wrapper.</summary>
public interface IWmiClient
{
    /// <summary>Runs a WQL query. Returns an empty list when the namespace/class is not available on this Windows version.</summary>
    IReadOnlyList<IWmiObject> Query(string wql, string wmiNamespace = @"root\cimv2");
    /// <summary>Same as Query but reports why a query returned nothing (access denied, namespace/class missing, timeout).</summary>
    IReadOnlyList<IWmiObject> TryQuery(string wql, string wmiNamespace, out string error);
}

public interface IWmiObject
{
    object this[string property] { get; }
    IReadOnlyList<IWmiObject> GetRelated(string resultClass);
}

public enum RegistryHiveKind { LocalMachine, Users, CurrentUser }
public enum RegistryViewKind { Default, View32, View64 }

public interface IRegistryReader
{
    object GetValue(RegistryHiveKind hive, RegistryViewKind view, string subKey, string name);
    IReadOnlyList<string> GetSubKeyNames(RegistryHiveKind hive, RegistryViewKind view, string subKey);
    IReadOnlyList<string> GetValueNames(RegistryHiveKind hive, RegistryViewKind view, string subKey);
    bool KeyExists(RegistryHiveKind hive, RegistryViewKind view, string subKey);
}

public class EventQuerySpec
{
    public string LogName { get; set; }
    public long MinRecordIdExclusive { get; set; }
    public DateTime? SinceUtc { get; set; }
    /// <summary>XPath boolean expression valid inside "*[ ... ]" (System[...] / EventData[...] terms). The reader adds the record-id and time filters.</summary>
    public string Predicate { get; set; }
    public int MaxEvents { get; set; } = 500;
}

public class RawEventRecord
{
    public long RecordId { get; set; }
    public int EventId { get; set; }
    public string Provider { get; set; }
    public int Level { get; set; }
    public DateTime TimestampUtc { get; set; }
    public string Computer { get; set; }
    public string Message { get; set; }
    public Dictionary<string, string> Data { get; set; }
}

public interface IEventLogReader
{
    /// <summary>Returns events matching the spec; empty when the log does not exist or access is denied.</summary>
    IReadOnlyList<RawEventRecord> Read(EventQuerySpec spec, out string error);
    /// <summary>Highest record id currently in the log (0 when unavailable).</summary>
    long GetLastRecordId(string logName);
}

public class WindowsUpdateInfo
{
    public bool Available { get; set; }
    public List<string> PendingTitles { get; set; }
    public List<UpdateHistoryEntry> History { get; set; }
    public string Error { get; set; }
}

public interface IWindowsUpdateClient
{
    /// <summary>Reads the Windows Update Agent's cached state. Never triggers a network scan.</summary>
    WindowsUpdateInfo Read(int historyCount);
}

public interface IFirewallPolicyReader
{
    /// <summary>Returns profile enabled flags (domain, private, public) or null when the COM API is unavailable.</summary>
    bool?[] ReadProfileStates(out string activeProfiles);
}

public class SystemMemoryStatus
{
    public long TotalPhysical { get; set; }
    public long AvailablePhysical { get; set; }
    public long TotalCommit { get; set; }
    public long AvailableCommit { get; set; }
}

public class PowerStatus
{
    public bool BatteryPresent { get; set; }
    public bool? OnAcPower { get; set; }
    public int? ChargePercent { get; set; }
    public bool Charging { get; set; }
    public int? SecondsRemaining { get; set; }
}

public class WtsSession
{
    public int SessionId { get; set; }
    public string User { get; set; }
    public string Domain { get; set; }
    public string State { get; set; }
    public string Station { get; set; }
}

public class AccountPolicyRaw
{
    public int? MinPasswordLength { get; set; }
    public int? MaxPasswordAgeDays { get; set; }
    public int? MinPasswordAgeDays { get; set; }
    public int? PasswordHistoryLength { get; set; }
    public int? LockoutThreshold { get; set; }
    public int? LockoutDurationMinutes { get; set; }
    public int? LockoutObservationMinutes { get; set; }
}

/// <summary>Win32 API access that does not need WMI (cheap, works from Windows 7).</summary>
public interface INativeSystem
{
    bool TryGetCpuTimes(out ulong idle, out ulong kernel, out ulong user);
    bool TryGetMemory(out SystemMemoryStatus status);
    long TickCount64 { get; }
    PowerStatus GetPowerStatus();
    IReadOnlyList<WtsSession> GetSessions();
    AccountPolicyRaw GetAccountPolicy();
    bool IsElevated();
    bool IsWow64OperatingSystem();
}

public class WifiSnapshot
{
    public bool WlanServiceAvailable { get; set; }
    public bool AdapterPresent { get; set; }
    public string AdapterGuid { get; set; }
    public string AdapterDescription { get; set; }
    public string InterfaceState { get; set; }
    public bool Connected { get; set; }
    public string Ssid { get; set; }
    public string Bssid { get; set; }
    public int? SignalQuality { get; set; }
    public int? Rssi { get; set; }
    public int? FrequencyKhz { get; set; }
    public int PhyType { get; set; }
    public long? RxRateKbps { get; set; }
    public long? TxRateKbps { get; set; }
    public int AuthAlgorithm { get; set; }
    public int CipherAlgorithm { get; set; }
    public bool OneXEnabled { get; set; }
}

public interface IWifiApi
{
    /// <summary>Reads SSID / BSSID / signal data only. Implementations must never read profile keys or passwords.</summary>
    WifiSnapshot Read();
}

public class DiskIoctlInfo
{
    public int? BusType { get; set; }              // STORAGE_BUS_TYPE (7 USB, 11 SATA, 17 NVMe ...)
    public bool? IncursSeekPenalty { get; set; }   // true = spinning disk, false = SSD
}

public interface IDiskIoctl
{
    /// <summary>Queries \\.\PhysicalDriveN without needing administrator rights. Null when the device cannot be opened.</summary>
    DiskIoctlInfo Query(int physicalDriveIndex);
}

public class RawProcess
{
    public int Pid { get; set; }
    public string Name { get; set; }
    public TimeSpan CpuTime { get; set; }
    public long WorkingSetBytes { get; set; }
    public long PrivateBytes { get; set; }
}

public interface IProcessSource
{
    IReadOnlyList<RawProcess> Snapshot();
}

public class RawAdapter
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string Description { get; set; }
    public string Mac { get; set; }
    public string InterfaceType { get; set; }      // Ethernet | Wireless80211 | Loopback | Tunnel | Ppp | Other
    public bool IsUp { get; set; }
    public long SpeedBitsPerSecond { get; set; }
    public List<string> IPv4 { get; set; }
    public List<string> IPv6 { get; set; }
    public List<string> Masks { get; set; }
    public List<string> Gateways { get; set; }
    public List<string> Dns { get; set; }
    public bool? DhcpEnabled { get; set; }
    public string DhcpServer { get; set; }
    public string DnsSuffix { get; set; }
    public long BytesReceived { get; set; }
    public long BytesSent { get; set; }
}

public interface INetworkInterfaceSource
{
    IReadOnlyList<RawAdapter> GetAdapters();
}

public class PingSample
{
    public bool Success { get; set; }
    public long RoundTripMs { get; set; }
}

public interface INetworkProbe
{
    PingSample Ping(string host, int timeoutMs);
    bool TcpConnect(string host, int port, int timeoutMs);
    /// <summary>Windows Network List Manager verdict, or null when unavailable.</summary>
    bool? IsInternetConnected();
}

public interface IDeviceFingerprintProvider
{
    DeviceFingerprint Get();
}
