using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Agent.Core;
using Agent.Models;

namespace Agent.Collectors.Events;

public class EventClassification
{
    public string Category { get; set; }
    public string Severity { get; set; }
}

/// <summary>
/// What the agent considers relevant in the Windows event logs, as data: one combined XPath predicate per log plus a classifier.
/// Only operational / error / security-relevant events are collected (never "everything"), which keeps volume and privacy exposure low.
/// </summary>
public static class EventCatalog
{
    public const string SystemLog = "System";
    public const string ApplicationLog = "Application";
    public const string SecurityLog = "Security";
    public const string DefenderLog = "Microsoft-Windows-Windows Defender/Operational";
    public const string FirewallLog = "Microsoft-Windows-Windows Firewall With Advanced Security/Firewall";
    public const string WlanLog = "Microsoft-Windows-WLAN-AutoConfig/Operational";
    public const string NetworkProfileLog = "Microsoft-Windows-NetworkProfile/Operational";

    public static readonly string[] Logs = { SystemLog, ApplicationLog, SecurityLog, DefenderLog, FirewallLog, WlanLog, NetworkProfileLog };

    private static readonly string[] SecurityServices =
    {
        "Windows Defender Antivirus Service", "Microsoft Defender Antivirus Service", "Windows Defender Firewall", "Windows Firewall",
        "Security Center", "Windows Security Service", "Windows Event Log", "Windows Update"
    };

    private static readonly HashSet<string> HardwareProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { "disk", "Ntfs", "volmgr", "volsnap", "storahci", "stornvme", "iaStorA", "iaStorAC", "nvme", "stexstor", "Microsoft-Windows-WHEA-Logger", "atapi" };

    private static readonly HashSet<string> DriverProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { "Display", "nvlddmkm", "amdkmdag", "amdwddmg", "igfx", "igfxn", "Microsoft-Windows-Kernel-PnP", "Microsoft-Windows-DriverFrameworks-UserMode", "Microsoft-Windows-UserPnp" };

    /// <summary>Events that are error-level but pure noise on healthy machines.</summary>
    private static readonly int[] NoiseIds = { 10016, 10010, 36871, 36874, 36887, 36888, 36882, 8198 };

    public static string PredicateFor(string log, EventConfig cfg)
    {
        Func<IEnumerable<int>, string> ids = list => "(" + string.Join(" or ", list.Select(i => "EventID=" + i.ToString(CultureInfo.InvariantCulture))) + ")";
        switch (log)
        {
            case SystemLog:
                {
                    var noise = string.Join(" and ", NoiseIds.Select(i => "EventID!=" + i));
                    var providers = string.Join(" or ", new[] { "Service Control Manager", "Microsoft-Windows-WindowsUpdateClient", "Display", "nvlddmkm", "amdkmdag", "Microsoft-Windows-Kernel-PnP" }
                        .Concat(HardwareProviders).Select(p => "Provider[@Name='" + p + "']"));
                    var services = string.Join(" or ", SecurityServices.Select(s => "Data='" + s + "'"));
                    return "System[(Level=1) or " + ids(new[] { 41, 104, 1001, 1074, 6008, 7045, 219, 4101, 7, 11, 15, 51, 55, 129, 153 }) +
                           " or (Level=2 and (" + providers + ") and " + noise + ")" +
                           " or (Provider[@Name='Microsoft-Windows-WHEA-Logger'] and Level<=3)]" +
                           " or (System[EventID=7036] and EventData[" + services + "])";
                }
            case ApplicationLog:
                return "System[" + ids(new[] { 1000, 1002, 1026, 11708 }) + " or Level=1]";
            case SecurityLog:
                {
                    var types = "(Data[@Name='LogonType']='2' or Data[@Name='LogonType']='7' or Data[@Name='LogonType']='10' or Data[@Name='LogonType']='11')";
                    var clauses = new List<string> { "System[" + ids(new[] { 4625, 4740, 1102, 4719, 4720, 4728, 4732 }) + "]" };
                    if (cfg == null || cfg.CollectSuccessfulLogons) clauses.Add("(System[EventID=4624] and EventData[" + types + "])");
                    return string.Join(" or ", clauses);
                }
            case DefenderLog:
                return "System[" + ids(new[] { 1116, 1117, 1118, 1119, 5001, 5010, 5012 }) + "]";
            case FirewallLog:
                return "System[" + ids(new[] { 2002, 2003, 2006 }) + "]";
            case WlanLog:
                return "System[" + ids(new[] { 8002, 8003 }) + "]";
            case NetworkProfileLog:
                return "System[EventID=10001]";
            default:
                return null;
        }
    }

