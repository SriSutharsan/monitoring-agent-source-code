using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Agent.Models;

namespace Agent.Core;

/// <summary>
/// Evaluates configurable threshold rules on every payload. Alerts are state-based (raised / cleared) so a condition that stays
/// true does not re-alert every collection cycle. Sustained-sample counters prevent one-off spikes from alerting.
/// Alert state is persisted so an agent restart does not re-raise (or forget) active alerts.
/// </summary>
public sealed class AlertEvaluator
{
    private readonly IConfigProvider _config;
    private readonly IStateStore _state;
    private readonly Dictionary<string, string> _active = new Dictionary<string, string>();
    private readonly Dictionary<string, int> _streaks = new Dictionary<string, int>();
    private readonly Dictionary<string, int> _processStreaks = new Dictionary<string, int>();
    private readonly List<DateTime> _failedLogons = new List<DateTime>();
    private readonly List<DateTime> _networkDrops = new List<DateTime>();
    private readonly object _gate = new object();

    public AlertEvaluator(IConfigProvider config, IStateStore state)
    {
        _config = config;
        _state = state;
    }

    public IReadOnlyList<AlertRecord> Evaluate(string category, object payload, DateTime nowUtc)
    {
        var output = new List<AlertRecord>();
        lock (_gate)
        {
            var t = _config.Current.Thresholds ?? new ThresholdConfig();
            var storage = payload as StorageInventory;
            var perf = payload as PerformanceMetrics;
            var net = payload as NetworkInfo;
            var sec = payload as SecurityStatus;
            var sw = payload as SoftwareInventory;
            var ev = payload as WindowsEventBatch;
            var health = payload as DeviceHealth;

            if (storage != null) EvaluateStorage(storage, t, nowUtc, output);
            else if (perf != null) EvaluatePerformance(perf, t, nowUtc, output);
            else if (net != null) EvaluateNetwork(net, t, nowUtc, output);
            else if (sec != null) EvaluateSecurity(sec, t, nowUtc, output);
            else if (sw != null) EvaluateSoftware(sw, nowUtc, output);
            else if (ev != null) EvaluateEvents(ev, t, nowUtc, output);
            else if (health != null) EvaluateHealth(health, t, nowUtc, output);
        }
        return output;
    }

    // ---------------------------------------------------------------- storage

    private void EvaluateStorage(StorageInventory s, ThresholdConfig t, DateTime now, List<AlertRecord> o)
    {
        if (s.Volumes != null)
        {
            foreach (var v in s.Volumes.Where(x => x.DriveType == "Fixed" && (x.TotalBytes ?? 0) > 0))
            {
                var pct = v.UsedPercent ?? (100.0 * (v.UsedBytes ?? 0) / v.TotalBytes.Value);
                string sev = pct >= t.StorageCriticalPercent ? Severity.Critical : pct >= t.StorageWarningPercent ? Severity.Warning : null;
                Condition(o, now, "storage.utilization", v.DriveLetter, sev,
                    string.Format(CultureInfo.InvariantCulture, "Drive {0} is {1:0.#}% full", v.DriveLetter, pct),
                    Num.Round(pct), sev == Severity.Critical ? t.StorageCriticalPercent : t.StorageWarningPercent);

                var free = v.FreeBytes ?? 0;
                var freePct = 100.0 * free / v.TotalBytes.Value;
                bool low = free < t.CriticalFreeBytes || freePct < t.CriticalFreePercent;
                Condition(o, now, "storage.free_space", v.DriveLetter, low ? Severity.Critical : null,
                    string.Format(CultureInfo.InvariantCulture, "Drive {0} has only {1:0.0} GB free ({2:0.#}%)", v.DriveLetter, free / 1073741824.0, freePct),
                    Num.Round(free / 1073741824.0, 2), t.CriticalFreeBytes / 1073741824.0);
            }
        }
        if (s.Disks != null)
        {
            foreach (var d in s.Disks)
            {
                var subject = d.DeviceId ?? d.Model;
                string sev = null;
                string why = null;
                if (d.SmartPredictFailure == true) { sev = Severity.Critical; why = "SMART predicts imminent failure"; }
                else if (string.Equals(d.HealthStatus, "Unhealthy", StringComparison.OrdinalIgnoreCase)) { sev = Severity.Critical; why = "disk reports Unhealthy"; }
                else if (string.Equals(d.HealthStatus, "Warning", StringComparison.OrdinalIgnoreCase)) { sev = Severity.Warning; why = "disk reports Warning"; }
                Condition(o, now, "storage.disk_health", subject, sev, "Disk " + d.Model + ": " + why, null, null);

                bool hot = d.TemperatureC.HasValue && d.TemperatureC.Value >= t.DiskTemperatureC;
                Condition(o, now, "storage.disk_temperature", subject, hot ? Severity.Warning : null,
                    "Disk " + d.Model + " temperature is " + d.TemperatureC + " C", d.TemperatureC, t.DiskTemperatureC);
            }
        }
    }

