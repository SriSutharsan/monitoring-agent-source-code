using System;
using System.Collections.Generic;
using Agent.Core;
using Microsoft.Win32;

namespace Agent.Collectors.Platform;

/// <summary>Read-only registry access. Every call is exception safe: a missing key or denied access returns null / empty.</summary>
public sealed class RegistryReader : IRegistryReader
{
    public object GetValue(RegistryHiveKind hive, RegistryViewKind view, string subKey, string name)
    {
        try { using (var k = Open(hive, view, subKey)) return k == null ? null : k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames); }
        catch (Exception) { return null; }
    }

    public IReadOnlyList<string> GetSubKeyNames(RegistryHiveKind hive, RegistryViewKind view, string subKey)
    {
        try { using (var k = Open(hive, view, subKey)) return k == null ? new string[0] : k.GetSubKeyNames(); }
        catch (Exception) { return new string[0]; }
    }

    public IReadOnlyList<string> GetValueNames(RegistryHiveKind hive, RegistryViewKind view, string subKey)
    {
        try { using (var k = Open(hive, view, subKey)) return k == null ? new string[0] : k.GetValueNames(); }
        catch (Exception) { return new string[0]; }
    }

    public bool KeyExists(RegistryHiveKind hive, RegistryViewKind view, string subKey)
    {
        try { using (var k = Open(hive, view, subKey)) return k != null; }
        catch (Exception) { return false; }
    }

    private static RegistryKey Open(RegistryHiveKind hive, RegistryViewKind view, string subKey)
    {
        var v = view == RegistryViewKind.View32 ? RegistryView.Registry32 : view == RegistryViewKind.View64 ? RegistryView.Registry64 : RegistryView.Default;
        RegistryHive h = hive == RegistryHiveKind.Users ? RegistryHive.Users : hive == RegistryHiveKind.CurrentUser ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;
        using (var baseKey = RegistryKey.OpenBaseKey(h, v))
            return string.IsNullOrEmpty(subKey) ? baseKey.OpenSubKey("") : baseKey.OpenSubKey(subKey, false);
    }
}