    public static string LevelName(int level)
    {
        switch (level)
        {
            case 1: return "critical"; case 2: return "error"; case 3: return "warning"; case 5: return "verbose"; default: return "information";
        }
    }

    public static string Get(Dictionary<string, string> data, params string[] keys)
    {
        if (data == null) return null;
        foreach (var k in keys)
        {
            string v;
            if (data.TryGetValue(k, out v) && !string.IsNullOrWhiteSpace(v)) return v.Trim();
        }
        return null;
    }

    /// <summary>Maps a raw event to a problem category and severity, or null when it is not something the agent reports.</summary>
    public static EventClassification Classify(string log, RawEventRecord e, EventConfig cfg)
    {
        var provider = e.Provider ?? "";
        int id = e.EventId;
        Func<string, string, EventClassification> c = (cat, sev) => new EventClassification { Category = cat, Severity = sev };

        switch (log)
        {
            case SystemLog:
                if (id == 41 && provider.IndexOf("Kernel-Power", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var bug = Get(e.Data, "BugcheckCode");
                    return bug != null && bug != "0" ? c("bsod", Severity.Critical) : c("unexpected_shutdown", Severity.Critical);
                }
                if (id == 6008) return c("unexpected_shutdown", Severity.Warning);
                if (id == 1001 && (provider.IndexOf("BugCheck", StringComparison.OrdinalIgnoreCase) >= 0 || provider.IndexOf("WER-SystemErrorReporting", StringComparison.OrdinalIgnoreCase) >= 0))
                    return c("bsod", Severity.Critical);
                if (id == 1001) return null;                                     // WER "fault bucket" noise
                if (id == 104) return c("audit_log_cleared", Severity.Critical);
                if (id == 1074) return c("restart_requested", Severity.Info);
                if (id == 7045) return c("service_installed", Severity.Info);
                if (id == 7036)
                {
                    var name = Get(e.Data, "param1", "Data0");
                    var state = Get(e.Data, "param2", "Data1");
                    if (name != null && SecurityServices.Any(s => string.Equals(s, name, StringComparison.OrdinalIgnoreCase)) &&
                        state != null && state.IndexOf("stop", StringComparison.OrdinalIgnoreCase) >= 0)
                        return c("security_service_stopped", Severity.Critical);
                    return null;
                }
                if (provider.Equals("Service Control Manager", StringComparison.OrdinalIgnoreCase) && (e.Level <= 2))
                    return c("service_failure", Severity.Warning);
                if (provider.IndexOf("WindowsUpdateClient", StringComparison.OrdinalIgnoreCase) >= 0 && e.Level <= 2)
                    return c("windows_update_failure", Severity.Warning);
                if (HardwareProviders.Contains(provider) && e.Level <= 3) return c("hardware_error", Severity.Warning);
                if (DriverProviders.Contains(provider) && e.Level <= 2) return c("driver_error", Severity.Warning);
                if (e.Level == 1) return c("system_critical", Severity.Critical);
                return null;

            case ApplicationLog:
                if (id == 1000 && provider.Equals("Application Error", StringComparison.OrdinalIgnoreCase)) return c("application_error", Severity.Warning);
                if (id == 1002) return c("application_hang", Severity.Warning);
                if (id == 1026) return c("application_error", Severity.Warning);
                if (id == 11708 && provider.IndexOf("MsiInstaller", StringComparison.OrdinalIgnoreCase) >= 0) return c("application_install_failure", Severity.Warning);
                if (e.Level == 1) return c("application_critical", Severity.Critical);
                return null;

            case SecurityLog:
                switch (id)
                {
                    case 4625: return c("failed_logon", Severity.Warning);
                    case 4624:
                        if (cfg != null && !cfg.CollectSuccessfulLogons) return null;
                        var user = Get(e.Data, "TargetUserName", "Data5") ?? "";
                        if (user.EndsWith("$", StringComparison.Ordinal) || user.StartsWith("UMFD-", StringComparison.OrdinalIgnoreCase) ||
                            user.StartsWith("DWM-", StringComparison.OrdinalIgnoreCase) || user.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase)) return null;
                        return c("successful_logon", Severity.Info);
                    case 4740: return c("account_lockout", Severity.Warning);
                    case 1102: return c("audit_log_cleared", Severity.Critical);
                    case 4719: return c("audit_policy_changed", Severity.Warning);
                    case 4720: return c("user_account_created", Severity.Info);
                    case 4728: case 4732: return c("privileged_group_change", Severity.Warning);
                }
                return null;

            case DefenderLog:
                if (id == 5001 || id == 5010 || id == 5012) return c("antivirus_disabled", Severity.Critical);
                if (id == 1116 || id == 1117 || id == 1118 || id == 1119) return c("malware_detected", id == 1116 || id == 1117 ? Severity.Warning : Severity.Critical);
                return null;

            case FirewallLog:
                return id == 2002 || id == 2003 || id == 2006 ? c("firewall_change", Severity.Warning) : null;

            case WlanLog:
                return id == 8002 || id == 8003 ? c("network_disconnect", Severity.Info) : null;

            case NetworkProfileLog:
                return id == 10001 ? c("network_disconnect", Severity.Info) : null;
        }
        return null;
    }

