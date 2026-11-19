using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Agent.Core;
using Agent.Models;

namespace Agent.Collectors.Security;

/// <summary>
/// Endpoint security posture: antivirus / Defender, firewall, TPM, Secure Boot, BitLocker, Windows Update, pending restart, UAC and
/// local account policy. Anything that cannot be read (missing privilege, edition or hardware support, older Windows) is listed in
/// "limitations" instead of being guessed. Reads configuration only: no passwords, files or personal data.
/// </summary>
public sealed class SecurityCollector : CollectorBase<SecurityStatus>
{
    private const string DefenderNs = @"root\Microsoft\Windows\Defender";
    private const string SecurityCenterNs = @"root\SecurityCenter2";
    private const string TpmNs = @"root\cimv2\Security\MicrosoftTpm";
    private const string BitLockerNs = @"root\cimv2\Security\MicrosoftVolumeEncryption";

    private readonly IWmiClient _wmi;
    private readonly IRegistryReader _registry;
    private readonly IFirewallPolicyReader _firewall;
    private readonly INativeSystem _native;
    private readonly IClock _clock;

    public SecurityCollector(IWmiClient wmi, IRegistryReader registry, IFirewallPolicyReader firewall, INativeSystem native, IClock clock)
    {
        _wmi = wmi;
        _registry = registry;
        _firewall = firewall;
        _native = native;
        _clock = clock;
    }

    public override string Category { get { return Categories.Security; } }
    public override TimeSpan Timeout { get { return TimeSpan.FromMinutes(3); } }

    public override Task<SecurityStatus> CollectTypedAsync(CollectionContext ctx, CancellationToken ct)
    {
        return Task.FromResult(Collect());
    }

    private object Reg(string path, string name, RegistryViewKind view = RegistryViewKind.View64)
    {
        return _registry.GetValue(RegistryHiveKind.LocalMachine, view, path, name);
    }

    public SecurityStatus Collect()
    {
        var limits = new List<string>();
        var s = new SecurityStatus { Limitations = limits, Elevated = SafeElevated() };
        if (s.Elevated == false) limits.Add("agent is not elevated: TPM, BitLocker, SMART and Security-log data may be incomplete (the installed service runs as LocalSystem)");

        s.Antivirus = ReadAntivirus(limits);
        s.Firewall = ReadFirewall(limits);
        s.Tpm = ReadTpm(limits);
        s.SecureBoot = ReadSecureBoot();
        s.Encryption = ReadEncryption(limits);
        s.WindowsUpdate = ReadWindowsUpdate();
        s.PendingRestart = ReadPendingRestart();
        s.Uac = ReadUac();
        s.AccountPolicy = ReadAccountPolicy(limits);
        return s;
    }

    private bool? SafeElevated()
    {
        try { return _native.IsElevated(); } catch (Exception) { return null; }
    }

    // ------------------------------------------------------------- antivirus

    public static bool ProductEnabled(long state) { return (state & 0x1000) != 0; }
    public static bool ProductUpToDate(long state) { return (state & 0x10) == 0; }

    private AntivirusStatus ReadAntivirus(List<string> limits)
    {
        var av = new AntivirusStatus { Products = new List<AntivirusProduct>() };
        string err;
        foreach (var p in _wmi.TryQuery("SELECT displayName, productState FROM AntiVirusProduct", SecurityCenterNs, out err))
        {
            var state = p.Long("productState");
            av.Products.Add(new AntivirusProduct
            {
                Name = p.Str("displayName"),
                RawState = state,
                Enabled = state.HasValue ? ProductEnabled(state.Value) : (bool?)null,
                SignaturesUpToDate = state.HasValue ? ProductUpToDate(state.Value) : (bool?)null,
                Source = "security-center"
            });
        }
        if (av.Products.Count == 0 && err != null) limits.Add("antivirus products: Security Center WMI is unavailable (" + Brief(err) + ")");
        av.Defender = ReadDefender(limits);
        return av;
    }

