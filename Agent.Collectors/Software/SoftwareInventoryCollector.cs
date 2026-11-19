using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Agent.Core;
using Agent.Models;

namespace Agent.Collectors.Software;

/// <summary>Applies the (remotely configurable) software policy to the inventory: blocked, allow-list, minimum versions, potentially unwanted.</summary>
public static class SoftwarePolicyEvaluator
{
    public static void Apply(IList<InstalledApplication> apps, SoftwarePolicy policy)
    {
        if (policy == null) return;
        bool hasBlock = policy.BlockedApplications != null && policy.BlockedApplications.Count > 0;
        bool hasAllow = policy.AllowedApplications != null && policy.AllowedApplications.Count > 0;
        bool hasPua = policy.PotentiallyUnwantedPatterns != null && policy.PotentiallyUnwantedPatterns.Count > 0;

        foreach (var app in apps)
        {
            if (hasBlock || hasAllow)
            {
                app.IsUnauthorized = false;
                var blocked = Wildcard.FirstMatch(policy.BlockedApplications, app.Name);
                if (blocked != null)
                {
                    app.IsUnauthorized = true;
                    app.PolicyMatch = "blocked: " + blocked;
                }
                else if (hasAllow && !IsMicrosoftComponent(app, policy) && Wildcard.FirstMatch(policy.AllowedApplications, app.Name) == null)
                {
                    app.IsUnauthorized = true;
                    app.PolicyMatch = "not on allow list";
                }
            }
            if (policy.MinimumVersions != null)
            {
                foreach (var rule in policy.MinimumVersions)
                {
                    if (rule == null || !Wildcard.IsMatch(rule.NamePattern, app.Name)) continue;
                    app.IsOutdated = VersionCompare.IsOlderThan(app.Version, rule.MinimumVersion);
                    app.MinimumRequiredVersion = rule.MinimumVersion;
                    break;
                }
            }
            if (hasPua) app.IsPotentiallyUnwanted = Wildcard.FirstMatch(policy.PotentiallyUnwantedPatterns, app.Name) != null;
        }
    }

    private static bool IsMicrosoftComponent(InstalledApplication a, SoftwarePolicy p)
    {
        return p.AllowListIgnoresMicrosoftComponents && a.Publisher != null && a.Publisher.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) >= 0;
    }
}

