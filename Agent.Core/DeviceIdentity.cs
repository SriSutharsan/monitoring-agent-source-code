using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Agent.Models;

namespace Agent.Core;

public sealed class AgentPaths
{
    public string Root { get; private set; }
    public string LogDirectory { get { return Path.Combine(Root, "logs"); } }
    public string DataDirectory { get { return Path.Combine(Root, "data"); } }
    public string SecureDirectory { get { return Path.Combine(Root, "secure"); } }
    public string ConfigDirectory { get { return Path.Combine(Root, "config"); } }
    public string UpdateDirectory { get { return Path.Combine(Root, "updates"); } }
    public string DatabasePath { get { return Path.Combine(DataDirectory, "agent.db"); } }
    public string IdentityPath { get { return Path.Combine(DataDirectory, "identity.json"); } }
    public string CredentialPath { get { return Path.Combine(SecureDirectory, "credential.bin"); } }
    public string CachedConfigPath { get { return Path.Combine(ConfigDirectory, "remote-config.json"); } }

    public AgentPaths(string root)
    {
        Root = root;
    }

    public static AgentPaths Default()
    {
        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return new AgentPaths(Path.Combine(common, "EndpointAgent"));
    }

    public void EnsureCreated()
    {
        foreach (var d in new[] { Root, LogDirectory, DataDirectory, SecureDirectory, ConfigDirectory, UpdateDirectory })
            Directory.CreateDirectory(d);
    }
}

public static class AtomicFile
{
    /// <summary>Writes via temp file + replace so a crash or power loss never leaves a truncated file.</summary>
    public static void WriteAllText(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content, new UTF8Encoding(false));
        if (File.Exists(path)) File.Replace(tmp, path, null);
        else File.Move(tmp, path);
    }

    public static void WriteAllBytes(string path, byte[] content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, content);
        if (File.Exists(path)) File.Replace(tmp, path, null);
        else File.Move(tmp, path);
    }
}

public class IdentityRecord
{
    public string DeviceId { get; set; }
    public string FingerprintHash { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? FirstRegisteredUtc { get; set; }
}

/// <summary>
/// Produces and persists the stable device id. The id is a random GUID created once and cross-checked against the hardware
/// fingerprint (system UUID + serial). If the fingerprint changes (disk image cloned to a different machine) a new id is issued
/// and the old credential is discarded, so two laptops can never report as the same device.
/// </summary>
public sealed class DeviceIdentityService
{
    private static readonly string[] PlaceholderValues =
    {
        "to be filled by o.e.m.", "to be filled by oem", "default string", "none", "n/a", "not specified", "not applicable",
        "system serial number", "system product name", "0", "123456789", "0123456789", "xxxxxxxx", "unknown", "oem",
        "00000000-0000-0000-0000-000000000000", "ffffffff-ffff-ffff-ffff-ffffffffffff"
    };

    private readonly string _path;
    private readonly IDeviceFingerprintProvider _fingerprint;
    private readonly IClock _clock;
    private IdentityRecord _record;

    public bool WasReset { get; private set; }
    public string DeviceId { get { return _record.DeviceId; } }
    public DateTime? FirstRegisteredUtc { get { return _record.FirstRegisteredUtc; } }

    public DeviceIdentityService(string identityPath, IDeviceFingerprintProvider fingerprint, IClock clock)
    {
        _path = identityPath;
        _fingerprint = fingerprint;
        _clock = clock;
    }

    public static bool IsPlaceholder(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        var v = value.Trim().ToLowerInvariant();
        if (PlaceholderValues.Contains(v)) return true;
        return v.Length > 0 && v.All(c => c == '0' || c == '-' || c == 'f');
    }

    public static string ComputeFingerprintHash(DeviceFingerprint fp)
    {
        if (fp == null) return null;
        var uuid = IsPlaceholder(fp.SystemUuid) ? null : fp.SystemUuid.Trim().ToLowerInvariant();
        var serial = IsPlaceholder(fp.SerialNumber) ? null : fp.SerialNumber.Trim().ToLowerInvariant();
        if (uuid == null && serial == null) return null;         // nothing trustworthy to compare with
        return Hashing.Sha256Hex((uuid ?? "") + "|" + (serial ?? ""));
    }

    public void Initialize()
    {
        var currentHash = ComputeFingerprintHash(SafeFingerprint());
        IdentityRecord existing = null;
        try
        {
            if (File.Exists(_path)) Json.TryDeserialize(File.ReadAllText(_path, Encoding.UTF8), out existing);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        if (existing != null && !string.IsNullOrEmpty(existing.DeviceId))
        {
            bool cloned = existing.FingerprintHash != null && currentHash != null && existing.FingerprintHash != currentHash;
            if (!cloned)
            {
                if (existing.FingerprintHash == null && currentHash != null)
                {
                    existing.FingerprintHash = currentHash;
                    Persist(existing);
                }
                _record = existing;
                return;
            }
            WasReset = true;     // hardware identity changed: treat as a brand new device
        }

        _record = new IdentityRecord
        {
            DeviceId = Guid.NewGuid().ToString("D"),
            FingerprintHash = currentHash,
            CreatedUtc = _clock.UtcNow
        };
        Persist(_record);
    }

    public void MarkRegistered()
    {
        if (_record.FirstRegisteredUtc.HasValue) return;
        _record.FirstRegisteredUtc = _clock.UtcNow;
        Persist(_record);
    }

    public void ClearRegistration()
    {
        _record.FirstRegisteredUtc = null;
        Persist(_record);
    }

    private DeviceFingerprint SafeFingerprint()
    {
        try { return _fingerprint.Get(); }
        catch (Exception) { return null; }
    }

    private void Persist(IdentityRecord r)
    {
        AtomicFile.WriteAllText(_path, Json.SerializeIndented(r));
    }
}
