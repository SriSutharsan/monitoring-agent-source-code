using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Agent.Core;
using Agent.Models;

namespace Agent.Verify;

public static class CoreTests
{
    public static void Register(TestRunner t)
    {
        t.Add("Wildcard matching is case-insensitive with * and ?", () =>
        {
            Assert.True(Wildcard.IsMatch("*torrent*", "uTorrent Web"));
            Assert.True(Wildcard.IsMatch("Chrome ?", "chrome 9"));
            Assert.True(!Wildcard.IsMatch("Chrome", "Google Chrome"));
        });

        t.Add("Version compare is numeric, not lexical", () =>
        {
            Assert.True(VersionCompare.IsOlderThan("9.0.1", "10.0"));
            Assert.True(!VersionCompare.IsOlderThan("120.0.6099.71", "120.0.6099"));
            Assert.True(VersionCompare.Compare("abc", "1.0") == null);
        });

        t.Add("RuntimeConfig defaults match the specified intervals", () =>
        {
            var c = RuntimeConfig.CreateDefault();
            Assert.Equal(60, (int)c.IntervalFor(Categories.Heartbeat).TotalSeconds);
            Assert.Equal(30, (int)c.IntervalFor(Categories.PerfCpuMemory).TotalSeconds);
            Assert.Equal(30, (int)c.IntervalFor(Categories.PerfDisk).TotalSeconds);
            Assert.Equal(30, (int)c.IntervalFor(Categories.PerfNetwork).TotalSeconds);
            Assert.Equal(300, (int)c.IntervalFor(Categories.NetworkConfig).TotalSeconds);
            Assert.Equal(120, (int)c.IntervalFor(Categories.Wifi).TotalSeconds);
            Assert.Equal(86400, (int)c.IntervalFor(Categories.Hardware).TotalSeconds);
            Assert.Equal(3600, (int)c.IntervalFor(Categories.Storage).TotalSeconds);
            Assert.Equal(43200, (int)c.IntervalFor(Categories.Software).TotalSeconds);
            Assert.Equal(1800, (int)c.IntervalFor(Categories.Security).TotalSeconds);
            Assert.Equal(60, (int)c.IntervalFor(Categories.Events).TotalSeconds);
            Assert.Equal(300, (int)c.IntervalFor(Categories.Battery).TotalSeconds);
        });

        t.Add("Remote config merge clamps intervals and cannot disable heartbeat", () =>
        {
            var c = RuntimeConfig.CreateDefault().Merge(new AgentConfigDocument
            {
                Version = 5,
                IntervalsSeconds = new Dictionary<string, int> { { Categories.PerfCpuMemory, 1 }, { "bogus", 10 } },
                CollectorsEnabled = new Dictionary<string, bool> { { Categories.Heartbeat, false }, { Categories.Wifi, false } }
            });
            Assert.Equal(RuntimeConfig.MinIntervalSeconds, (int)c.IntervalFor(Categories.PerfCpuMemory).TotalSeconds);
            Assert.True(c.IsEnabled(Categories.Heartbeat), "heartbeat must stay enabled");
            Assert.True(!c.IsEnabled(Categories.Wifi));
            Assert.True(!c.IntervalsSeconds.ContainsKey("bogus"));
        });

        t.Add("Change detector: first run is baseline, then RAM upgrade is reported", () =>
        {
            var det = new ChangeDetector(new MemSnapshots());
            var now = DateTime.UtcNow;
            Func<long, HardwareInventory> hw = gb => new HardwareInventory
            {
                Memory = new MemoryInfo
                {
                    Modules = new List<MemoryModuleInfo>
                    {
                        new MemoryModuleInfo { Slot = "DIMM A", CapacityBytes = gb * 1073741824L, SerialNumber = "S1", Type = "DDR4" }
                    }
                }
            };
            Assert.Equal(0, det.Detect(Categories.Hardware, hw(8), now).Count, "baseline");
            Assert.Equal(0, det.Detect(Categories.Hardware, hw(8), now).Count, "no change");
            var ch = det.Detect(Categories.Hardware, hw(16), now);
            Assert.True(ch.Any(c => c.ChangeClass == "ram_changed"), "ram_changed missing");
            Assert.True(ch.Any(c => c.ChangeClass == "ram_capacity_changed"), "capacity change missing");
        });

        t.Add("Change detector: software install / uninstall / version change", () =>
        {
            var det = new ChangeDetector(new MemSnapshots());
            var now = DateTime.UtcNow;
            Func<string, string, SoftwareInventory> inv = (n, v) => new SoftwareInventory
            {
                Applications = new List<InstalledApplication>
                {
                    new InstalledApplication { Key = "machine|x64|7zip", Name = "7-Zip", Version = "23.01" },
                    new InstalledApplication { Key = "machine|x64|" + n, Name = n, Version = v }
                }
            };
            det.Detect(Categories.Software, inv("Foo", "1.0"), now);
            var upd = det.Detect(Categories.Software, inv("Foo", "2.0"), now);
            Assert.True(upd.Any(c => c.ChangeClass == "software_version_changed"));
            var removed = det.Detect(Categories.Software, new SoftwareInventory { Applications = new List<InstalledApplication> { new InstalledApplication { Key = "machine|x64|7zip", Name = "7-Zip", Version = "23.01" } } }, now);
            Assert.True(removed.Any(c => c.ChangeClass == "software_uninstalled"));
            var added = det.Detect(Categories.Software, inv("Bar", "1.0"), now);
            Assert.True(added.Any(c => c.ChangeClass == "software_installed"));
        });

        t.Add("Change detector: disk swap yields storage_replaced", () =>
        {
            var det = new ChangeDetector(new MemSnapshots());
            Func<string, StorageInventory> s = serial => new StorageInventory
            {
                Disks = new List<PhysicalDiskInfo> { new PhysicalDiskInfo { Model = "NVMe X", SerialNumber = serial, SizeBytes = 512000000000, MediaType = "NVMe" } },
                Volumes = new List<VolumeInfo>()
            };
            det.Detect(Categories.Storage, s("AAA"), DateTime.UtcNow);
            var ch = det.Detect(Categories.Storage, s("BBB"), DateTime.UtcNow);
            Assert.True(ch.Any(c => c.ChangeClass == "storage_replaced"));
        });

        t.Add("Storage free-space change alone is NOT a hardware change", () =>
        {
            var det = new ChangeDetector(new MemSnapshots());
            Func<long, StorageInventory> s = free => new StorageInventory
            {
                Volumes = new List<VolumeInfo> { new VolumeInfo { DriveLetter = "C:", DriveType = "Fixed", FileSystem = "NTFS", TotalBytes = 500, FreeBytes = free } }
            };
            det.Detect(Categories.Storage, s(100), DateTime.UtcNow);
            Assert.Equal(0, det.Detect(Categories.Storage, s(50), DateTime.UtcNow).Count);
        });

        t.Add("Alerts: storage 80% warning, 90% critical, clear, no repeat", () =>
        {
            var cfg = new StaticConfig();
            var ev = new AlertEvaluator(cfg, new MemState());
            Func<double, StorageInventory> s = pct => new StorageInventory
            {
                Volumes = new List<VolumeInfo> { new VolumeInfo { DriveLetter = "C:", DriveType = "Fixed", TotalBytes = 1000L * 1073741824, FreeBytes = (long)((100 - pct) * 10 * 1073741824), UsedPercent = pct } }
            };
            var now = DateTime.UtcNow;
            var a = ev.Evaluate(Categories.Storage, s(85), now);
            Assert.True(a.Any(x => x.Rule == "storage.utilization" && x.Severity == "warning" && x.State == "raised"));
            Assert.Equal(0, ev.Evaluate(Categories.Storage, s(86), now).Count, "must not repeat");
            var b = ev.Evaluate(Categories.Storage, s(93), now);
            Assert.True(b.Any(x => x.Rule == "storage.utilization" && x.Severity == "critical"));
            var c = ev.Evaluate(Categories.Storage, s(40), now);
            Assert.True(c.Any(x => x.Rule == "storage.utilization" && x.State == "cleared"));
        });

        t.Add("Alerts: critically low free space", () =>
        {
            var ev = new AlertEvaluator(new StaticConfig(), new MemState());
            var s = new StorageInventory
            {
                Volumes = new List<VolumeInfo> { new VolumeInfo { DriveLetter = "C:", DriveType = "Fixed", TotalBytes = 100L * 1073741824, FreeBytes = 1L * 1073741824, UsedPercent = 99 } }
            };
            Assert.True(ev.Evaluate(Categories.Storage, s, DateTime.UtcNow).Any(x => x.Rule == "storage.free_space" && x.Severity == "critical"));
        });

        t.Add("Alerts: disk health warning / SMART failure", () =>
        {
            var ev = new AlertEvaluator(new StaticConfig(), new MemState());
            var s = new StorageInventory { Disks = new List<PhysicalDiskInfo> { new PhysicalDiskInfo { Model = "D", DeviceId = "0", SmartPredictFailure = true } } };
            Assert.True(ev.Evaluate(Categories.Storage, s, DateTime.UtcNow).Any(x => x.Rule == "storage.disk_health" && x.Severity == "critical"));
        });

        t.Add("Alerts: CPU needs sustained samples (single spike ignored)", () =>
        {
            var ev = new AlertEvaluator(new StaticConfig(), new MemState());
            Func<double, PerformanceMetrics> p = cpu => new PerformanceMetrics { Cpu = new CpuMetrics { UsagePercent = cpu } };
            var now = DateTime.UtcNow;
            Assert.Equal(0, ev.Evaluate(Categories.PerfCpuMemory, p(97), now).Count, "1st");
            Assert.Equal(0, ev.Evaluate(Categories.PerfCpuMemory, p(97), now).Count, "2nd");
            Assert.True(ev.Evaluate(Categories.PerfCpuMemory, p(97), now).Any(x => x.Rule == "performance.cpu_high"), "3rd should raise");
            Assert.True(ev.Evaluate(Categories.PerfCpuMemory, p(10), now).Any(x => x.State == "cleared"));
        });

        t.Add("Alerts: state survives agent restart (no re-raise)", () =>
        {
            var state = new MemState();
            var s = new StorageInventory { Volumes = new List<VolumeInfo> { new VolumeInfo { DriveLetter = "C:", DriveType = "Fixed", TotalBytes = 1000, FreeBytes = 100, UsedPercent = 91 } } };
            Assert.True(new AlertEvaluator(new StaticConfig(), state).Evaluate(Categories.Storage, s, DateTime.UtcNow).Count > 0);
            var after = new AlertEvaluator(new StaticConfig(), state).Evaluate(Categories.Storage, s, DateTime.UtcNow);
            Assert.True(!after.Any(x => x.Rule == "storage.utilization"), "re-raised after restart");
        });

        t.Add("Alerts: unauthorized software raised then cleared on removal", () =>
        {
            var ev = new AlertEvaluator(new StaticConfig(), new MemState());
            var bad = new SoftwareInventory { Applications = new List<InstalledApplication> { new InstalledApplication { Key = "k1", Name = "BadApp", Version = "1", IsUnauthorized = true, PolicyMatch = "Bad*" } } };
            Assert.True(ev.Evaluate(Categories.Software, bad, DateTime.UtcNow).Any(x => x.Rule == "software.unauthorized" && x.State == "raised"));
            var clean = new SoftwareInventory { Applications = new List<InstalledApplication>() };
            Assert.True(ev.Evaluate(Categories.Software, clean, DateTime.UtcNow).Any(x => x.Rule == "software.unauthorized" && x.State == "cleared"));
        });

        t.Add("Alerts: repeated failed logons threshold with cooldown", () =>
        {
            var ev = new AlertEvaluator(new StaticConfig(), new MemState());
            var now = DateTime.UtcNow;
            Func<int, WindowsEventBatch> b = n => new WindowsEventBatch
            {
                Events = Enumerable.Range(0, n).Select(i => new WindowsEventRecord { Category = "failed_logon", Log = "Security", RecordId = i, TimestampUtc = now, Metadata = new Dictionary<string, string> { { "targetUser", "bob" } } }).ToList()
            };
            Assert.True(!ev.Evaluate(Categories.Events, b(3), now).Any(x => x.Rule == "security.repeated_failed_logons"));
            Assert.True(ev.Evaluate(Categories.Events, b(3), now).Any(x => x.Rule == "security.repeated_failed_logons"), "5 total should alert");
            Assert.True(!ev.Evaluate(Categories.Events, b(3), now).Any(x => x.Rule == "security.repeated_failed_logons"), "cooldown");
        });

        t.Add("Device identity: stable across restarts, new id when hardware changes (clone)", () =>
        {
            var path = Path.Combine(Path.GetTempPath(), "id-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var fp = new FakeFingerprint();
                var a = new DeviceIdentityService(path, fp, new SystemClock()); a.Initialize();
                var b = new DeviceIdentityService(path, fp, new SystemClock()); b.Initialize();
                Assert.Equal(a.DeviceId, b.DeviceId, "stable");
                fp.Value = new DeviceFingerprint { SystemUuid = "11111111-2222-3333-4444-555555555555", SerialNumber = "OTHER" };
                var c = new DeviceIdentityService(path, fp, new SystemClock()); c.Initialize();
                Assert.True(c.DeviceId != a.DeviceId && c.WasReset, "clone must get a new id");
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        });

        t.Add("Device identity: placeholder BIOS values are not trusted", () =>
        {
            Assert.True(DeviceIdentityService.IsPlaceholder("To Be Filled By O.E.M."));
            Assert.True(DeviceIdentityService.IsPlaceholder("00000000-0000-0000-0000-000000000000"));
            Assert.True(DeviceIdentityService.IsPlaceholder("FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF"));
            Assert.True(!DeviceIdentityService.IsPlaceholder("PF2ABC12"));
            Assert.True(DeviceIdentityService.ComputeFingerprintHash(new DeviceFingerprint { SystemUuid = "Default string", SerialNumber = "N/A" }) == null);
        });

        t.Add("Backoff grows and is capped", () =>
        {
            var d0 = Backoff.Exponential(0, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(15));
            var d6 = Backoff.Exponential(6, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(15));
            var d30 = Backoff.Exponential(30, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(15));
            Assert.True(d6 > d0);
            Assert.True(d30 <= TimeSpan.FromMinutes(18.1), "cap + jitter");
        });
    }
}