    // ------------------------------------------------------------ performance

    private void EvaluatePerformance(PerformanceMetrics p, ThresholdConfig t, DateTime now, List<AlertRecord> o)
    {
        int need = Math.Max(1, t.SustainedSamples);

        if (p.Cpu != null)
        {
            bool high = Sustained("cpu", p.Cpu.UsagePercent >= t.CpuPercent, need);
            Condition(o, now, "performance.cpu_high", null, high ? Severity.Warning : null,
                string.Format(CultureInfo.InvariantCulture, "CPU usage is {0:0.#}%", p.Cpu.UsagePercent), Num.Round(p.Cpu.UsagePercent), t.CpuPercent);
        }
        if (p.Memory != null)
        {
            bool high = Sustained("ram", p.Memory.UsedPercent >= t.RamPercent, need);
            Condition(o, now, "performance.ram_high", null, high ? Severity.Warning : null,
                string.Format(CultureInfo.InvariantCulture, "RAM usage is {0:0.#}%", p.Memory.UsedPercent), Num.Round(p.Memory.UsedPercent), t.RamPercent);
        }
        if (p.Cpu != null && p.Memory != null)
        {
            // degradation = at least two resources under pressure at the same time for a sustained period
            double diskBusy = _streaks.ContainsKey("diskbusy-now") ? _streaks["diskbusy-now"] : 0;
            int stressed = (p.Cpu.UsagePercent >= t.CpuPercent - 5 ? 1 : 0)
                         + (p.Memory.UsedPercent >= t.RamPercent - 5 ? 1 : 0)
                         + (diskBusy > 0 ? 1 : 0);
            bool degraded = Sustained("degraded", stressed >= 2, need * 2);
            Condition(o, now, "performance.degraded", null, degraded ? Severity.Warning : null,
                "Device performance is degraded: multiple resources (CPU / RAM / disk) are under sustained pressure", stressed, 2);
        }
        if (p.TopCpuProcesses != null)
        {
            var high = p.TopCpuProcesses.Where(x => x.CpuPercent >= t.ProcessCpuPercent).GroupBy(x => x.Name)
                .ToDictionary(g => g.Key, g => g.Sum(x => x.CpuPercent));
            foreach (var name in high.Keys.ToList())
            {
                int n = _processStreaks.ContainsKey(name) ? _processStreaks[name] + 1 : 1;
                _processStreaks[name] = n;
                bool raise = n >= Math.Max(1, t.ProcessSustainedSamples);
                Condition(o, now, "performance.process_cpu", name, raise ? Severity.Warning : null,
                    string.Format(CultureInfo.InvariantCulture, "Process {0} has used {1:0}% CPU for {2} consecutive samples", name, high[name], n),
                    Num.Round(high[name]), t.ProcessCpuPercent);
            }
            foreach (var name in _processStreaks.Keys.Where(k => !high.ContainsKey(k)).ToList())
            {
                _processStreaks.Remove(name);
                Condition(o, now, "performance.process_cpu", name, null, "Process CPU usage returned to normal", null, null);
            }
        }
        if (p.Disks != null)
        {
            bool anyBusy = false;
            foreach (var d in p.Disks)
            {
                bool hot = d.BusyPercent >= t.DiskBusyPercent || d.QueueLength >= t.DiskQueueLength;
                anyBusy |= d.BusyPercent >= t.DiskBusyPercent - 5;
                bool raise = Sustained("disk:" + d.Name, hot, need);
                Condition(o, now, "performance.disk_activity", d.Name, raise ? Severity.Warning : null,
                    string.Format(CultureInfo.InvariantCulture, "Disk {0} is saturated ({1:0}% busy, queue {2:0.#})", d.Name, d.BusyPercent, d.QueueLength),
                    Num.Round(d.BusyPercent), t.DiskBusyPercent);
            }
            _streaks["diskbusy-now"] = anyBusy ? 1 : 0;
        }
        if (p.Network != null)
        {
            var n = p.Network;
            bool latency = n.LatencyMs.HasValue && n.LatencyMs.Value >= t.LatencyMs;
            Condition(o, now, "network.latency_high", null, Sustained("latency", latency, 2) ? Severity.Warning : null,
                string.Format(CultureInfo.InvariantCulture, "Network latency is {0:0} ms", n.LatencyMs), n.LatencyMs, t.LatencyMs);

            bool loss = n.PacketLossPercent.HasValue && n.PacketLossPercent.Value >= t.PacketLossPercent;
            Condition(o, now, "network.packet_loss", null, Sustained("loss", loss, 2) ? Severity.Warning : null,
                string.Format(CultureInfo.InvariantCulture, "Packet loss is {0:0.#}%", n.PacketLossPercent), n.PacketLossPercent, t.PacketLossPercent);

            Condition(o, now, "network.internet_unreachable", null,
                Sustained("internet", !n.InternetReachable, 2) ? Severity.Warning : null, "Internet connectivity lost", null, null);

            if (n.Events != null)
                foreach (var e in n.Events)
                    if (e.Type == "adapter_down" || e.Type == "internet_lost") _networkDrops.Add(e.TimestampUtc);
            _networkDrops.RemoveAll(x => x < now - TimeSpan.FromMinutes(t.NetworkFlapWindowMinutes));
            Condition(o, now, "network.unstable_connection", null,
                _networkDrops.Count >= t.NetworkFlapCount ? Severity.Warning : null,
                _networkDrops.Count + " network disconnections in the last " + t.NetworkFlapWindowMinutes + " minutes",
                _networkDrops.Count, t.NetworkFlapCount);
        }
        if (p.Battery != null && p.Battery.Present)
        {
            var b = p.Battery;
            bool low = b.ChargePercent.HasValue && b.ChargePercent.Value <= t.BatteryLowPercent && b.ChargingStatus == "Discharging";
            Condition(o, now, "battery.low", null, low ? Severity.Warning : null, "Battery is at " + b.ChargePercent + "% and discharging", b.ChargePercent, t.BatteryLowPercent);
            bool worn = b.HealthPercent.HasValue && b.HealthPercent.Value < t.BatteryHealthLowPercent;
            Condition(o, now, "battery.health", null, worn ? Severity.Warning : null,
                "Battery health is " + Num.Round(b.HealthPercent) + "% of design capacity", Num.Round(b.HealthPercent), t.BatteryHealthLowPercent);
        }
        if (p.TemperatureC.HasValue)
            Condition(o, now, "system.temperature", null, p.TemperatureC.Value >= t.SystemTemperatureC ? Severity.Warning : null,
                "System temperature is " + Num.Round(p.TemperatureC.Value) + " C", Num.Round(p.TemperatureC), t.SystemTemperatureC);
    }