    private DefenderStatus ReadDefender(List<string> limits)
    {
        var d = new DefenderStatus();
        string err;
        var svc = _wmi.Query("SELECT State FROM Win32_Service WHERE Name = 'WinDefend'").FirstOrDefault();
        if (svc != null) { d.Present = true; d.ServiceRunning = string.Equals(svc.Str("State"), "Running", StringComparison.OrdinalIgnoreCase); }

        var st = _wmi.TryQuery("SELECT AMServiceEnabled, AntivirusEnabled, AntispywareEnabled, RealTimeProtectionEnabled, IsTamperProtected, AMEngineVersion, " +
                               "AntivirusSignatureVersion, AntivirusSignatureLastUpdated, AntivirusSignatureAge, QuickScanEndTime, FullScanEndTime FROM MSFT_MpComputerStatus",
            DefenderNs, out err).FirstOrDefault();
        if (st != null)
        {
            d.Present = true;
            d.Source = "wmi";
            d.AntivirusEnabled = st.Bool("AntivirusEnabled");
            d.AntispywareEnabled = st.Bool("AntispywareEnabled");
            d.RealTimeProtectionEnabled = st.Bool("RealTimeProtectionEnabled");
            d.TamperProtected = st.Bool("IsTamperProtected");
            d.EngineVersion = st.Str("AMEngineVersion");
            d.SignatureVersion = st.Str("AntivirusSignatureVersion");
            d.SignatureLastUpdatedUtc = st.Dmtf("AntivirusSignatureLastUpdated");
            d.SignatureAgeDays = st.Int("AntivirusSignatureAge");
            d.LastQuickScanUtc = st.Dmtf("QuickScanEndTime");
            d.LastFullScanUtc = st.Dmtf("FullScanEndTime");
            d.RecentThreats = ReadThreats();
        }
        else if (d.Present || _registry.KeyExists(RegistryHiveKind.LocalMachine, RegistryViewKind.View64, @"SOFTWARE\Microsoft\Windows Defender"))
        {
            // Windows 7 (Security Essentials) / Defender WMI provider disabled: registry fallback
            d.Present = true;
            d.Source = "registry";
            ReadDefenderRegistry(d);
        }
        if (d.SignatureAgeDays == null && d.SignatureLastUpdatedUtc.HasValue)
            d.SignatureAgeDays = Math.Max(0, (int)(_clock.UtcNow - d.SignatureLastUpdatedUtc.Value).TotalDays);
        if (!d.Present) return new DefenderStatus { Present = false };
        return d;
    }

    private void ReadDefenderRegistry(DefenderStatus d)
    {
        foreach (var root in new[] { @"SOFTWARE\Microsoft\Windows Defender", @"SOFTWARE\Microsoft\Microsoft Antimalware" })
        {
            var sig = root + @"\Signature Updates";
            var version = Text.ToStringOrNull(Reg(sig, "AVSignatureVersion"));
            if (version == null) continue;
            d.SignatureVersion = version;
            d.EngineVersion = Text.ToStringOrNull(Reg(sig, "EngineVersion"));
            var applied = Reg(sig, "AVSignatureApplied") as byte[];
            if (applied != null && applied.Length >= 8)
            {
                try { d.SignatureLastUpdatedUtc = DateTime.FromFileTimeUtc(BitConverter.ToInt64(applied, 0)); } catch (ArgumentOutOfRangeException) { }
            }
            var disableRt = Text.ToLong(Reg(root + @"\Real-Time Protection", "DisableRealtimeMonitoring"))
                            ?? Text.ToLong(Reg(@"SOFTWARE\Policies\Microsoft\Windows Defender\Real-Time Protection", "DisableRealtimeMonitoring"));
            if (disableRt.HasValue) d.RealTimeProtectionEnabled = disableRt.Value == 0;
            var disableAs = Text.ToLong(Reg(@"SOFTWARE\Policies\Microsoft\Windows Defender", "DisableAntiSpyware"));
            d.AntivirusEnabled = d.ServiceRunning ?? (disableAs.HasValue ? disableAs.Value == 0 : (bool?)null);
            return;
        }
    }

