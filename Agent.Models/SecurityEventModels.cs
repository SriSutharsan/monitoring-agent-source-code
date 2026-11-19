using System;
using System.Collections.Generic;

namespace Agent.Models;

public class SecurityStatus
{
    public AntivirusStatus Antivirus { get; set; }
    public FirewallStatus Firewall { get; set; }
    public TpmStatus Tpm { get; set; }
    public string SecureBoot { get; set; }           // Enabled | Disabled | Unsupported | Unknown
    public EncryptionStatus Encryption { get; set; }
    public WindowsUpdateSecurity WindowsUpdate { get; set; }
    public PendingRestartStatus PendingRestart { get; set; }
    public UacStatus Uac { get; set; }
    public AccountPolicyStatus AccountPolicy { get; set; }
    public bool? Elevated { get; set; }
    public List<string> Limitations { get; set; }    // data the agent could not read (privilege / hardware / OS version)
}

public class AntivirusStatus
{
    public List<AntivirusProduct> Products { get; set; }
    public DefenderStatus Defender { get; set; }
}

public class AntivirusProduct
{
    public string Name { get; set; }
    public bool? Enabled { get; set; }
    public bool? SignaturesUpToDate { get; set; }
    public long? RawState { get; set; }
    public string Source { get; set; }               // security-center
}

public class DefenderStatus
{
    public bool Present { get; set; }
    public bool? ServiceRunning { get; set; }
    public bool? AntivirusEnabled { get; set; }
    public bool? AntispywareEnabled { get; set; }
    public bool? RealTimeProtectionEnabled { get; set; }
    public bool? TamperProtected { get; set; }
    public string EngineVersion { get; set; }
    public string SignatureVersion { get; set; }
    public DateTime? SignatureLastUpdatedUtc { get; set; }
    public int? SignatureAgeDays { get; set; }
    public DateTime? LastQuickScanUtc { get; set; }
    public DateTime? LastFullScanUtc { get; set; }
    public List<ThreatDetection> RecentThreats { get; set; }
    public string Source { get; set; }               // wmi | registry
}

public class ThreatDetection
{
    public string ThreatName { get; set; }
    public string Severity { get; set; }
    public string Category { get; set; }
    public bool IsPotentiallyUnwanted { get; set; }
    public DateTime? DetectedUtc { get; set; }
    public string ActionSuccess { get; set; }
}

public class FirewallStatus
{
    public bool? ServiceRunning { get; set; }
    public bool? DomainProfileEnabled { get; set; }
    public bool? PrivateProfileEnabled { get; set; }
    public bool? PublicProfileEnabled { get; set; }
    public string ActiveProfiles { get; set; }
    public string Source { get; set; }               // com | registry
}

public class TpmStatus
{
    public bool Present { get; set; }
    public bool? Enabled { get; set; }
    public bool? Activated { get; set; }
    public string SpecVersion { get; set; }
    public string ManufacturerVersion { get; set; }
}

public class EncryptionStatus
{
    public string BitLockerState { get; set; }       // Available | NotAvailable | Unknown
    public bool? SystemDriveProtected { get; set; }
    public bool? DeviceEncryptionActive { get; set; }
    public List<BitLockerVolume> Volumes { get; set; }
}

public class BitLockerVolume
{
    public string DriveLetter { get; set; }
    public string ProtectionStatus { get; set; }     // On | Off | Unknown
    public string ConversionStatus { get; set; }
    public string EncryptionMethod { get; set; }
}

public class WindowsUpdateSecurity
{
    public bool? ServiceEnabled { get; set; }
    public string AutoUpdateMode { get; set; }
    public DateTime? LastSuccessfulInstallUtc { get; set; }
    public int? DaysSinceLastSuccessfulInstall { get; set; }
    public bool? ManagedByPolicy { get; set; }
}

public class PendingRestartStatus
{
    public bool Pending { get; set; }
    public List<string> Reasons { get; set; }
}

public class UacStatus
{
    public bool? Enabled { get; set; }
    public int? ConsentPromptBehaviorAdmin { get; set; }
    public bool? SecureDesktopPrompt { get; set; }
    public string Level { get; set; }                // AlwaysNotify | Default | NoDim | Disabled | Custom
}