    /// <summary>Extracts the small set of metadata fields that make an event actionable (no message bodies, no file or command content).</summary>
    public static Dictionary<string, string> Metadata(string log, string category, RawEventRecord e)
    {
        var m = new Dictionary<string, string>();
        Action<string, string> add = (k, v) => { if (!string.IsNullOrEmpty(v)) m[k] = Text.Truncate(v, 200); };
        switch (category)
        {
            case "failed_logon":
                add("targetUser", Get(e.Data, "TargetUserName", "Data5"));
                add("targetDomain", Get(e.Data, "TargetDomainName", "Data6"));
                add("logonType", Get(e.Data, "LogonType", "Data10"));
                add("sourceIp", Get(e.Data, "IpAddress", "Data19"));
                add("workstation", Get(e.Data, "WorkstationName", "Data13"));
                add("status", Get(e.Data, "SubStatus", "Status", "Data9"));
                break;
            case "successful_logon":
                add("targetUser", Get(e.Data, "TargetUserName", "Data5"));
                add("targetDomain", Get(e.Data, "TargetDomainName", "Data6"));
                add("logonType", Get(e.Data, "LogonType", "Data8"));
                add("sourceIp", Get(e.Data, "IpAddress", "Data18"));
                break;
            case "account_lockout":
                add("targetUser", Get(e.Data, "TargetUserName", "Data0"));
                add("callerComputer", Get(e.Data, "TargetDomainName", "Data4"));
                break;
            case "application_error":
            case "application_hang":
                add("application", Get(e.Data, "AppName", "Data0"));
                add("version", Get(e.Data, "AppVersion", "Data1"));
                add("faultingModule", Get(e.Data, "ModuleName", "Data3"));
                add("exceptionCode", Get(e.Data, "ExceptionCode", "Data6"));
                break;
            case "service_failure":
            case "security_service_stopped":
                add("service", Get(e.Data, "param1", "ServiceName", "Data0"));
                add("state", Get(e.Data, "param2"));
                break;
            case "service_installed":
                add("service", Get(e.Data, "ServiceName", "Data0"));
                add("imagePath", Get(e.Data, "ImagePath", "Data1"));
                break;
            case "bsod":
                add("bugcheckCode", Get(e.Data, "BugcheckCode", "param1"));
                break;
            case "application_install_failure":
                add("product", Get(e.Data, "Data0"));
                break;
            case "restart_requested":
                add("process", Get(e.Data, "param1", "Data0"));
                add("reason", Get(e.Data, "param3", "Data2"));
                add("user", Get(e.Data, "param7", "Data6"));
                break;
            case "malware_detected":
                add("threat", Get(e.Data, "Threat Name", "ThreatName"));
                add("severity", Get(e.Data, "Severity Name"));
                break;
        }
        return m;
    }

    /// <summary>Stable key used for recurring-problem detection (same app / service / source failing again and again).</summary>
    public static string ProblemKey(string category, RawEventRecord e, Dictionary<string, string> meta)
    {
        string subject = null;
        if (category == "application_error" || category == "application_hang") meta.TryGetValue("application", out subject);
        else if (category == "service_failure") meta.TryGetValue("service", out subject);
        return category + ":" + (subject ?? e.Provider ?? "") + ":" + e.EventId.ToString(CultureInfo.InvariantCulture);
    }

    public static readonly HashSet<string> ProblemCategories = new HashSet<string>
    {
        "application_error", "application_hang", "service_failure", "driver_error", "hardware_error", "bsod", "unexpected_shutdown",
        "windows_update_failure", "application_install_failure", "system_critical", "application_critical"
    };
}
