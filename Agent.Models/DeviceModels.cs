using System;
using System.Collections.Generic;

namespace Agent.Models;

public class DeviceInfo
{
    public string DeviceId { get; set; }
    public string Hostname { get; set; }
    public string Manufacturer { get; set; }
    public string Model { get; set; }
    public string SerialNumber { get; set; }
    public string SystemUuid { get; set; }
    public string ChassisType { get; set; }
    public bool? IsLaptop { get; set; }
    public bool? IsVirtualMachine { get; set; }
    public string AssignedUser { get; set; }
    public List<UserSessionInfo> ActiveSessions { get; set; }
    public string Domain { get; set; }
    public bool? PartOfDomain { get; set; }
    public string DomainType { get; set; }          // "domain" | "workgroup"
    public DateTime? FirstRegisteredUtc { get; set; }
    public DateTime? LastBootUtc { get; set; }
    public long? UptimeSeconds { get; set; }
    public string Status { get; set; }              // online | degraded
    public string TimeZone { get; set; }
    public string OsCaption { get; set; }
    public string OsVersion { get; set; }
    public string AgentVersion { get; set; }
    public string AgentRuntime { get; set; }        // ".NET Framework 4.8" / ".NET 8.0"
}

public class UserSessionInfo
{
    public int SessionId { get; set; }
    public string User { get; set; }
    public string Domain { get; set; }
    public string State { get; set; }               // active | disconnected
    public string Station { get; set; }
}

public class CollectorStatus
{
    public string Category { get; set; }
    public DateTime? LastSuccessUtc { get; set; }
    public DateTime? LastAttemptUtc { get; set; }
    public int ConsecutiveFailures { get; set; }
    public string LastError { get; set; }
    public long? LastDurationMs { get; set; }
    public bool Enabled { get; set; }
}

public class Heartbeat
{
    public string DeviceId { get; set; }
    public string Hostname { get; set; }
    public string AgentVersion { get; set; }
    public string Status { get; set; }
    public long AgentUptimeSeconds { get; set; }
    public long? SystemUptimeSeconds { get; set; }
    public long ConfigVersion { get; set; }
    public DateTime? LastSuccessfulSyncUtc { get; set; }
    public long? ClockSkewMs { get; set; }
    public int PendingRecords { get; set; }
    public int DeadLetterRecords { get; set; }
    public string PrimaryIPv4 { get; set; }
    public string OsVersion { get; set; }
    public bool? Elevated { get; set; }
    public double? AgentCpuPercent { get; set; }
    public long? AgentWorkingSetBytes { get; set; }
    public List<CollectorStatus> Collectors { get; set; }
}