public class AccountPolicyStatus
{
    public int? MinimumPasswordLength { get; set; }
    public int? MaximumPasswordAgeDays { get; set; }
    public int? MinimumPasswordAgeDays { get; set; }
    public int? PasswordHistoryLength { get; set; }
    public int? LockoutThreshold { get; set; }
    public int? LockoutDurationMinutes { get; set; }
    public int? LockoutObservationMinutes { get; set; }
    public string Scope { get; set; }                // local-machine policy
}

// ------------------------------------------------------------------ events

public class WindowsEventBatch
{
    public List<WindowsEventRecord> Events { get; set; }
    public int DroppedCount { get; set; }
    public DateTime FromUtc { get; set; }
    public DateTime ToUtc { get; set; }
}

public class WindowsEventRecord
{
    public string DeviceId { get; set; }
    public string Log { get; set; }
    public long RecordId { get; set; }
    public int EventId { get; set; }
    public string Source { get; set; }
    public string Level { get; set; }                // critical | error | warning | information | verbose
    public string Message { get; set; }
    public DateTime TimestampUtc { get; set; }
    public string Category { get; set; }             // application_error | bsod | unexpected_shutdown | service_failure | driver_error | ...
    public string Severity { get; set; }
    public string Computer { get; set; }
    public Dictionary<string, string> Metadata { get; set; }
}

// ----------------------------------------------------------- health / support

public class DeviceHealth
{
    public int Score { get; set; }                   // 0-100
    public string Status { get; set; }               // healthy | warning | critical
    public List<HealthFactor> Factors { get; set; }
    public List<RecurringProblem> RecurringProblems { get; set; }
    public int UnexpectedShutdownsLast30Days { get; set; }
    public int BugChecksLast30Days { get; set; }
    public DateTime? LastBootUtc { get; set; }
    public bool PendingRestart { get; set; }
    public List<MaintenanceEntry> RecentMaintenance { get; set; }
}

public class HealthFactor
{
    public string Name { get; set; }
    public string Status { get; set; }               // ok | warning | critical | unknown
    public string Detail { get; set; }
    public int Penalty { get; set; }
}

public class RecurringProblem
{
    public string Key { get; set; }
    public string Source { get; set; }
    public int EventId { get; set; }
    public string Description { get; set; }
    public int Occurrences { get; set; }
    public DateTime FirstSeenUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
}

public class MaintenanceEntry
{
    public DateTime TimestampUtc { get; set; }
    public string Type { get; set; }                 // software_installed | update_installed | reboot | hardware_change | ...
    public string Summary { get; set; }
}

public class DiagnosticReport
{
    public string RequestId { get; set; }
    public DateTime GeneratedUtc { get; set; }
    public List<string> Sections { get; set; }
    public List<ServiceInfo> StoppedAutoStartServices { get; set; }
    public List<ProblemDevice> ProblemDevices { get; set; }
    public List<StartupItem> StartupItems { get; set; }
    public List<WindowsEventRecord> RecentErrors { get; set; }
    public List<string> Errors { get; set; }
}

public class ServiceInfo
{
    public string Name { get; set; }
    public string DisplayName { get; set; }
    public string State { get; set; }
    public string StartMode { get; set; }
    public long? ExitCode { get; set; }
}

public class ProblemDevice
{
    public string Name { get; set; }
    public string DeviceClass { get; set; }
    public string DeviceId { get; set; }
    public int ErrorCode { get; set; }
    public string ErrorDescription { get; set; }
}

public class StartupItem
{
    public string Name { get; set; }
    public string Location { get; set; }
    public string Command { get; set; }
}

// ---------------------------------------------------------- derived records

public class AlertRecord
{
    public string AlertKey { get; set; }             // rule + subject, stable across raise/clear
    public string Rule { get; set; }
    public string Subject { get; set; }
    public string State { get; set; }                // raised | cleared | event
    public string Severity { get; set; }
    public string Message { get; set; }
    public double? Value { get; set; }
    public double? Threshold { get; set; }
    public DateTime TimestampUtc { get; set; }
    public Dictionary<string, string> Metadata { get; set; }
}

public class ChangeRecord
{
    public string Category { get; set; }             // hardware | storage | network | software | ...
    public string ChangeClass { get; set; }          // ram_changed, storage_replaced, software_installed, ...
    public string ChangeType { get; set; }           // added | removed | modified
    public string ItemKind { get; set; }
    public string ItemKey { get; set; }
    public string DisplayName { get; set; }
    public string Summary { get; set; }
    public string Before { get; set; }
    public string After { get; set; }
    public DateTime DetectedUtc { get; set; }
}
