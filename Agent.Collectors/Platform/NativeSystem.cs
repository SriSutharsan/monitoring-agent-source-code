using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Agent.Core;

namespace Agent.Collectors.Platform;

/// <summary>Direct Win32 calls: cheap, no WMI, available since Windows XP/7 (GetTickCount64 and GlobalMemoryStatusEx are Vista+).</summary>
public sealed class NativeSystem : INativeSystem
{
    [StructLayout(LayoutKind.Sequential)] private struct FILETIME { public uint Low; public uint High; public ulong Value { get { return ((ulong)High << 32) | Low; } } }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);
    [DllImport("kernel32.dll")] private static extern ulong GetTickCount64();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private class MEMORYSTATUSEX
    {
        public uint dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
        public uint dwMemoryLoad; public ulong ullTotalPhys; public ulong ullAvailPhys; public ulong ullTotalPageFile; public ulong ullAvailPageFile;
        public ulong ullTotalVirtual; public ulong ullAvailVirtual; public ulong ullAvailExtendedVirtual;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)] private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS { public byte ACLineStatus; public byte BatteryFlag; public byte BatteryLifePercent; public byte SystemStatusFlag; public int BatteryLifeTime; public int BatteryFullLifeTime; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool IsWow64Process(IntPtr process, out bool wow64);

    // WTS sessions
    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSEnumerateSessions(IntPtr server, int reserved, int version, out IntPtr info, out int count);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr p);
    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSQuerySessionInformation(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytes);
    [StructLayout(LayoutKind.Sequential)] private struct WTS_SESSION_INFO { public int SessionId; public IntPtr WinStationName; public int State; }

    // account policy (local security policy, no admin rights required for level 0 / 3 reads)
    [DllImport("netapi32.dll")] private static extern int NetUserModalsGet(string server, int level, out IntPtr buf);
    [DllImport("netapi32.dll")] private static extern int NetApiBufferFree(IntPtr buf);
    [StructLayout(LayoutKind.Sequential)] private struct USER_MODALS_INFO_0 { public uint min_passwd_len; public uint max_passwd_age; public uint min_passwd_age; public uint force_logoff; public uint password_hist_len; }
    [StructLayout(LayoutKind.Sequential)] private struct USER_MODALS_INFO_3 { public uint lockout_duration; public uint lockout_observation_window; public uint lockout_threshold; }

    public bool TryGetCpuTimes(out ulong idle, out ulong kernel, out ulong user)
    {
        FILETIME i, k, u;
        idle = kernel = user = 0;
        if (!GetSystemTimes(out i, out k, out u)) return false;
        idle = i.Value; kernel = k.Value; user = u.Value;        // kernel time INCLUDES idle time
        return true;
    }

    public bool TryGetMemory(out SystemMemoryStatus status)
    {
        status = null;
        var m = new MEMORYSTATUSEX();
        if (!GlobalMemoryStatusEx(m)) return false;
        status = new SystemMemoryStatus { TotalPhysical = (long)m.ullTotalPhys, AvailablePhysical = (long)m.ullAvailPhys, TotalCommit = (long)m.ullTotalPageFile, AvailableCommit = (long)m.ullAvailPageFile };
        return true;
    }

    public long TickCount64 { get { try { return (long)GetTickCount64(); } catch (EntryPointNotFoundException) { return Environment.TickCount; } } }

    public PowerStatus GetPowerStatus()
    {
        SYSTEM_POWER_STATUS s;
        if (!GetSystemPowerStatus(out s)) return new PowerStatus();
        return new PowerStatus
        {
            BatteryPresent = (s.BatteryFlag & 128) == 0 && s.BatteryFlag != 255,
            OnAcPower = s.ACLineStatus == 255 ? (bool?)null : s.ACLineStatus == 1,
            ChargePercent = s.BatteryLifePercent == 255 ? (int?)null : s.BatteryLifePercent,
            Charging = (s.BatteryFlag & 8) != 0,
            SecondsRemaining = s.BatteryLifeTime < 0 ? (int?)null : s.BatteryLifeTime
        };
    }

    public IReadOnlyList<WtsSession> GetSessions()
    {
        var list = new List<WtsSession>();
        IntPtr info; int count;
        if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out info, out count)) return list;
        try
        {
            int size = Marshal.SizeOf(typeof(WTS_SESSION_INFO));
            for (int i = 0; i < count; i++)
            {
                var si = (WTS_SESSION_INFO)Marshal.PtrToStructure(new IntPtr(info.ToInt64() + (long)i * size), typeof(WTS_SESSION_INFO));
                if (si.SessionId == 0) continue;                       // services session
                var user = Query(si.SessionId, 5); var domain = Query(si.SessionId, 7);
                if (string.IsNullOrEmpty(user)) continue;
                list.Add(new WtsSession { SessionId = si.SessionId, User = user, Domain = domain, Station = Marshal.PtrToStringAuto(si.WinStationName), State = StateName(si.State) });
            }
        }
        finally { WTSFreeMemory(info); }
        return list;
    }

    private static string Query(int session, int infoClass)
    {
        IntPtr buf; int bytes;
        if (!WTSQuerySessionInformation(IntPtr.Zero, session, infoClass, out buf, out bytes)) return null;
        try { return Marshal.PtrToStringAuto(buf); } finally { WTSFreeMemory(buf); }
    }

    private static string StateName(int s)
    {
        switch (s) { case 0: return "active"; case 1: return "connected"; case 4: return "disconnected"; case 5: return "idle"; case 6: return "listening"; default: return "other"; }
    }

    public AccountPolicyRaw GetAccountPolicy()
    {
        var p = new AccountPolicyRaw();
        IntPtr buf;
        if (NetUserModalsGet(null, 0, out buf) == 0)
        {
            try
            {
                var m = (USER_MODALS_INFO_0)Marshal.PtrToStructure(buf, typeof(USER_MODALS_INFO_0));
                p.MinPasswordLength = (int)m.min_passwd_len;
                p.MaxPasswordAgeDays = m.max_passwd_age == uint.MaxValue ? 0 : (int)(m.max_passwd_age / 86400);   // 0 = never expires
                p.MinPasswordAgeDays = (int)(m.min_passwd_age / 86400);
                p.PasswordHistoryLength = (int)m.password_hist_len;
            }
            finally { NetApiBufferFree(buf); }
        }
        if (NetUserModalsGet(null, 3, out buf) == 0)
        {
            try
            {
                var m = (USER_MODALS_INFO_3)Marshal.PtrToStructure(buf, typeof(USER_MODALS_INFO_3));
                p.LockoutThreshold = (int)m.lockout_threshold;
                p.LockoutDurationMinutes = (int)(m.lockout_duration / 60);
                p.LockoutObservationMinutes = (int)(m.lockout_observation_window / 60);
            }
            finally { NetApiBufferFree(buf); }
        }
        return p;
    }

    public bool IsElevated()
    {
        try
        {
            using (var id = WindowsIdentity.GetCurrent())
                return id.IsSystem || new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception) { return false; }
    }

    public bool IsWow64OperatingSystem()
    {
        if (IntPtr.Size == 8) return true;
        try { bool w; return IsWow64Process(System.Diagnostics.Process.GetCurrentProcess().Handle, out w) && w; }
        catch (Exception) { return false; }
    }
}
