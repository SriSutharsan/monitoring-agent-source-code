using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Agent.Core;
using Agent.Models;

namespace Agent.Configuration;

/// <summary>
/// Stores the device credential encrypted with Windows DPAPI (machine scope, so the LocalSystem service can read it
/// regardless of which user is logged on). Also bound to extra entropy so other DPAPI blobs cannot be swapped in.
/// The secret is never logged.
/// </summary>
public sealed class DpapiCredentialStore : ICredentialStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("EndpointAgent.Credential.v1");
    private readonly string _path;

    public DpapiCredentialStore(string path)
    {
        _path = path;
    }

    public DeviceCredential Load()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var blob = File.ReadAllBytes(_path);
            var plain = ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.LocalMachine);
            DeviceCredential c;
            return Json.TryDeserialize(Encoding.UTF8.GetString(plain), out c) && !string.IsNullOrEmpty(c.Secret) ? c : null;
        }
        catch (CryptographicException) { return null; }   // blob from another machine / corrupted: treated as "not registered"
        catch (IOException) { return null; }
    }

    public void Save(DeviceCredential credential)
    {
        var plain = Encoding.UTF8.GetBytes(Json.Serialize(credential));
        var blob = ProtectedData.Protect(plain, Entropy, DataProtectionScope.LocalMachine);
        AtomicFile.WriteAllBytes(_path, blob);
    }

    public void Delete()
    {
        try { if (File.Exists(_path)) File.Delete(_path); }
        catch (IOException) { }
    }
}
