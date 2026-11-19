using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Agent.Communication;
using Agent.Configuration;
using Agent.Core;
using Agent.Models;
using Microsoft.Extensions.Logging;

namespace Agent.Service;

/// <summary>
/// Handles a signed update offer: verify the manifest signature, check it is newer and built for this runtime, download over HTTPS,
/// verify the SHA-256 from the SIGNED manifest, then hand the package to the installed update.ps1 which stops the service, swaps the
/// files and rolls back automatically when the new version does not start. Nothing is executed from the manifest itself: the script
/// path is fixed at install time and only receives a local file path and the verified hash.
/// </summary>
public sealed class SelfUpdateHandler : IUpdateHandler
{
    private const long MaxPackageBytes = 300L * 1024 * 1024;
    private readonly SignedEnvelopeVerifier _verifier;
    private readonly AgentPaths _paths;
    private readonly IStateStore _state;
    private readonly IClock _clock;
    private readonly HttpClient _http;
    private readonly string _currentVersion;
    private readonly string _runtime;
    private readonly ILogger _log;
    private int _running;

    public SelfUpdateHandler(SignedEnvelopeVerifier verifier, AgentPaths paths, IStateStore state, IClock clock, HttpClient http,
        string currentVersion, string runtime, ILogger<SelfUpdateHandler> log)
    {
        _verifier = verifier;
        _paths = paths;
        _state = state;
        _clock = clock;
        _http = http;
        _currentVersion = currentVersion;
        _runtime = runtime;
        _log = log;
    }

    public async Task OfferAsync(UpdateOffer offer, CancellationToken ct)
    {
        if (offer == null || offer.Manifest == null) return;
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        try
        {
            UpdateManifest m;
            var r = _verifier.VerifyManifest(offer.Manifest, _clock.UtcNow, out m);
            if (r != VerifyResult.Valid) { _log.LogWarning("Update manifest rejected: {Result}", r); return; }

            var cmp = VersionCompare.Compare(m.Version, _currentVersion);
            if (cmp == null || cmp.Value <= 0) return;                       // not newer: also blocks downgrade/replay of old manifests
            if (!string.IsNullOrEmpty(m.Runtime) && !string.Equals(m.Runtime, _runtime, StringComparison.OrdinalIgnoreCase))
            { _log.LogInformation("Update {Version} targets runtime {Runtime}, this agent is {Mine}; skipped", m.Version, m.Runtime, _runtime); return; }
            if (string.IsNullOrWhiteSpace(m.Sha256) || string.IsNullOrWhiteSpace(m.PackageUrl)) { _log.LogWarning("Update manifest incomplete"); return; }

            Uri uri;
            if (!Uri.TryCreate(m.PackageUrl, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps) { _log.LogWarning("Update package URL must be https"); return; }
            // do not loop on a package that already failed once (update.ps1 writes the marker after a rollback)
            if (_state.Get("update.failed_version") == m.Version
                || File.Exists(Path.Combine(_paths.UpdateDirectory, "failed-" + SafeName(m.Version) + ".txt"))) return;

            var script = Path.Combine(AppContext.BaseDirectory, "update.ps1");
            if (!File.Exists(script)) { _log.LogWarning("update.ps1 not found next to the service; automatic update unavailable"); return; }

            Directory.CreateDirectory(_paths.UpdateDirectory);
            var target = Path.Combine(_paths.UpdateDirectory, "agent-" + SafeName(m.Version) + ".zip");
            _log.LogInformation("Downloading agent update {Version}", m.Version);
            using (var resp = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                var len = resp.Content.Headers.ContentLength;
                if (len.HasValue && len.Value > MaxPackageBytes) throw new InvalidOperationException("update package too large");
                using (var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var dst = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var buf = new byte[81920]; long total = 0; int n;
                    while ((n = await src.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false)) > 0)
                    {
                        total += n;
                        if (total > MaxPackageBytes) throw new InvalidOperationException("update package too large");
                        dst.Write(buf, 0, n);
                    }
                }
            }

            var actual = Hashing.Sha256Hex(File.ReadAllBytes(target));
            if (!string.Equals(actual, m.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(target);
                _state.Set("update.failed_version", m.Version);
                _log.LogError("Update package hash mismatch; package deleted");
                return;
            }
            if (m.SizeBytes.HasValue && new FileInfo(target).Length != m.SizeBytes.Value)
            {
                File.Delete(target);
                _state.Set("update.failed_version", m.Version);
                _log.LogError("Update package size mismatch; package deleted");
                return;
            }

            _state.Set("update.pending_version", m.Version);
            _log.LogInformation("Update {Version} verified; handing over to update.ps1", m.Version);
            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + script + "\" -PackagePath \"" + target + "\" -Sha256 " + actual + " -ExpectedVersion " + SafeName(m.Version))
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            Process.Start(psi);                                                // the script stops this service; it must outlive us
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _log.LogWarning(ex, "Agent update failed"); }
        finally { Interlocked.Exchange(ref _running, 0); }
    }

    private static string SafeName(string v)
    {
        var chars = (v ?? "").ToCharArray();
        for (int i = 0; i < chars.Length; i++) if (!(char.IsLetterOrDigit(chars[i]) || chars[i] == '.' || chars[i] == '-')) chars[i] = '_';
        return new string(chars);
    }
}
