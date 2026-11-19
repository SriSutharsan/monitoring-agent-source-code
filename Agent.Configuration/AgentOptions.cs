using System;
using System.Collections.Generic;

namespace Agent.Configuration;

/// <summary>Bound from appsettings.json ("Agent" section). Installer writes the registration values.</summary>
public class AgentOptions
{
    public const string SectionName = "Agent";

    public string ServiceName { get; set; } = "EndpointMonitoringAgent";
    public ApiOptions Api { get; set; } = new ApiOptions();
    public RegistrationOptions Registration { get; set; } = new RegistrationOptions();
    public LoggingOptions Logging { get; set; } = new LoggingOptions();
    public SecurityOptions Security { get; set; } = new SecurityOptions();
    public CollectionOptions Collection { get; set; } = new CollectionOptions();
    public string DataDirectory { get; set; }
}

public class ApiOptions
{
    /// <summary>Base URL, e.g. https://monitor.example.com . HTTPS is mandatory.</summary>
    public string BaseUrl { get; set; }
    public string ApiVersion { get; set; } = "v1";
    public int RequestTimeoutSeconds { get; set; } = 30;
    public int MaxRetryDelaySeconds { get; set; } = 900;
    /// <summary>Optional SHA-256 certificate/public-key pins (hex). Empty = normal OS certificate validation only.</summary>
    public List<string> PinnedCertificateSha256 { get; set; } = new List<string>();
    /// <summary>Optional proxy; empty = system proxy.</summary>
    public string ProxyUrl { get; set; }
    /// <summary>Allows http://localhost for development/mock servers ONLY. Must stay false in production.</summary>
    public bool AllowInsecureLoopback { get; set; } = false;
}

public class RegistrationOptions
{
    public string Token { get; set; }
    public string Group { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();
    public int PollSeconds { get; set; } = 60;
}

public class LoggingOptions
{
    public string MinimumLevel { get; set; } = "Information";
    public int RetainedFileCount { get; set; } = 14;
    public long FileSizeLimitBytes { get; set; } = 10L * 1024 * 1024;
    public bool WriteToEventLog { get; set; } = true;
}

public class SecurityOptions
{
    /// <summary>RSA public key (PEM, SPKI) used to verify signed config documents and update manifests.</summary>
    public string ConfigSigningPublicKey { get; set; }
    /// <summary>When true, remote config and updates without a valid signature are rejected (always true in production).</summary>
    public bool RequireSignedConfig { get; set; } = true;
    public int MaxClockSkewMinutes { get; set; } = 10;
}

public class CollectionOptions
{
    /// <summary>Local overrides applied before remote config (seconds). Keys are category names, e.g. "perf.cpu_memory".</summary>
    public Dictionary<string, int> IntervalsSeconds { get; set; } = new Dictionary<string, int>();
    public Dictionary<string, bool> CollectorsEnabled { get; set; } = new Dictionary<string, bool>();
    public int SyncIntervalSeconds { get; set; } = 15;
    public int BatchSize { get; set; } = 100;
    public int MaxConcurrentCollections { get; set; } = 2;
    public int SyncedRetentionDays { get; set; } = 7;
    public bool CollectSuccessfulLogons { get; set; } = true;
    public List<string> PingTargets { get; set; } = new List<string>();
}