    // ---------------------------------------------------------------- network

    private void EvaluateNetwork(NetworkInfo n, ThresholdConfig t, DateTime now, List<AlertRecord> o)
    {
        if (n.Wifi == null || !n.Wifi.AdapterPresent) return;
        bool weak = n.Wifi.Connected && n.Wifi.SignalQualityPercent.HasValue && n.Wifi.SignalQualityPercent.Value <= 25;
        Condition(o, now, "network.wifi_signal_weak", null, Sustained("wifi", weak, 3) ? Severity.Warning : null,
            "Wi-Fi signal is weak (" + n.Wifi.SignalQualityPercent + "% on " + n.Wifi.Ssid + ")", n.Wifi.SignalQualityPercent, 25);
    }

    // --------------------------------------------------------------- security

    private void EvaluateSecurity(SecurityStatus s, ThresholdConfig t, DateTime now, List<AlertRecord> o)
    {
        var av = s.Antivirus;
        if (av != null)
        {
            var d = av.Defender;
            bool known = (av.Products != null && av.Products.Count > 0) || (d != null && d.Present);
            bool anyEnabled = (av.Products != null && av.Products.Any(p => p.Enabled == true)) || (d != null && d.AntivirusEnabled == true);
            Condition(o, now, "security.antivirus_disabled", null, known && !anyEnabled ? Severity.Critical : null,
                "No enabled antivirus product detected", null, null);

            if (d != null && d.Present)
            {
                Condition(o, now, "security.realtime_protection_off", null,
                    d.AntivirusEnabled == true && d.RealTimeProtectionEnabled == false ? Severity.Warning : null,
                    "Defender real-time protection is turned off", null, null);
                bool stale = d.SignatureAgeDays.HasValue && d.SignatureAgeDays.Value > t.SignatureMaxAgeDays;
                Condition(o, now, "security.antivirus_signatures_stale", null, stale ? Severity.Warning : null,
                    "Antivirus signatures are " + d.SignatureAgeDays + " days old", d.SignatureAgeDays, t.SignatureMaxAgeDays);

                if (d.RecentThreats != null)
                    foreach (var th in d.RecentThreats)
                    {
                        var id = Hashing.Short((th.ThreatName ?? "") + "|" + th.DetectedUtc, 16);
                        if (_state.Get("threat:" + id) != null) continue;
                        _state.Set("threat:" + id, now.ToString("o", CultureInfo.InvariantCulture));
                        o.Add(new AlertRecord
                        {
                            AlertKey = "security.threat_detected|" + id, Rule = "security.threat_detected", Subject = th.ThreatName,
                            State = "event", Severity = th.IsPotentiallyUnwanted ? Severity.Warning : Severity.Critical, TimestampUtc = now,
                            Message = (th.IsPotentiallyUnwanted ? "Potentially unwanted software detected: " : "Threat detected: ") + th.ThreatName,
                            Metadata = new Dictionary<string, string> { { "detectedUtc", th.DetectedUtc.HasValue ? th.DetectedUtc.Value.ToString("o", CultureInfo.InvariantCulture) : "" }, { "category", th.Category ?? "" } }
                        });
                    }
            }
        }
        if (s.Firewall != null)
        {
            var f = s.Firewall;
            Condition(o, now, "security.firewall_disabled", "domain", f.DomainProfileEnabled == false ? Severity.Warning : null, "Windows Firewall is off for the Domain profile", null, null);
            Condition(o, now, "security.firewall_disabled", "private", f.PrivateProfileEnabled == false ? Severity.Warning : null, "Windows Firewall is off for the Private profile", null, null);
            Condition(o, now, "security.firewall_disabled", "public", f.PublicProfileEnabled == false ? Severity.Critical : null, "Windows Firewall is off for the Public profile", null, null);
        }
        if (s.Encryption != null)
            Condition(o, now, "security.system_drive_unencrypted", null,
                s.Encryption.BitLockerState == "Available" && s.Encryption.SystemDriveProtected == false ? Severity.Warning : null,
                "System drive is not protected by BitLocker", null, null);
        Condition(o, now, "security.secure_boot_disabled", null, s.SecureBoot == "Disabled" ? Severity.Info : null, "Secure Boot is disabled", null, null);
        if (s.Uac != null)
            Condition(o, now, "security.uac_disabled", null, s.Uac.Enabled == false ? Severity.Warning : null, "User Account Control is disabled", null, null);

        if (s.WindowsUpdate != null)
        {
            bool stale = s.WindowsUpdate.DaysSinceLastSuccessfulInstall.HasValue && s.WindowsUpdate.DaysSinceLastSuccessfulInstall.Value > 60;
            Condition(o, now, "security.updates_stale", null, stale ? Severity.Warning : null,
                "No Windows updates installed for " + s.WindowsUpdate.DaysSinceLastSuccessfulInstall + " days", s.WindowsUpdate.DaysSinceLastSuccessfulInstall, 60);
        }
        if (s.PendingRestart != null)
        {
            const string sinceKey = "pending_restart.since";
            if (s.PendingRestart.Pending)
            {
                var since = _state.Get(sinceKey);
                DateTime sinceUtc;
                if (since == null || !DateTime.TryParse(since, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out sinceUtc))
                {
                    sinceUtc = now;
                    _state.Set(sinceKey, now.ToString("o", CultureInfo.InvariantCulture));
                }
                var days = (now - sinceUtc).TotalDays;
                Condition(o, now, "security.restart_pending_too_long", null, days >= t.PendingRestartDays ? Severity.Warning : null,
                    "A restart has been pending for " + (int)days + " days (" + string.Join(", ", s.PendingRestart.Reasons ?? new List<string>()) + ")", Num.Round(days), t.PendingRestartDays);
            }
            else
            {
                _state.Delete(sinceKey);
                Condition(o, now, "security.restart_pending_too_long", null, null, "Restart no longer pending", null, null);
            }
        }
    }

