using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Agent.Core;
using Agent.Models;

namespace Agent.Collectors.Hardware;

/// <summary>Device identity: hostname, make/model, serial, UUID, assigned user, domain/workgroup, boot time, uptime.</summary>
public sealed class DeviceInfoCollector : CollectorBase<DeviceInfo>
{
    // SMBIOS chassis types that are portable computers
    private static readonly HashSet<int> LaptopChassis = new HashSet<int> { 8, 9, 10, 11, 12, 14, 18, 21, 30, 31, 32 };

    private readonly IWmiClient _wmi;
    private readonly INativeSystem _native;
    private readonly IRegistryReader _registry;
    private readonly DeviceIdentityService _identity;
    private readonly string _agentVersion;
    private readonly string _runtime;

    public DeviceInfoCollector(IWmiClient wmi, INativeSystem native, IRegistryReader registry, DeviceIdentityService identity, string agentVersion, string runtime)
    {
        _wmi = wmi;
        _native = native;
        _registry = registry;
        _identity = identity;
        _agentVersion = agentVersion;
        _runtime = runtime;
    }

    public override string Category { get { return Categories.Device; } }

    public override Task<DeviceInfo> CollectTypedAsync(CollectionContext ctx, CancellationToken ct)
    {
        return Task.FromResult(Collect(ctx.NowUtc, ctx.DeviceId));
    }

    public DeviceInfo Collect(DateTime nowUtc, string deviceId)
    {
        var info = new DeviceInfo
        {
            DeviceId = deviceId,
            Hostname = Environment.MachineName,
            FirstRegisteredUtc = _identity.FirstRegisteredUtc,
            Status = "online",
            AgentVersion = _agentVersion,
            AgentRuntime = _runtime,
            TimeZone = TimeZoneInfo.Local.Id
        };

        var cs = _wmi.Query("SELECT Manufacturer, Model, Domain, PartOfDomain, UserName, DomainRole, Name, HypervisorPresent FROM Win32_ComputerSystem").FirstOrDefault();
        if (cs != null)
        {
            info.Manufacturer = Clean(cs.Str("Manufacturer"));
            info.Model = Clean(cs.Str("Model"));
            info.PartOfDomain = cs.Bool("PartOfDomain");
            info.Domain = cs.Str("Domain");
            info.DomainType = info.PartOfDomain == true ? "domain" : "workgroup";
            var name = cs.Str("Name");
            if (!string.IsNullOrEmpty(name)) info.Hostname = name;
        }

        var product = _wmi.Query("SELECT UUID, IdentifyingNumber, Vendor, Name FROM Win32_ComputerSystemProduct").FirstOrDefault();
        if (product != null)
        {
            var uuid = product.Str("UUID");
            info.SystemUuid = DeviceIdentityService.IsPlaceholder(uuid) ? null : uuid;
            if (string.IsNullOrEmpty(info.Manufacturer)) info.Manufacturer = Clean(product.Str("Vendor"));
            if (string.IsNullOrEmpty(info.Model)) info.Model = Clean(product.Str("Name"));
        }

        var bios = _wmi.Query("SELECT SerialNumber FROM Win32_BIOS").FirstOrDefault();
        var serial = bios != null ? bios.Str("SerialNumber") : null;
        if (DeviceIdentityService.IsPlaceholder(serial) && product != null) serial = product.Str("IdentifyingNumber");
        info.SerialNumber = DeviceIdentityService.IsPlaceholder(serial) ? null : serial;

        var enclosure = _wmi.Query("SELECT ChassisTypes FROM Win32_SystemEnclosure").FirstOrDefault();
        if (enclosure != null)
        {
            var types = ChassisTypes(enclosure["ChassisTypes"]);
            if (types.Count > 0)
            {
                info.IsLaptop = types.Any(LaptopChassis.Contains);
                info.ChassisType = info.IsLaptop == true ? "laptop" : ChassisName(types[0]);
            }
        }
        if (info.IsLaptop == null)
        {
            var battery = _wmi.Query("SELECT DeviceID FROM Win32_Battery");
            if (battery.Count > 0) { info.IsLaptop = true; info.ChassisType = "laptop"; }
        }

        info.IsVirtualMachine = DetectVirtualMachine(info.Manufacturer, info.Model, cs != null ? cs.Bool("HypervisorPresent") : null);

        var os = _wmi.Query("SELECT Caption, Version, LastBootUpTime FROM Win32_OperatingSystem").FirstOrDefault();
        if (os != null)
        {
            info.OsCaption = os.Str("Caption");
            info.OsVersion = os.Str("Version");
            info.LastBootUtc = os.Dmtf("LastBootUpTime");
        }
        var uptimeMs = _native.TickCount64;
        if (uptimeMs > 0)
        {
            info.UptimeSeconds = uptimeMs / 1000;
            if (!info.LastBootUtc.HasValue) info.LastBootUtc = nowUtc.AddMilliseconds(-uptimeMs);
        }

        info.ActiveSessions = ReadSessions();
        info.AssignedUser = ResolveAssignedUser(info.ActiveSessions, cs != null ? cs.Str("UserName") : null);
        return info;
    }

    private List<UserSessionInfo> ReadSessions()
    {
        var list = new List<UserSessionInfo>();
        try
        {
            foreach (var s in _native.GetSessions())
                if (!string.IsNullOrEmpty(s.User))
                    list.Add(new UserSessionInfo { SessionId = s.SessionId, User = s.User, Domain = s.Domain, State = s.State, Station = s.Station });
        }
        catch (Exception) { }
        return list;
    }

    private string ResolveAssignedUser(List<UserSessionInfo> sessions, string wmiUser)
    {
        var active = sessions.FirstOrDefault(s => s.State == "active") ?? sessions.FirstOrDefault();
        if (active != null) return string.IsNullOrEmpty(active.Domain) ? active.User : active.Domain + "\\" + active.User;
        if (!string.IsNullOrEmpty(wmiUser)) return wmiUser;
        // nobody is logged on: fall back to the last interactive user Windows remembers
        var last = _registry.GetValue(RegistryHiveKind.LocalMachine, RegistryViewKind.View64,
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\LogonUI", "LastLoggedOnUser");
        return Text.ToStringOrNull(last);
    }

    private static List<int> ChassisTypes(object value)
    {
        var r = new List<int>();
        var e = value as System.Collections.IEnumerable;
        if (e == null || value is string) return r;
        foreach (var item in e)
        {
            var n = Text.ToInt(item);
            if (n.HasValue) r.Add(n.Value);
        }
        return r;
    }

    private static string ChassisName(int t)
    {
        switch (t)
        {
            case 3: case 4: case 5: case 6: case 7: case 15: case 16: return "desktop";
            case 17: case 23: case 28: return "server";
            case 13: return "all-in-one";
            default: return "other";
        }
    }

    public static bool? DetectVirtualMachine(string manufacturer, string model, bool? hypervisorPresent)
    {
        var s = ((manufacturer ?? "") + " " + (model ?? "")).ToLowerInvariant();
        if (s.Contains("vmware") || s.Contains("virtualbox") || s.Contains("virtual machine") || s.Contains("kvm") || s.Contains("qemu") ||
            s.Contains("xen") || s.Contains("parallels") || s.Contains("bochs")) return true;
        if (s.Trim().Length == 0) return null;
        return false;
    }

    private static string Clean(string s)
    {
        if (DeviceIdentityService.IsPlaceholder(s)) return null;
        return s.Trim();
    }
}
