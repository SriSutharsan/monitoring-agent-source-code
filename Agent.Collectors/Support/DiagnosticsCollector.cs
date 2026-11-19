using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Agent.Collectors.Events;
using Agent.Core;
using Agent.Models;

namespace Agent.Collectors.Support;

/// <summary>
/// On-demand diagnostic snapshot, produced ONLY when an administrator requests it through the signed remote configuration
/// (config.diagnostics.requestId). Each request is answered once. Read-only: lists services, devices with driver problems,
/// startup entries and recent errors. It never runs commands, opens a shell or reads files.
/// </summary>
public sealed class DiagnosticsCollector : CollectorBase<DiagnosticReport>
{
    private const string DoneKey = "diagnostics.done_request";
    private static readonly string[] AllSections = { "services", "problem_devices", "startup_items", "recent_errors" };

    private readonly IWmiClient _wmi;
    private readonly IRegistryReader _registry;
    private readonly IEventLogReader _events;
    private readonly IStateStore _state;
    private readonly IClock _clock;

    public DiagnosticsCollector(IWmiClient wmi, IRegistryReader registry, IEventLogReader events, IStateStore state, IClock clock)
    {
        _wmi = wmi;
        _registry = registry;
        _events = events;
        _state = state;
        _clock = clock;
    }

    public override string Category { get { return Categories.Diagnostics; } }
    public override TimeSpan Timeout { get { return TimeSpan.FromMinutes(3); } }

    public override Task<DiagnosticReport> CollectTypedAsync(CollectionContext ctx, CancellationToken ct)
    {
        return Task.FromResult(Collect(ctx.Config.Diagnostics));
    }

    public DiagnosticReport Collect(DiagnosticRequest request)
    {
        if (request == null || string.IsNullOrEmpty(request.RequestId)) return null;
        if (request.ExpiresUtc.HasValue && request.ExpiresUtc.Value < _clock.UtcNow) return null;
        if (_state.Get(DoneKey) == request.RequestId) return null;

        var wanted = (request.Sections == null || request.Sections.Count == 0 ? AllSections : request.Sections.Intersect(AllSections, StringComparer.OrdinalIgnoreCase).ToArray());
        var report = new DiagnosticReport
        {
            RequestId = request.RequestId,
            GeneratedUtc = _clock.UtcNow,
            Sections = wanted.Select(s => s.ToLowerInvariant()).ToList(),
            Errors = new List<string>()
        };

        foreach (var section in report.Sections)
        {
            try
            {
                switch (section)
                {
                    case "services": report.StoppedAutoStartServices = ReadServices(); break;
                    case "problem_devices": report.ProblemDevices = ReadProblemDevices(); break;
                    case "startup_items": report.StartupItems = ReadStartupItems(); break;
                    case "recent_errors": report.RecentErrors = ReadRecentErrors(); break;
                }
            }
            catch (Exception ex) { report.Errors.Add(section + ": " + Text.Truncate(ex.Message, 200)); }
        }
        _state.Set(DoneKey, request.RequestId);
        return report;
    }

    private List<ServiceInfo> ReadServices()
    {
        return _wmi.Query("SELECT Name, DisplayName, State, StartMode, ExitCode FROM Win32_Service WHERE StartMode = 'Auto' AND State <> 'Running'")
            .Select(s => new ServiceInfo { Name = s.Str("Name"), DisplayName = s.Str("DisplayName"), State = s.Str("State"), StartMode = s.Str("StartMode"), ExitCode = s.Long("ExitCode") })
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Take(200).ToList();
    }

    public static string ProblemCodeText(int code)
    {
        switch (code)
        {
            case 1: return "not configured correctly"; case 3: return "driver corrupted or low memory"; case 10: return "device cannot start";
            case 12: return "cannot find enough free resources"; case 14: return "restart required"; case 18: return "reinstall drivers";
            case 19: return "registry configuration problem"; case 21: return "Windows is removing the device"; case 22: return "device is disabled";
            case 24: return "device not present or not working"; case 28: return "drivers not installed"; case 31: return "device is not working properly";
            case 32: return "driver service disabled"; case 37: return "driver initialization failed"; case 39: return "driver corrupted or missing";
            case 40: return "service key information missing"; case 41: return "driver loaded but device not found"; case 43: return "Windows stopped the device (reported a problem)";
            case 45: return "device not connected"; case 48: return "driver blocked from starting"; case 52: return "driver signature cannot be verified";
            default: return "device manager error code " + code.ToString(CultureInfo.InvariantCulture);
        }
    }

    private List<ProblemDevice> ReadProblemDevices()
    {
        var list = new List<ProblemDevice>();
        foreach (var d in _wmi.Query("SELECT Name, PNPClass, DeviceID, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0"))
        {
            var code = d.Int("ConfigManagerErrorCode") ?? 0;
            if (code == 0) continue;
            list.Add(new ProblemDevice { Name = d.Str("Name"), DeviceClass = d.Str("PNPClass"), DeviceId = d.Str("DeviceID"), ErrorCode = code, ErrorDescription = ProblemCodeText(code) });
        }
        return list.Take(100).ToList();
    }

    private List<StartupItem> ReadStartupItems()
    {
        var items = new List<StartupItem>();
        Action<RegistryHiveKind, RegistryViewKind, string, string> read = (hive, view, path, label) =>
        {
            try
            {
                foreach (var name in _registry.GetValueNames(hive, view, path))
                {
                    var cmd = Text.ToStringOrNull(_registry.GetValue(hive, view, path, name));
                    if (cmd != null) items.Add(new StartupItem { Name = name, Location = label, Command = Text.Truncate(cmd, 300) });
                }
            }
            catch (Exception) { }
        };
        const string run = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        read(RegistryHiveKind.LocalMachine, RegistryViewKind.View64, run, @"HKLM\" + run);
        if (Environment.Is64BitOperatingSystem) read(RegistryHiveKind.LocalMachine, RegistryViewKind.View32, run, @"HKLM\WOW6432Node\...\Run");
        try
        {
            foreach (var sid in _registry.GetSubKeyNames(RegistryHiveKind.Users, RegistryViewKind.Default, ""))
                if (sid.StartsWith("S-1-5-21-", StringComparison.Ordinal) && !sid.EndsWith("_Classes", StringComparison.OrdinalIgnoreCase))
                    read(RegistryHiveKind.Users, RegistryViewKind.Default, sid + @"\Software\Microsoft\Windows\CurrentVersion\Run", @"HKU\" + sid + @"\...\Run");
        }
        catch (Exception) { }
        return items.Take(200).ToList();
    }

    private List<WindowsEventRecord> ReadRecentErrors()
    {
        var list = new List<WindowsEventRecord>();
        foreach (var log in new[] { EventCatalog.SystemLog, EventCatalog.ApplicationLog })
        {
            string error;
            var raw = _events.Read(new EventQuerySpec { LogName = log, SinceUtc = _clock.UtcNow.AddHours(-24), Predicate = "System[Level<=2]", MaxEvents = 60 }, out error);
            foreach (var e in raw)
                list.Add(new WindowsEventRecord
                {
                    Log = log, RecordId = e.RecordId, EventId = e.EventId, Source = e.Provider, Level = EventCatalog.LevelName(e.Level),
                    Message = Text.Truncate(e.Message, 400), TimestampUtc = e.TimestampUtc, Computer = e.Computer
                });
        }
        return list.OrderByDescending(e => e.TimestampUtc).Take(100).ToList();
    }
}
