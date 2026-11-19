using System;
using System.Collections.Generic;
using System.Linq;
using Agent.Collectors;
using Agent.Collectors.Hardware;
using Agent.Collectors.Network;
using Agent.Collectors.Performance;
using Agent.Collectors.Storage;
using Agent.Core;
using Agent.Models;

namespace Agent.Verify;

public static class HardwareStorageNetworkTests
{
    public static void Register(TestRunner t)
    {
        // ------------------------------------------------------------ parsers
        t.Add("WMI datetime parsing (DMTF with offset) -> UTC", () =>
        {
            var d = Wmi.ParseDmtf("20261118153000.000000+330");
            Assert.Equal(new DateTime(2026, 11, 18, 10, 0, 0, DateTimeKind.Utc), d.Value);
            Assert.True(Wmi.ParseDmtf("garbage") == null && Wmi.ParseDmtf(null) == null);
            Assert.Equal(new DateTime(2024, 3, 9), Wmi.ParseYmd("20240309").Value.Date);
        });

        // ------------------------------------------------------- device info
        t.Add("DeviceInfo: laptop, domain, placeholder serial dropped, assigned user from session, uptime", () =>
        {
            var wmi = new FakeWmi();
            wmi.Add("Win32_ComputerSystem", P.D("Manufacturer", "LENOVO", "Model", "20XW0055IN", "Domain", "corp.example.com", "PartOfDomain", true, "UserName", "CORP\\old", "Name", "LAPTOP-01"));
            wmi.Add("Win32_ComputerSystemProduct", P.D("UUID", "4C4C4544-0042-3510-8051-B2C04F595A32", "IdentifyingNumber", "PF2ABC12", "Vendor", "LENOVO"));
            wmi.Add("Win32_BIOS", P.D("SerialNumber", "To Be Filled By O.E.M."));
            wmi.Add("Win32_SystemEnclosure", P.D("ChassisTypes", new ushort[] { 10 }));
            wmi.Add("Win32_OperatingSystem", P.D("Caption", "Microsoft Windows 11 Pro", "Version", "10.0.22631", "LastBootUpTime", "20261118080000.000000+000"));
            var native = new FakeNative { Ticks = 7200 * 1000L };
            native.Sessions.Add(new WtsSession { SessionId = 1, User = "asha", Domain = "CORP", State = "active", Station = "Console" });
            var path = System.IO.Path.GetTempFileName();
            var id = new DeviceIdentityService(path, new FakeFingerprint(), new SystemClock()); id.Initialize();
            var info = new DeviceInfoCollector(wmi, native, new FakeRegistry(), id, "1.0.0", ".NET 8").Collect(new DateTime(2026, 11, 18, 10, 0, 0, DateTimeKind.Utc), id.DeviceId);
            Assert.Equal("LENOVO", info.Manufacturer);
            Assert.Equal("PF2ABC12", info.SerialNumber, "BIOS placeholder -> falls back to product serial");
            Assert.Equal("CORP\\asha", info.AssignedUser);
            Assert.Equal("domain", info.DomainType);
            Assert.True(info.IsLaptop == true && info.ChassisType == "laptop");
            Assert.Equal(7200L, info.UptimeSeconds.Value);
            Assert.Equal(new DateTime(2026, 11, 18, 8, 0, 0, DateTimeKind.Utc), info.LastBootUtc.Value);
            System.IO.File.Delete(path);
        });

        t.Add("DeviceInfo: nobody logged on -> falls back to LastLoggedOnUser; workgroup", () =>
        {
            var wmi = new FakeWmi();
            wmi.Add("Win32_ComputerSystem", P.D("Manufacturer", "Dell Inc.", "Model", "Latitude 5420", "PartOfDomain", false, "Domain", "WORKGROUP"));
            var reg = new FakeRegistry().Lm(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\LogonUI", "LastLoggedOnUser", @".\ravi");
            var path = System.IO.Path.GetTempFileName();
            var id = new DeviceIdentityService(path, new FakeFingerprint(), new SystemClock()); id.Initialize();
            var info = new DeviceInfoCollector(wmi, new FakeNative(), reg, id, "1", "x").Collect(DateTime.UtcNow, "d");
            Assert.Equal(@".\ravi", info.AssignedUser);
            Assert.Equal("workgroup", info.DomainType);
            System.IO.File.Delete(path);
        });

        // ----------------------------------------------------------- hardware
        t.Add("Hardware: CPU, RAM modules/slots, BIOS, board", () =>
        {
            var wmi = new FakeWmi();
            wmi.Add("Win32_Processor", P.D("Name", "12th Gen Intel(R) Core(TM) i7-1255U", "Manufacturer", "GenuineIntel", "Architecture", 9, "NumberOfCores", 10, "NumberOfLogicalProcessors", 12, "MaxClockSpeed", 1700, "L3CacheSize", 12288));
            wmi.Add("Win32_PhysicalMemory", P.D("DeviceLocator", "ChannelA-DIMM0", "Capacity", 8589934592UL, "Manufacturer", "Samsung", "PartNumber", "M471A1K43DB1-CWE", "SerialNumber", "0A1B2C3D", "SMBIOSMemoryType", 26, "FormFactor", 12, "Speed", 3200, "ConfiguredClockSpeed", 3200));
            wmi.Add("Win32_PhysicalMemory", P.D("DeviceLocator", "ChannelB-DIMM0", "Capacity", 8589934592UL, "Manufacturer", "Samsung", "PartNumber", "M471A1K43DB1-CWE", "SerialNumber", "0A1B2C3E", "SMBIOSMemoryType", 26, "FormFactor", 12, "Speed", 3200));
            wmi.Add("Win32_PhysicalMemoryArray", P.D("MemoryDevices", 2, "MaxCapacityEx", 67108864UL));
            wmi.Add("Win32_BaseBoard", P.D("Manufacturer", "LENOVO", "Product", "LNVNB161216", "SerialNumber", "L1HF"));
            wmi.Add("Win32_BIOS", P.D("Manufacturer", "LENOVO", "SMBIOSBIOSVersion", "N3MET12W (1.12 )", "ReleaseDate", "20230614000000.000000+000", "SMBIOSMajorVersion", 3, "SMBIOSMinorVersion", 4));
            var hw = new HardwareInventoryCollector(wmi, new FakeRegistry(), new FakeNative()).Collect();
            Assert.Equal("Intel", hw.Processor.Manufacturer);
            Assert.Equal("x64", hw.Processor.Architecture);
            Assert.Equal(10, hw.Processor.Cores.Value);
            Assert.Equal(12, hw.Processor.LogicalProcessors.Value);
            Assert.Equal(2, hw.Memory.Modules.Count);
            Assert.Equal("DDR4", hw.Memory.Modules[0].Type);
            Assert.Equal("SODIMM", hw.Memory.Modules[0].FormFactor);
            Assert.Equal(2, hw.Memory.SlotsTotal.Value);
            Assert.Equal(64L * 1024 * 1024 * 1024, hw.Memory.MaxCapacityBytes.Value);
            Assert.Equal(16L << 30, hw.Memory.TotalBytes.Value);
            Assert.Equal(75.0, hw.Memory.UsedPercent.Value);
            Assert.Equal("3.4", hw.Bios.SmbiosVersion);
            Assert.Equal(new DateTime(2023, 6, 14, 0, 0, 0, DateTimeKind.Utc), hw.Bios.ReleaseDateUtc.Value);
        });

        t.Add("Hardware: GPU memory is read from the registry because WMI AdapterRAM caps at 4 GB; monitors decoded", () =>
        {
            var wmi = new FakeWmi();
            wmi.Add("Win32_VideoController", P.D("Name", "NVIDIA GeForce RTX 3060", "AdapterCompatibility", "NVIDIA", "AdapterRAM", 4293918720UL, "CurrentHorizontalResolution", 1920, "CurrentVerticalResolution", 1080, "CurrentRefreshRate", 144));
            wmi.Add("Win32_VideoController", P.D("Name", "Microsoft Remote Display Adapter"));
            var cls = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0000";
            var reg = new FakeRegistry().Lm(cls, "DriverDesc", "NVIDIA GeForce RTX 3060").Lm(cls, "HardwareInformation.qwMemorySize", BitConverter.GetBytes(12L << 30));
            wmi.Add("WmiMonitorID", P.D("InstanceName", "DISPLAY\\DELA0B1\\1", "ManufacturerName", new ushort[] { 'D', 'E', 'L', 0 }, "UserFriendlyName", new ushort[] { 'U', '2', '7', '2', '3', 'Q', 'E', 0 }, "SerialNumberID", new ushort[] { 'C', 'N', '1', '2' }, "YearOfManufacture", 2022));
            wmi.Add("WmiMonitorBasicDisplayParams", P.D("InstanceName", "DISPLAY\\DELA0B1\\1", "MaxHorizontalImageSize", 60, "MaxVerticalImageSize", 34));
            var hw = new HardwareInventoryCollector(wmi, reg, new FakeNative()).Collect();
            Assert.Equal(1, hw.Gpus.Count);                                    // remote display adapter skipped
            Assert.Equal(12L << 30, hw.Gpus[0].MemoryBytes.Value);
            Assert.Equal(144, hw.Gpus[0].RefreshRateHz.Value);
            Assert.Equal("Dell", hw.Monitors[0].Manufacturer);
            Assert.Equal("U2723QE", hw.Monitors[0].Model);
            Assert.Equal(27.2, hw.Monitors[0].DiagonalInches.Value);
        });

        t.Add("Battery: health = full-charge / design capacity; no battery on desktops", () =>
        {
            var wmi = new FakeWmi();
            Assert.Equal(0, BatteryReader.Read(wmi).Count);
            wmi.Add("Win32_Battery", P.D("Name", "01AV456", "Chemistry", 6, "EstimatedChargeRemaining", 80, "BatteryStatus", 2));
            wmi.Add("BatteryStaticData", P.D("DesignedCapacity", 57000, "ManufactureName", "SMP", "SerialNumber", "1234"));
            wmi.Add("BatteryFullChargedCapacity", P.D("FullChargedCapacity", 45600));
            wmi.Add("BatteryCycleCount", P.D("CycleCount", 212));
            var b = BatteryReader.Read(wmi).Single();
            Assert.Equal(80.0, b.HealthPercent.Value);
            Assert.Equal(212, b.CycleCount.Value);
            Assert.Equal("Li-ion", b.Chemistry);
        });

        t.Add("Peripherals: hubs and composite children are ignored; USB vs Bluetooth", () =>
        {
            var wmi = new FakeWmi();
            wmi.Add("Win32_PnPEntity", P.D("Name", "USB Root Hub (USB 3.0)", "DeviceID", @"USB\ROOT_HUB30\4&1"));
            wmi.Add("Win32_PnPEntity", P.D("Name", "Logitech USB Receiver", "PNPClass", "HIDClass", "Manufacturer", "Logitech", "DeviceID", @"USB\VID_046D&PID_C52B\5&2"));
            wmi.Add("Win32_PnPEntity", P.D("Name", "USB Input Device", "DeviceID", @"USB\VID_046D&PID_C52B&MI_00\6&1"));
            wmi.Add("Win32_PnPEntity", P.D("Name", "Sony WH-1000XM4", "PNPClass", "Bluetooth", "DeviceID", @"BTHENUM\DEV_AABBCC\7&1"));
            var r = new PeripheralsCollector(wmi).Collect();
            Assert.Equal(2, r.Peripherals.Count);
            Assert.True(r.Peripherals.Any(p => p.Connection == "usb") && r.Peripherals.Any(p => p.Connection == "bluetooth"));
        });

        // ------------------------------------------------------------ storage
        t.Add("Storage: NVMe via MSFT_PhysicalDisk, health, reliability counters, partitions, volumes", () =>
        {
            var wmi = new FakeWmi();
            var part = new FakeWmiObject { Props = P.D("Index", 1, "Type", "GPT: Basic Data", "Size", 510000000000UL, "StartingOffset", 1048576UL, "BootPartition", true, "PrimaryPartition", true) };
            part.Related["Win32_LogicalDisk"] = new List<IWmiObject> { new FakeWmiObject { Props = P.D("DeviceID", "C:") } };
            var drive = wmi.Add("Win32_DiskDrive", P.D("Index", 0, "DeviceID", @"\\.\PHYSICALDRIVE0", "Model", "Samsung SSD 970 EVO 500GB", "Manufacturer", "(Standard disk drives)", "SerialNumber", "0025_38B4_21C1_B1AD.", "Size", 500107862016UL, "Status", "OK", "PNPDeviceID", @"SCSI\DISK&VEN_NVME&PROD_SAMSUNG_SSD_970\5&1"));
            drive.Related["Win32_DiskPartition"] = new List<IWmiObject> { part };
            wmi.Add("MSFT_PhysicalDisk", P.D("DeviceId", "0", "FriendlyName", "Samsung SSD 970 EVO 500GB", "SerialNumber", "S466NX0K", "MediaType", 4, "BusType", 17, "HealthStatus", 0, "OperationalStatus", new ushort[] { 2 }, "Size", 500107862016UL, "FirmwareVersion", "2B2QEXE7"));
            wmi.Add("MSFT_StorageReliabilityCounter", P.D("DeviceId", "0", "Temperature", 41, "Wear", 3, "PowerOnHours", 5120));
            wmi.Add("Win32_LogicalDisk", P.D("DeviceID", "C:", "VolumeName", "OS", "FileSystem", "NTFS", "Size", 500000000000UL, "FreeSpace", 125000000000UL, "DriveType", 3));
            var s = new StorageCollector(wmi, new FakeIoctl()).Collect();
            var d = s.Disks.Single();
            Assert.Equal("NVMe", d.MediaType);
            Assert.Equal("NVMe", d.BusType);
            Assert.Equal("Samsung", d.Manufacturer);
            Assert.Equal("S466NX0K", d.SerialNumber);
            Assert.Equal("Healthy", d.HealthStatus);
            Assert.Equal(41, d.TemperatureC.Value);
            Assert.Equal(3, d.WearPercent.Value);
            Assert.Equal("storage-reliability", d.SmartSource);
            Assert.Equal("C:", d.Partitions[0].DriveLetters[0]);
            var v = s.Volumes.Single();
            Assert.Equal(75.0, v.UsedPercent.Value);
            Assert.Equal(@"\\.\PHYSICALDRIVE0", v.PhysicalDiskId);
        });

        t.Add("Storage: Windows 7 fallback (no Storage WMI): HDD via seek-penalty ioctl + SMART predict-failure", () =>
        {
            var wmi = new FakeWmi();
            wmi.Errors["MSFT_PhysicalDisk"] = "Invalid namespace";
            wmi.Errors["MSFT_StorageReliabilityCounter"] = "Invalid namespace";
            wmi.Add("Win32_DiskDrive", P.D("Index", 0, "DeviceID", @"\\.\PHYSICALDRIVE0", "Model", "ST500LM012 HN-M500MBB", "Manufacturer", "(Standard disk drives)", "SerialNumber", "S2XYZ", "Size", 500107862016UL, "InterfaceType", "IDE", "Status", "Pred Fail", "PNPDeviceID", @"IDE\DISKST500LM012_HN-M500MBB\4&2"));
            wmi.Add("MSStorageDriver_FailurePredictStatus", P.D("InstanceName", @"IDE\DISKST500LM012_HN-M500MBB\4&2_0", "PredictFailure", true));
            var smart = new byte[512];
            smart[2] = 194; smart[2 + 3] = 100; smart[2 + 5] = 38;                 // attr 194 raw = 38 C
            smart[14] = 9; smart[14 + 5] = 0x10; smart[14 + 6] = 0x27;              // attr 9 raw = 0x2710 = 10000 h
            wmi.Add("MSStorageDriver_ATAPISmartData", P.D("InstanceName", @"IDE\DISKST500LM012_HN-M500MBB\4&2_0", "VendorSpecific", smart));
            var io = new FakeIoctl(); io.Map[0] = new DiskIoctlInfo { BusType = 3, IncursSeekPenalty = true };
            var d = new StorageCollector(wmi, io).Collect().Disks.Single();
            Assert.Equal("HDD", d.MediaType);
            Assert.Equal("Seagate", d.Manufacturer);
            Assert.Equal("Unhealthy", d.HealthStatus);
            Assert.True(d.SmartPredictFailure == true);
            Assert.Equal(38, d.TemperatureC.Value);
            Assert.Equal(10000L, d.PowerOnHours.Value);
            Assert.Equal("ata-smart", d.SmartSource);
        });

        t.Add("Storage: media type resolution rules", () =>
        {
            Assert.Equal("SSD", StorageCollector.ResolveMediaType(null, 11, new DiskIoctlInfo { IncursSeekPenalty = false }, "X", null));
            Assert.Equal("Unknown", StorageCollector.ResolveMediaType(null, null, null, "Generic Disk", null));
            Assert.Equal("NVMe", StorageCollector.ResolveMediaType(null, null, null, "WDC PC SN730 NVMe", null));
            Assert.Equal("HDD", StorageCollector.ResolveMediaType(3, 11, null, "X", null));
        });

        // -------------------------------------------------------------- network
        t.Add("Network config: types, MAC format, primary IP ignores VPN/virtual adapters", () =>
        {
            var src = new FakeAdapters();
            src.List.Add(new RawAdapter { Id = "1", Name = "Wi-Fi", Description = "Intel(R) Wi-Fi 6 AX201 160MHz", Mac = "AA-BB-CC-00-11-22", InterfaceType = "Wireless80211", IsUp = true, SpeedBitsPerSecond = 866000000, IPv4 = new List<string> { "192.168.1.50" }, Masks = new List<string> { "255.255.255.0" }, Gateways = new List<string> { "192.168.1.1" }, Dns = new List<string> { "8.8.8.8" }, DhcpEnabled = true });
            src.List.Add(new RawAdapter { Id = "2", Name = "Ethernet 2", Description = "Cisco AnyConnect Secure Mobility Client Virtual Miniport Adapter", Mac = "00-05-9A-3C-7A-00", InterfaceType = "Ethernet", IsUp = true, SpeedBitsPerSecond = 1000000000, IPv4 = new List<string> { "10.9.0.4" }, Gateways = new List<string> { "10.9.0.1" } });
            src.List.Add(new RawAdapter { Id = "3", Name = "vEthernet (WSL)", Description = "Hyper-V Virtual Ethernet Adapter", Mac = "00-15-5D-00-00-01", InterfaceType = "Ethernet", IsUp = true, SpeedBitsPerSecond = 10000000000, IPv4 = new List<string> { "172.20.0.1" } });
            src.List.Add(new RawAdapter { Id = "4", Name = "Loopback", InterfaceType = "Loopback", IsUp = true });
            var wmi = new FakeWmi();
            wmi.Add("Win32_NetworkAdapter", P.D("MACAddress", "AA:BB:CC:00:11:22", "Manufacturer", "Intel Corporation", "PNPDeviceID", "PCI\\VEN_8086", "PhysicalAdapter", true));
            wmi.Add("Win32_PnPSignedDriver", P.D("DeviceID", "PCI\\VEN_8086", "DriverVersion", "22.200.0.5", "DriverProviderName", "Intel", "DriverDate", "20230510000000.000000+000"));
            var n = new NetworkConfigCollector(src, wmi, new FakeClock()).Collect();
            Assert.Equal(3, n.Adapters.Count);
            Assert.Equal("192.168.1.50", n.PrimaryIPv4);
            var wifi = n.Adapters.Single(a => a.AdapterType == "Wi-Fi");
            Assert.Equal("AA:BB:CC:00:11:22", wifi.MacAddress);
            Assert.Equal("Intel Corporation", wifi.Manufacturer);
            Assert.Equal("22.200.0.5", wifi.DriverVersion);
            Assert.True(wifi.DhcpEnabled == true);
            Assert.Equal("VPN", n.Adapters.Single(a => a.Id == "2").AdapterType);
            Assert.Equal("Virtual", n.Adapters.Single(a => a.Id == "3").AdapterType);
        });

        t.Add("Wi-Fi: standard / band / channel / auth mapping", () =>
        {
            Assert.Equal("Wi-Fi 6E", WifiCollector.Standard(10, 6115));
            Assert.Equal("Wi-Fi 6", WifiCollector.Standard(10, 5180));
            Assert.Equal("Wi-Fi 5", WifiCollector.Standard(8, 5180));
            Assert.Equal("Wi-Fi 7", WifiCollector.Standard(11, 6115));
            Assert.Equal("5 GHz", WifiCollector.Band(5180));
            Assert.Equal("2.4 GHz", WifiCollector.Band(2437));
            Assert.Equal("6 GHz", WifiCollector.Band(6115));
            Assert.Equal(36, WifiCollector.Channel(5180).Value);
            Assert.Equal(6, WifiCollector.Channel(2437).Value);
            Assert.Equal(33, WifiCollector.Channel(6115).Value);
            Assert.Equal("WPA2-Personal", WifiCollector.AuthName(7, false));
            Assert.Equal("WPA3-Personal", WifiCollector.AuthName(9, false));
            Assert.Equal("WPA2-Enterprise", WifiCollector.AuthName(6, true));
        });

        t.Add("Wi-Fi: connected snapshot is mapped; duration tracks the SSID; no adapter is handled", () =>
        {
            var api = new FakeWifi();
            var clock = new FakeClock();
            var c = new WifiCollector(api, clock);
            api.Value = new WifiSnapshot { WlanServiceAvailable = true, AdapterPresent = true, AdapterDescription = "Intel AX201", Connected = true, Ssid = "Office", Bssid = "aa:bb:cc:dd:ee:ff", SignalQuality = 78, Rssi = -55, FrequencyKhz = 5180000, PhyType = 10, RxRateKbps = 1201000, TxRateKbps = 1201000, AuthAlgorithm = 7, CipherAlgorithm = 4 };
            var w = c.Collect().Wifi;
            Assert.Equal("Wi-Fi 6", w.WifiStandard);
            Assert.Equal(36, w.Channel.Value);
            Assert.Equal(1201L, w.ReceiveRateMbps.Value);
            Assert.Equal("CCMP (AES)", w.Cipher);
            clock.Now = clock.Now.AddMinutes(10);
            Assert.Equal(600L, c.Collect().Wifi.ConnectionDurationSeconds.Value);
            api.Value = new WifiSnapshot { WlanServiceAvailable = true, AdapterPresent = false };
            Assert.True(!c.Collect().Wifi.AdapterPresent);
            api.Value = new WifiSnapshot { WlanServiceAvailable = false };
            Assert.Equal("wlan-service-unavailable", c.Collect().Wifi.State);
        });
    }
}