/// <summary>
/// Installed software from the registry Uninstall keys (64-bit, 32-bit and per-user hives), Windows edition/build/activation,
/// installed hotfixes and Windows Update state. Works on Windows 7 through 11.
/// </summary>
public sealed class SoftwareInventoryCollector : CollectorBase<SoftwareInventory>
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string UserUninstallPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string UserUninstall32Path = @"Software\Wow6432Node\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string NtVersion = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
    private const int MaxUpdates = 250;

    private static readonly Regex KbPattern = new Regex(@"^KB\d{5,8}", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex KbInText = new Regex(@"KB\d{5,8}", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex UpdateNamePattern = new Regex(@"^(Security Update|Update|Hotfix|Service Pack)\b.*\bKB\d+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly HashSet<string> PatchReleaseTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { "Update", "Hotfix", "Security Update", "Update Rollup", "ServicePack", "Service Pack", "Critical Update" };

    private readonly IRegistryReader _registry;
    private readonly IWmiClient _wmi;
    private readonly IWindowsUpdateClient _wua;

    public SoftwareInventoryCollector(IRegistryReader registry, IWmiClient wmi, IWindowsUpdateClient wua)
    {
        _registry = registry;
        _wmi = wmi;
        _wua = wua;
    }

    public override string Category { get { return Categories.Software; } }
    public override TimeSpan Timeout { get { return TimeSpan.FromMinutes(5); } }

    public override Task<SoftwareInventory> CollectTypedAsync(CollectionContext ctx, CancellationToken ct)
    {
        return Task.FromResult(Collect(ctx.Config != null ? ctx.Config.SoftwarePolicy : null));
    }

    public SoftwareInventory Collect(SoftwarePolicy policy)
    {
        var inv = new SoftwareInventory { Applications = ReadApplications() };
        SoftwarePolicyEvaluator.Apply(inv.Applications, policy);
        inv.Os = ReadOs();

        WindowsUpdateInfo wu = null;
        try { wu = _wua.Read(60); } catch (Exception) { }
        inv.Updates = ReadHotfixes();
        inv.UpdateSummary = BuildUpdateSummary(inv.Updates, wu);
        return inv;
    }

    // ----------------------------------------------------------- applications

    public List<InstalledApplication> ReadApplications()
    {
        var result = new Dictionary<string, InstalledApplication>(StringComparer.OrdinalIgnoreCase);
        bool os64 = Environment.Is64BitOperatingSystem;

        ReadHive(result, RegistryHiveKind.LocalMachine, os64 ? RegistryViewKind.View64 : RegistryViewKind.Default, UninstallPath, "machine", os64 ? "x64" : "x86");
        if (os64) ReadHive(result, RegistryHiveKind.LocalMachine, RegistryViewKind.View32, UninstallPath, "machine", "x86");

        // Per-user installs live in each loaded user hive. Hives of users who are not logged on are not loaded and cannot be read.
        IReadOnlyList<string> sids;
        try { sids = _registry.GetSubKeyNames(RegistryHiveKind.Users, RegistryViewKind.Default, ""); }
        catch (Exception) { sids = new string[0]; }
        foreach (var sid in sids)
        {
            if (!sid.StartsWith("S-1-5-21-", StringComparison.Ordinal) || sid.EndsWith("_Classes", StringComparison.OrdinalIgnoreCase)) continue;
            ReadHive(result, RegistryHiveKind.Users, RegistryViewKind.Default, sid + "\\" + UserUninstallPath, "user:" + sid, os64 ? "x64" : "x86");
            if (os64) ReadHive(result, RegistryHiveKind.Users, RegistryViewKind.Default, sid + "\\" + UserUninstall32Path, "user:" + sid, "x86");
        }
        return result.Values.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ThenBy(a => a.Version).ToList();
    }

    private void ReadHive(Dictionary<string, InstalledApplication> into, RegistryHiveKind hive, RegistryViewKind view, string path, string scope, string arch)
    {
        IReadOnlyList<string> keys;
        try { keys = _registry.GetSubKeyNames(hive, view, path); }
        catch (Exception) { return; }
        foreach (var keyName in keys)
        {
            try
            {
                var sub = path + "\\" + keyName;
                Func<string, object> get = n => _registry.GetValue(hive, view, sub, n);

                var name = Text.ToStringOrNull(get("DisplayName"));
                if (name == null) continue;
                if (Text.ToLong(get("SystemComponent")) == 1) continue;
                if (get("ParentKeyName") != null || get("ParentDisplayName") != null) continue;
                var release = Text.ToStringOrNull(get("ReleaseType"));
                if (release != null && PatchReleaseTypes.Contains(release)) continue;
                if (KbPattern.IsMatch(keyName) || UpdateNamePattern.IsMatch(name)) continue;

                var key = scope + "|" + arch + "|" + keyName;
                if (into.ContainsKey(key)) continue;
                into[key] = new InstalledApplication
                {
                    Key = key,
                    Name = name.Trim(),
                    Version = Text.ToStringOrNull(get("DisplayVersion")),
                    Publisher = Text.ToStringOrNull(get("Publisher")),
                    InstallDateUtc = Wmi.ParseYmd(Text.ToStringOrNull(get("InstallDate"))),
                    InstallLocation = Text.ToStringOrNull(get("InstallLocation")),
                    Architecture = arch,
                    Scope = scope.StartsWith("user", StringComparison.Ordinal) ? "user" : "machine",
                    UninstallCommand = Text.ToStringOrNull(get("UninstallString")),
                    QuietUninstallCommand = Text.ToStringOrNull(get("QuietUninstallString"))
                };
            }
            catch (Exception) { /* one malformed key must not hide the rest */ }
        }
    }

    // ------------------------------------------------------------- operating system

    private OperatingSystemInfo ReadOs()
    {
        var os = new OperatingSystemInfo();
        var w = _wmi.Query("SELECT Caption, Version, BuildNumber, OSArchitecture, CSDVersion, InstallDate FROM Win32_OperatingSystem").FirstOrDefault();
        if (w != null)
        {
            os.Caption = (w.Str("Caption") ?? "").Replace("Microsoft ", "").Trim();
            os.Version = w.Str("Version");
            os.BuildNumber = w.Str("BuildNumber");
            os.Architecture = w.Str("OSArchitecture");
            os.ServicePack = w.Str("CSDVersion");
            os.InstallDateUtc = w.Dmtf("InstallDate");
        }
        Func<string, object> get = n => _registry.GetValue(RegistryHiveKind.LocalMachine, RegistryViewKind.View64, NtVersion, n);
        os.Edition = Text.ToStringOrNull(get("EditionID")) ?? Text.ToStringOrNull(get("ProductName"));
        os.DisplayVersion = Text.ToStringOrNull(get("DisplayVersion")) ?? Text.ToStringOrNull(get("ReleaseId"));
        os.Revision = Text.ToInt(get("UBR"));
        if (os.BuildNumber == null) os.BuildNumber = Text.ToStringOrNull(get("CurrentBuildNumber")) ?? Text.ToStringOrNull(get("CurrentBuild"));
        if (string.IsNullOrEmpty(os.Caption)) os.Caption = Text.ToStringOrNull(get("ProductName"));
        if (string.IsNullOrEmpty(os.Architecture)) os.Architecture = Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit";

        var lic = _wmi.TryQuery("SELECT Name, LicenseStatus, Description FROM SoftwareLicensingProduct WHERE PartialProductKey IS NOT NULL AND ApplicationId = '55c92734-d682-4d71-983e-d6ec3f16059f'",
            @"root\cimv2", out _);
        var product = lic.FirstOrDefault();
        if (product != null)
        {
            os.ActivationStatus = ActivationName(product.Int("LicenseStatus"));
            os.ActivationDetail = product.Str("Description");
        }
        else os.ActivationStatus = "Unknown";
        return os;
    }

    public static string ActivationName(int? status)
    {
        switch (status.GetValueOrDefault(-1))
        {
            case 0: return "Unlicensed"; case 1: return "Licensed"; case 2: return "Grace"; case 3: return "Grace"; case 4: return "Grace";
            case 5: return "Notification"; case 6: return "Grace"; default: return "Unknown";
        }
    }

    // ---------------------------------------------------------------- updates

    public static DateTime? ParseHotfixDate(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        DateTime d;
        // hex FILETIME is what older builds return (e.g. "01d9a2b1c3d4e5f6")
        if (Regex.IsMatch(s, "^[0-9a-fA-F]{16}$") && long.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long ft) && ft > 0)
            try { return DateTime.FromFileTimeUtc(ft); } catch (ArgumentOutOfRangeException) { }
        var cultures = new[] { CultureInfo.CurrentCulture, CultureInfo.InvariantCulture, new CultureInfo("en-US"), new CultureInfo("en-GB") };
        foreach (var c in cultures)
            if (DateTime.TryParse(s, c, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out d) && d.Year > 1995) return d;
        return null;
    }

    private List<InstalledUpdate> ReadHotfixes()
    {
        var list = new List<InstalledUpdate>();
        foreach (var q in _wmi.Query("SELECT HotFixID, Description, InstalledOn, InstalledBy FROM Win32_QuickFixEngineering"))
        {
            var id = q.Str("HotFixID");
            if (id == null) continue;
            var desc = q.Str("Description");
            var on = q["InstalledOn"] is DateTime ? Text.ToUtc(q["InstalledOn"]) : ParseHotfixDate(q.Str("InstalledOn"));
            list.Add(new InstalledUpdate
            {
                Id = id, Description = desc, InstalledOnUtc = on, InstalledBy = q.Str("InstalledBy"),
                IsSecurityUpdate = desc != null && desc.IndexOf("Security", StringComparison.OrdinalIgnoreCase) >= 0
            });
        }
        return list.OrderByDescending(u => u.InstalledOnUtc ?? DateTime.MinValue).Take(MaxUpdates).ToList();
    }

    public static WindowsUpdateSummary BuildUpdateSummary(IList<InstalledUpdate> hotfixes, WindowsUpdateInfo wu)
    {
        var s = new WindowsUpdateSummary { PendingDetection = wu != null && wu.Available ? "wua-cached" : "unavailable" };

        var latestSecurity = hotfixes.Where(h => h.IsSecurityUpdate == true && h.InstalledOnUtc.HasValue).OrderByDescending(h => h.InstalledOnUtc).FirstOrDefault();
        if (latestSecurity != null) { s.LatestSecurityUpdateId = latestSecurity.Id; s.LatestSecurityUpdateUtc = latestSecurity.InstalledOnUtc; }
        s.LastUpdateInstalledUtc = hotfixes.Where(h => h.InstalledOnUtc.HasValue).Select(h => h.InstalledOnUtc).DefaultIfEmpty(null).Max();

        if (wu != null && wu.Available)
        {
            s.PendingUpdateTitles = (wu.PendingTitles ?? new List<string>()).Take(50).ToList();
            s.PendingUpdateCount = wu.PendingTitles != null ? wu.PendingTitles.Count : 0;
            var history = wu.History ?? new List<UpdateHistoryEntry>();
            s.RecentFailures = history.Where(h => h.Result == "Failed").OrderByDescending(h => h.DateUtc).Take(20).ToList();
            foreach (var h in history.Where(x => x.Result == "Succeeded" && x.DateUtc.HasValue))
            {
                if (!s.LastUpdateInstalledUtc.HasValue || h.DateUtc > s.LastUpdateInstalledUtc) s.LastUpdateInstalledUtc = h.DateUtc;
                var title = h.Title ?? "";
                bool security = title.IndexOf("Security", StringComparison.OrdinalIgnoreCase) >= 0 || title.IndexOf("Cumulative", StringComparison.OrdinalIgnoreCase) >= 0;
                if (security && (!s.LatestSecurityUpdateUtc.HasValue || h.DateUtc > s.LatestSecurityUpdateUtc))
                {
                    var kb = KbInText.Match(title);
                    s.LatestSecurityUpdateUtc = h.DateUtc;
                    s.LatestSecurityUpdateId = kb.Success ? kb.Value.ToUpperInvariant() : title;
                }
            }
        }
        return s;
    }
}
