using System;
using System.Collections.Generic;

namespace Agent.Models;

// ---- registration ---------------------------------------------------------

public class RegistrationRequest
{
    public string SchemaVersion { get; set; } = SchemaInfo.SchemaVersion;
    public string DeviceId { get; set; }
    public string RegistrationToken { get; set; }
    public string AgentVersion { get; set; }
    public string Hostname { get; set; }
    public Dictionary<string, string> Metadata { get; set; }
    public DeviceFingerprint Fingerprint { get; set; }
    public DateTime RequestedUtc { get; set; }
    public RegistrationInventory Inventory { get; set; }
}

public class DeviceFingerprint
{
    public string SystemUuid { get; set; }
    public string SerialNumber { get; set; }
    public string Manufacturer { get; set; }
    public string Model { get; set; }
    public List<string> MacAddresses { get; set; }
}

public class RegistrationInventory
{
    public DeviceInfo Device { get; set; }
    public HardwareInventory Hardware { get; set; }
    public StorageInventory Storage { get; set; }
    public NetworkInfo Network { get; set; }
}

public class RegistrationResponse
{
    public string Status { get; set; }               // pending | approved | rejected
    public string DeviceId { get; set; }
    public DeviceCredential Credential { get; set; }
    public int? PollAfterSeconds { get; set; }
    public string Message { get; set; }
    public DateTime? ServerTimeUtc { get; set; }
}

public class DeviceCredential
{
    public string Secret { get; set; }
    public string KeyId { get; set; }
    public DateTime? ExpiresUtc { get; set; }
}

public static class RegistrationStates
{
    public const string Unregistered = "unregistered";
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string Unauthorized = "unauthorized";
}

// ---- ingestion ------------------------------------------------------------

public class IngestResponse
{
    public string Status { get; set; }
    public List<string> Accepted { get; set; }
    public List<RejectedRecord> Rejected { get; set; }
    public DateTime? ServerTimeUtc { get; set; }
    public long? ConfigVersion { get; set; }
    public int? NextSyncSeconds { get; set; }
    public UpdateOffer Update { get; set; }
}

public class RejectedRecord
{
    public string Id { get; set; }
    public string Code { get; set; }
    public string Message { get; set; }
    public bool? Retryable { get; set; }
}

public class ApiError
{
    public ApiErrorBody Error { get; set; }
}

public class ApiErrorBody
{
    public string Code { get; set; }
    public string Message { get; set; }
    public string RequestId { get; set; }
    public List<ApiFieldError> Details { get; set; }
}

public class ApiFieldError
{
    public string Field { get; set; }
    public string Issue { get; set; }
}

// ---- signed envelopes (config + update manifests) -------------------------

/// <summary>
/// payload = base64(UTF-8 JSON). signature = base64(RSA-SHA256 PKCS#1 v1.5 over the decoded payload bytes).
/// </summary>
public class SignedEnvelope
{
    public string Payload { get; set; }
    public string Signature { get; set; }
    public string KeyId { get; set; }
}

public class UpdateOffer
{
    public SignedEnvelope Manifest { get; set; }
}

public class UpdateManifest
{
    public string Version { get; set; }
    public string PackageUrl { get; set; }
    public string Sha256 { get; set; }
    public long? SizeBytes { get; set; }
    public string Runtime { get; set; }              // "net48" | "net8" | null = any
    public DateTime? IssuedAtUtc { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
}

// ---- remote configuration -------------------------------------------------

public class AgentConfigDocument
{
    public long Version { get; set; }
    public DateTime? IssuedAtUtc { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
    public string DeviceId { get; set; }             // optional binding; when set must match this device
    public Dictionary<string, int> IntervalsSeconds { get; set; }
    public Dictionary<string, bool> CollectorsEnabled { get; set; }
    public int? SyncIntervalSeconds { get; set; }
    public int? BatchSize { get; set; }
    public ThresholdConfig Thresholds { get; set; }
    public RetentionConfig Retention { get; set; }
    public SoftwarePolicy SoftwarePolicy { get; set; }
    public EventConfig Events { get; set; }
    public NetworkProbeConfig NetworkProbes { get; set; }
    public DiagnosticRequest Diagnostics { get; set; }
}

public class ThresholdConfig
{
    public double StorageWarningPercent { get; set; } = 80;
    public double StorageCriticalPercent { get; set; } = 90;
    public long CriticalFreeBytes { get; set; } = 2L * 1024 * 1024 * 1024;
    public double CriticalFreePercent { get; set; } = 5;
    public double CpuPercent { get; set; } = 90;
    public double RamPercent { get; set; } = 90;
    public int SustainedSamples { get; set; } = 3;
    public double DiskBusyPercent { get; set; } = 95;
    public double DiskQueueLength { get; set; } = 8;
    public double ProcessCpuPercent { get; set; } = 80;
    public int ProcessSustainedSamples { get; set; } = 10;
    public double LatencyMs { get; set; } = 300;
    public double PacketLossPercent { get; set; } = 10;
    public int NetworkFlapCount { get; set; } = 3;
    public int NetworkFlapWindowMinutes { get; set; } = 15;
    public int BatteryLowPercent { get; set; } = 15;
    public int BatteryHealthLowPercent { get; set; } = 50;
    public int SignatureMaxAgeDays { get; set; } = 7;
    public int FailedLogonCount { get; set; } = 5;
    public int FailedLogonWindowMinutes { get; set; } = 10;
    public double DiskTemperatureC { get; set; } = 65;
    public double SystemTemperatureC { get; set; } = 90;
    public int PendingRestartDays { get; set; } = 7;
    public int RecurringProblemCount { get; set; } = 5;
    public int RecurringProblemWindowHours { get; set; } = 24;
}

public class RetentionConfig
{
    public int SyncedRetentionDays { get; set; } = 7;
    public int PendingMaxAgeDays { get; set; } = 30;
    public int MaxPendingRecords { get; set; } = 200000;
    public int HealthHistoryDays { get; set; } = 30;
    public int ChangeHistoryDays { get; set; } = 365;
}

public class SoftwarePolicy
{
    /// <summary>Wildcard patterns (* and ?) matched against application name; any match = unauthorized.</summary>
    public List<string> BlockedApplications { get; set; }
    /// <summary>When non-empty, applications NOT matching any pattern are flagged (allow-list mode).</summary>
    public List<string> AllowedApplications { get; set; }
    public List<MinimumVersionRule> MinimumVersions { get; set; }
    public List<string> PotentiallyUnwantedPatterns { get; set; }
    public bool AllowListIgnoresMicrosoftComponents { get; set; } = true;
}

public class MinimumVersionRule
{
    public string NamePattern { get; set; }
    public string MinimumVersion { get; set; }
}

public class EventConfig
{
    public int FirstRunLookbackMinutes { get; set; } = 360;
    public int MaxEventsPerCycle { get; set; } = 500;
    public int MaxMessageLength { get; set; } = 1000;
    public bool CollectSuccessfulLogons { get; set; } = true;
}

public class NetworkProbeConfig
{
    public List<string> PingTargets { get; set; }
    public int PingTimeoutMs { get; set; } = 1500;
    public bool PingGateway { get; set; } = true;
}

public class DiagnosticRequest
{
    public string RequestId { get; set; }
    public DateTime? ExpiresUtc { get; set; }
    public List<string> Sections { get; set; }       // services | problem_devices | startup_items | recent_errors
}
