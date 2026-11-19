using System;
using System.Runtime.InteropServices;
using Agent.Core;
using Microsoft.Win32.SafeHandles;

namespace Agent.Collectors.Platform;

/// <summary>
/// IOCTL_STORAGE_QUERY_PROPERTY on \\.\PhysicalDriveN: bus type and "seek penalty" (spinning disk) detection. Works on Windows 7
/// where the Storage WMI namespace does not exist. The handle is opened with access 0, so no administrator rights are required.
/// </summary>
public sealed class DiskIoctl : IDiskIoctl
{
    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x2D1400;
    private const int StorageDeviceProperty = 0;
    private const int StorageDeviceSeekPenaltyProperty = 7;
    private const int PropertyStandardQuery = 0;

    [StructLayout(LayoutKind.Sequential)] private struct STORAGE_PROPERTY_QUERY { public int PropertyId; public int QueryType; public byte AdditionalParameters; }
    [StructLayout(LayoutKind.Sequential)] private struct DEVICE_SEEK_PENALTY_DESCRIPTOR { public uint Version; public uint Size; [MarshalAs(UnmanagedType.U1)] public bool IncursSeekPenalty; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, ref STORAGE_PROPERTY_QUERY inBuf, int inSize, IntPtr outBuf, int outSize, out int returned, IntPtr overlapped);

    public DiskIoctlInfo Query(int index)
    {
        try
        {
            using (var h = CreateFile(@"\\.\PhysicalDrive" + index, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
            {
                if (h.IsInvalid) return null;
                var info = new DiskIoctlInfo();

                var q = new STORAGE_PROPERTY_QUERY { PropertyId = StorageDeviceSeekPenaltyProperty, QueryType = PropertyStandardQuery };
                IntPtr buf = Marshal.AllocHGlobal(64);
                try
                {
                    int ret;
                    if (DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, ref q, Marshal.SizeOf(typeof(STORAGE_PROPERTY_QUERY)), buf, 64, out ret, IntPtr.Zero))
                        info.IncursSeekPenalty = ((DEVICE_SEEK_PENALTY_DESCRIPTOR)Marshal.PtrToStructure(buf, typeof(DEVICE_SEEK_PENALTY_DESCRIPTOR))).IncursSeekPenalty;

                    q.PropertyId = StorageDeviceProperty;
                    IntPtr big = Marshal.AllocHGlobal(1024);
                    try
                    {
                        if (DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, ref q, Marshal.SizeOf(typeof(STORAGE_PROPERTY_QUERY)), big, 1024, out ret, IntPtr.Zero))
                            info.BusType = Marshal.ReadInt32(big, 28);        // STORAGE_DEVICE_DESCRIPTOR.BusType
                    }
                    finally { Marshal.FreeHGlobal(big); }
                }
                finally { Marshal.FreeHGlobal(buf); }
                return info;
            }
        }
        catch (Exception) { return null; }
    }
}