    private List<ThreatDetection> ReadThreats()
    {
        var list = new List<ThreatDetection>();
        string err;
        var names = new Dictionary<string, IWmiObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in _wmi.TryQuery("SELECT ThreatID, ThreatName, SeverityID, CategoryID FROM MSFT_MpThreat", DefenderNs, out err))
        {
            var id = t.Str("ThreatID");
            if (id != null) names[id] = t;
        }
        var since = _clock.UtcNow.AddDays(-30);
        foreach (var det in _wmi.TryQuery("SELECT ThreatID, InitialDetectionTime, ActionSuccess FROM MSFT_MpThreatDetection", DefenderNs, out err))
        {
            var when = det.Dmtf("InitialDetectionTime");
            if (when.HasValue && when.Value < since) continue;
            IWmiObject threat;
            names.TryGetValue(det.Str("ThreatID") ?? "", out threat);
            var name = threat != null ? threat.Str("ThreatName") : null;
            var category = threat != null ? threat.Int("CategoryID") : null;
            list.Add(new ThreatDetection
            {
                ThreatName = name ?? ("ThreatID " + det.Str("ThreatID")),
                Severity = SeverityName(threat != null ? threat.Int("SeverityID") : null),
                Category = category.HasValue ? category.Value.ToString(CultureInfo.InvariantCulture) : null,
                IsPotentiallyUnwanted = category == 27 || (name != null && name.StartsWith("PUA:", StringComparison.OrdinalIgnoreCase)),
                DetectedUtc = when,
                ActionSuccess = det.Str("ActionSuccess")
            });
        }
        return list.OrderByDescending(t => t.DetectedUtc).Take(20).ToList();
    }

    public static string SeverityName(int? id)
    {
        switch (id.GetValueOrDefault(-1))
        {
            case 1: return "Low"; case 2: return "Moderate"; case 4: return "High"; case 5: return "Severe"; default: return "Unknown";
        }
    }

    // -------------------------------------------------------------- firewall

    private FirewallStatus ReadFirewall(List<string> limits)
    {
        var fw = new FirewallStatus();
        var svc = _wmi.Query("SELECT State FROM Win32_Service WHERE Name = 'MpsSvc'").FirstOrDefault();
        if (svc != null) fw.ServiceRunning = string.Equals(svc.Str("State"), "Running", StringComparison.OrdinalIgnoreCase);

        bool?[] profiles = null;
        string active = null;
        try { profiles = _firewall.ReadProfileStates(out active); } catch (Exception) { }
        if (profiles != null && profiles.Length >= 3)
        {
            fw.DomainProfileEnabled = profiles[0];
            fw.PrivateProfileEnabled = profiles[1];
            fw.PublicProfileEnabled = profiles[2];
            fw.ActiveProfiles = active;
            fw.Source = "com";
        }
        else
        {
            const string basePath = @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\";
            fw.DomainProfileEnabled = Text.ToBool(Reg(basePath + "DomainProfile", "EnableFirewall"));
            fw.PrivateProfileEnabled = Text.ToBool(Reg(basePath + "StandardProfile", "EnableFirewall"));
            fw.PublicProfileEnabled = Text.ToBool(Reg(basePath + "PublicProfile", "EnableFirewall"));
            fw.Source = "registry";
            if (!fw.DomainProfileEnabled.HasValue && !fw.PrivateProfileEnabled.HasValue && !fw.PublicProfileEnabled.HasValue)
                limits.Add("firewall: profile state could not be read");
        }
        return fw;
    }

    // ------------------------------------------------------------------- TPM

    private TpmStatus ReadTpm(List<string> limits)
    {
        string err;
        var t = _wmi.TryQuery("SELECT IsEnabled_InitialValue, IsActivated_InitialValue, SpecVersion, ManufacturerVersion FROM Win32_Tpm", TpmNs, out err).FirstOrDefault();
        if (t == null)
        {
            // Win32_Tpm needs administrator rights; an empty answer with no error means the machine has no TPM.
            limits.Add(err != null ? "tpm: " + Brief(err) : "tpm: not present on this device");
            return new TpmStatus { Present = false };
        }
        var spec = t.Str("SpecVersion");
        return new TpmStatus
        {
            Present = true,
            Enabled = t.Bool("IsEnabled_InitialValue"),
            Activated = t.Bool("IsActivated_InitialValue"),
            SpecVersion = spec != null ? spec.Split(',')[0].Trim() : null,
            ManufacturerVersion = t.Str("ManufacturerVersion")
        };
    }

    private string ReadSecureBoot()
    {
        var v = Text.ToLong(Reg(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled"));
        if (v.HasValue) return v.Value == 1 ? "Enabled" : "Disabled";
        // key missing: legacy BIOS (Secure Boot unsupported) or a Windows version that predates the key
        return _registry.KeyExists(RegistryHiveKind.LocalMachine, RegistryViewKind.View64, @"SYSTEM\CurrentControlSet\Control\SecureBoot") ? "Unknown" : "Unsupported";
    }

    // -------------------------------------------------------------- BitLocker

    public static string ProtectionName(int? c) { return c == 1 ? "On" : c == 0 ? "Off" : "Unknown"; }

    public static string ConversionName(int? c)
    {
        switch (c.GetValueOrDefault(-1))
        {
            case 0: return "Fully decrypted"; case 1: return "Fully encrypted"; case 2: return "Encryption in progress"; case 3: return "Decryption in progress";
            case 4: return "Encryption paused"; case 5: return "Decryption paused"; default: return "Unknown";
        }
    }

    public static string EncryptionMethodName(int? c)
    {
        switch (c.GetValueOrDefault(-1))
        {
            case 0: return "None"; case 1: return "AES-128 + Diffuser"; case 2: return "AES-256 + Diffuser"; case 3: return "AES-128"; case 4: return "AES-256";
            case 5: return "Hardware encryption"; case 6: return "XTS-AES-128"; case 7: return "XTS-AES-256"; default: return null;
        }
    }

    private EncryptionStatus ReadEncryption(List<string> limits)
    {
        string err;
        var rows = _wmi.TryQuery("SELECT DriveLetter, ProtectionStatus, ConversionStatus, EncryptionMethod FROM Win32_EncryptableVolume", BitLockerNs, out err);
        var e = new EncryptionStatus { Volumes = new List<BitLockerVolume>() };
        if (rows.Count == 0)
        {
            e.BitLockerState = err != null ? "Unknown" : "NotAvailable";
            limits.Add("bitlocker: " + (err != null ? Brief(err) : "not available on this Windows edition"));
            return e;
        }
        e.BitLockerState = "Available";
        foreach (var r in rows)
        {
            e.Volumes.Add(new BitLockerVolume
            {
                DriveLetter = r.Str("DriveLetter"),
                ProtectionStatus = ProtectionName(r.Int("ProtectionStatus")),
                ConversionStatus = ConversionName(r.Int("ConversionStatus")),
                EncryptionMethod = EncryptionMethodName(r.Int("EncryptionMethod"))
            });
        }
        var sysDrive = Environment.GetEnvironmentVariable("SystemDrive");
        var sys = e.Volumes.FirstOrDefault(v => string.Equals(v.DriveLetter, sysDrive, StringComparison.OrdinalIgnoreCase));
        if (sys != null)
        {
            e.SystemDriveProtected = sys.ProtectionStatus == "On";
            e.DeviceEncryptionActive = sys.ProtectionStatus == "On";      // Windows "device encryption" is BitLocker on the OS volume
        }
        return e;
    }

    // ----------------------------------------------------------- Windows Update

    private WindowsUpdateSecurity ReadWindowsUpdate()
    {
        var w = new WindowsUpdateSecurity();
        var svc = _wmi.Query("SELECT StartMode FROM Win32_Service WHERE Name = 'wuauserv'").FirstOrDefault();
        if (svc != null) w.ServiceEnabled = !string.Equals(svc.Str("StartMode"), "Disabled", StringComparison.OrdinalIgnoreCase);

        const string policy = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU";
        var noAuto = Text.ToLong(Reg(policy, "NoAutoUpdate"));
        var auOptions = Text.ToLong(Reg(policy, "AUOptions"));
        w.ManagedByPolicy = noAuto.HasValue || auOptions.HasValue || _registry.KeyExists(RegistryHiveKind.LocalMachine, RegistryViewKind.View64, @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate");
        if (noAuto == 1) w.AutoUpdateMode = "Disabled by policy";
        else if (auOptions.HasValue) w.AutoUpdateMode = AutoUpdateName(auOptions.Value);
        else w.AutoUpdateMode = "Default (automatic)";

        var last = Text.ToStringOrNull(Reg(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\Results\Install", "LastSuccessTime"));
        DateTime t;
        if (last != null && DateTime.TryParse(last, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out t) && t.Year > 2000)
        {
            w.LastSuccessfulInstallUtc = t;
            w.DaysSinceLastSuccessfulInstall = Math.Max(0, (int)(_clock.UtcNow - t).TotalDays);
        }
        return w;
    }

    private static string AutoUpdateName(long o)
    {
        switch (o)
        {
            case 2: return "Notify before download"; case 3: return "Auto download, notify to install"; case 4: return "Auto download and schedule install";
            case 5: return "Local admin chooses"; default: return "Policy option " + o;
        }
    }

    // ---------------------------------------------------------- pending restart

    private PendingRestartStatus ReadPendingRestart()
    {
        var reasons = new List<string>();
        if (_registry.KeyExists(RegistryHiveKind.LocalMachine, RegistryViewKind.View64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending")) reasons.Add("component servicing");
        if (_registry.KeyExists(RegistryHiveKind.LocalMachine, RegistryViewKind.View64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired")) reasons.Add("windows update");
        if (_registry.KeyExists(RegistryHiveKind.LocalMachine, RegistryViewKind.View64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Services\Pending")) reasons.Add("windows update (pending services)");
        var renames = Reg(@"SYSTEM\CurrentControlSet\Control\Session Manager", "PendingFileRenameOperations") as string[];
        if (renames != null && renames.Any(r => !string.IsNullOrEmpty(r))) reasons.Add("pending file rename");
        var active = Text.ToStringOrNull(Reg(@"SYSTEM\CurrentControlSet\Control\ComputerName\ActiveComputerName", "ComputerName"));
        var pending = Text.ToStringOrNull(Reg(@"SYSTEM\CurrentControlSet\Control\ComputerName\ComputerName", "ComputerName"));
        if (active != null && pending != null && !string.Equals(active, pending, StringComparison.OrdinalIgnoreCase)) reasons.Add("computer rename");
        return new PendingRestartStatus { Pending = reasons.Count > 0, Reasons = reasons };
    }

    // ---------------------------------------------------------------------- UAC

    public static string UacLevel(long? enableLua, long? consentAdmin, long? secureDesktop)
    {
        if (enableLua == 0) return "Disabled";
        var consent = consentAdmin ?? 5;                 // Windows default when the value is absent
        var secure = secureDesktop ?? 1;
        if (consent == 2 && secure == 1) return "AlwaysNotify";
        if (consent == 5 && secure == 1) return "Default";
        if (consent == 5 && secure == 0) return "NoDim";
        if (consent == 0 && secure == 0) return "NeverNotify";
        return "Custom";
    }

    private UacStatus ReadUac()
    {
        const string path = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
        var lua = Text.ToLong(Reg(path, "EnableLUA"));
        var consent = Text.ToLong(Reg(path, "ConsentPromptBehaviorAdmin"));
        var secure = Text.ToLong(Reg(path, "PromptOnSecureDesktop"));
        return new UacStatus
        {
            Enabled = lua.HasValue ? lua.Value != 0 : (bool?)true,
            ConsentPromptBehaviorAdmin = consent.HasValue ? (int?)consent.Value : null,
            SecureDesktopPrompt = secure.HasValue ? secure.Value != 0 : (bool?)null,
            Level = UacLevel(lua, consent, secure)
        };
    }

    // ----------------------------------------------------------- account policy

    private AccountPolicyStatus ReadAccountPolicy(List<string> limits)
    {
        AccountPolicyRaw raw = null;
        try { raw = _native.GetAccountPolicy(); } catch (Exception) { }
        if (raw == null) { limits.Add("account policy: could not be read"); return null; }
        return new AccountPolicyStatus
        {
            MinimumPasswordLength = raw.MinPasswordLength,
            MaximumPasswordAgeDays = raw.MaxPasswordAgeDays,
            MinimumPasswordAgeDays = raw.MinPasswordAgeDays,
            PasswordHistoryLength = raw.PasswordHistoryLength,
            LockoutThreshold = raw.LockoutThreshold,
            LockoutDurationMinutes = raw.LockoutDurationMinutes,
            LockoutObservationMinutes = raw.LockoutObservationMinutes,
            Scope = "local-machine policy"
        };
    }

    private static string Brief(string error)
    {
        if (string.IsNullOrEmpty(error)) return "unavailable";
        var e = error.ToLowerInvariant();
        if (e.Contains("access") && e.Contains("denied")) return "access denied (requires administrator)";
        if (e.Contains("invalid namespace") || e.Contains("invalid class") || e.Contains("not found")) return "not supported on this Windows version/edition";
        return Text.Truncate(error, 120);
    }
}