    // --------------------------------------------------------------- software

    private void EvaluateSoftware(SoftwareInventory s, DateTime now, List<AlertRecord> o)
    {
        var apps = s.Applications ?? new List<InstalledApplication>();
        Reconcile(o, now, "software.unauthorized", Severity.Warning,
            apps.Where(a => a.IsUnauthorized == true).Select(a => new[] { a.Key, "Unauthorized application installed: " + a.Name + " " + a.Version + (a.PolicyMatch != null ? " (policy: " + a.PolicyMatch + ")" : "") }));
        Reconcile(o, now, "software.outdated", Severity.Info,
            apps.Where(a => a.IsOutdated == true).Select(a => new[] { a.Key, "Outdated application: " + a.Name + " " + a.Version + " (minimum " + a.MinimumRequiredVersion + ")" }));
        Reconcile(o, now, "software.potentially_unwanted", Severity.Warning,
            apps.Where(a => a.IsPotentiallyUnwanted == true).Select(a => new[] { a.Key, "Potentially unwanted application: " + a.Name }));
    }

    /// <summary>Raises alerts for items present, clears alerts (from a persisted index) for items that disappeared.</summary>
    private void Reconcile(List<AlertRecord> o, DateTime now, string rule, string severity, IEnumerable<string[]> current)
    {
        var present = current.GroupBy(c => c[0]).ToDictionary(g => g.Key, g => g.First()[1]);
        var idxKey = "alertidx:" + rule;
        var previous = new HashSet<string>((_state.Get(idxKey) ?? "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries));
        foreach (var kv in present) Condition(o, now, rule, kv.Key, severity, kv.Value, null, null);
        foreach (var gone in previous.Where(p => !present.ContainsKey(p))) Condition(o, now, rule, gone, null, "No longer present", null, null);
        _state.Set(idxKey, string.Join("\n", present.Keys));
    }

    // ----------------------------------------------------------------- events

    private static readonly Dictionary<string, string> EventAlertSeverity = new Dictionary<string, string>
    {
        { "unexpected_shutdown", Severity.Warning },
        { "bsod", Severity.Critical },
        { "antivirus_disabled", Severity.Critical },
        { "security_service_stopped", Severity.Critical },
        { "firewall_change", Severity.Warning },
        { "audit_log_cleared", Severity.Critical },
        { "account_lockout", Severity.Warning },
        { "hardware_error", Severity.Warning }
    };

    private void EvaluateEvents(WindowsEventBatch b, ThresholdConfig t, DateTime now, List<AlertRecord> o)
    {
        if (b.Events == null) return;
        foreach (var e in b.Events)
        {
            if (e.Category == "failed_logon") _failedLogons.Add(e.TimestampUtc);
            string sev;
            if (e.Category != null && EventAlertSeverity.TryGetValue(e.Category, out sev))
            {
                o.Add(new AlertRecord
                {
                    AlertKey = "event." + e.Category + "|" + e.Log + ":" + e.RecordId, Rule = "event." + e.Category, Subject = e.Source,
                    State = "event", Severity = sev, TimestampUtc = e.TimestampUtc, Message = Text.Truncate(e.Message, 300),
                    Metadata = new Dictionary<string, string> { { "eventId", e.EventId.ToString(CultureInfo.InvariantCulture) }, { "log", e.Log ?? "" } }
                });
            }
        }

        _failedLogons.RemoveAll(x => x < now - TimeSpan.FromMinutes(t.FailedLogonWindowMinutes));
        if (_failedLogons.Count >= t.FailedLogonCount)
        {
            DateTime last;
            var lastText = _state.Get("failed_logon.last_alert");
            bool cooled = lastText == null
                          || !DateTime.TryParse(lastText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out last)
                          || (now - last) > TimeSpan.FromMinutes(t.FailedLogonWindowMinutes);
            if (cooled)
            {
                _state.Set("failed_logon.last_alert", now.ToString("o", CultureInfo.InvariantCulture));
                var accounts = b.Events.Where(e => e.Category == "failed_logon" && e.Metadata != null && e.Metadata.ContainsKey("targetUser"))
                    .Select(e => e.Metadata["targetUser"]).Distinct().Take(5);
                o.Add(new AlertRecord
                {
                    AlertKey = "security.repeated_failed_logons|" + now.Ticks, Rule = "security.repeated_failed_logons", State = "event", Severity = Severity.Warning,
                    TimestampUtc = now, Value = _failedLogons.Count, Threshold = t.FailedLogonCount,
                    Message = _failedLogons.Count + " failed sign-in attempts in the last " + t.FailedLogonWindowMinutes + " minutes",
                    Metadata = new Dictionary<string, string> { { "accounts", string.Join(",", accounts) } }
                });
            }
        }
    }

    private void EvaluateHealth(DeviceHealth h, ThresholdConfig t, DateTime now, List<AlertRecord> o)
    {
        Condition(o, now, "device.health", null,
            h.Status == "critical" ? Severity.Critical : h.Status == "warning" ? Severity.Warning : null,
            "Device health is " + h.Status + " (score " + h.Score + ")", h.Score, null);
        if (h.RecurringProblems != null)
            foreach (var p in h.RecurringProblems)
            {
                var key = "recurring:" + p.Key;
                DateTime last;
                var text = _state.Get(key);
                if (text != null && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out last) && now - last < TimeSpan.FromHours(24)) continue;
                _state.Set(key, now.ToString("o", CultureInfo.InvariantCulture));
                o.Add(new AlertRecord
                {
                    AlertKey = "problem.recurring|" + p.Key + "|" + now.Ticks, Rule = "problem.recurring", Subject = p.Source, State = "event",
                    Severity = Severity.Warning, TimestampUtc = now, Value = p.Occurrences, Threshold = t.RecurringProblemCount,
                    Message = "Recurring problem: " + p.Description + " (" + p.Occurrences + " occurrences)"
                });
            }
    }

    // ---------------------------------------------------------------- helpers

    private bool Sustained(string key, bool condition, int needed)
    {
        int n = condition ? (_streaks.ContainsKey(key) ? _streaks[key] + 1 : 1) : 0;
        _streaks[key] = n;
        return condition && n >= needed;
    }

    private string GetActive(string key)
    {
        string v;
        if (_active.TryGetValue(key, out v)) return v == "" ? null : v;
        var stored = _state.Get("alert:" + key);
        _active[key] = stored ?? "";
        return stored;
    }

    private void Condition(List<AlertRecord> o, DateTime now, string rule, string subject, string severity, string message, double? value, double? threshold)
    {
        var key = subject == null ? rule : rule + "|" + subject;
        var current = GetActive(key);
        if (severity != null)
        {
            if (current == severity) return;
            _active[key] = severity;
            _state.Set("alert:" + key, severity);
            o.Add(new AlertRecord { AlertKey = key, Rule = rule, Subject = subject, State = "raised", Severity = severity, Message = message, Value = value, Threshold = threshold, TimestampUtc = now });
        }
        else if (current != null)
        {
            _active[key] = "";
            _state.Delete("alert:" + key);
            o.Add(new AlertRecord { AlertKey = key, Rule = rule, Subject = subject, State = "cleared", Severity = Severity.Info, Message = "Resolved: " + rule + (subject != null ? " (" + subject + ")" : ""), TimestampUtc = now });
        }
    }
}
