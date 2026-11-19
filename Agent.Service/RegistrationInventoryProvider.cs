using System;
using System.Threading;
using System.Threading.Tasks;
using Agent.Collectors.Hardware;
using Agent.Collectors.Network;
using Agent.Collectors.Performance;
using Agent.Collectors.Software;
using Agent.Collectors.Storage;
using Agent.Communication;
using Agent.Core;
using Agent.Models;

namespace Agent.Service;

/// <summary>Collects the inventory that accompanies the first registration request, directly from this machine. Nothing is hard coded.</summary>
public sealed class RegistrationInventoryProvider : IRegistrationInventoryProvider
{
    private readonly DeviceInfoCollector _device;
    private readonly HardwareInventoryCollector _hardware;
    private readonly StorageCollector _storage;
    private readonly NetworkConfigCollector _network;
    private readonly SoftwareInventoryCollector _software;
    private readonly CpuMemoryCollector _perf;
    private readonly IDeviceFingerprintProvider _fingerprint;
    private readonly DeviceIdentityService _identity;
    private readonly IClock _clock;

    public RegistrationInventoryProvider(DeviceInfoCollector device, HardwareInventoryCollector hardware, StorageCollector storage,
        NetworkConfigCollector network, SoftwareInventoryCollector software, CpuMemoryCollector perf,
        IDeviceFingerprintProvider fingerprint, DeviceIdentityService identity, IClock clock)
    {
        _device = device;
        _hardware = hardware;
        _storage = storage;
        _network = network;
        _software = software;
        _perf = perf;
        _fingerprint = fingerprint;
        _identity = identity;
        _clock = clock;
    }

    public async Task<RegistrationInventory> CollectAsync(CancellationToken ct)
    {
        PerformanceMetrics perfMetrics = null;
        try
        {
            perfMetrics = await _perf.CollectTypedAsync(new CollectionContext { DeviceId = _identity.DeviceId, NowUtc = _clock.UtcNow }, ct).ConfigureAwait(false);
        }
        catch { }

        return await Task.Run(() => new RegistrationInventory
        {
            Device = Safe(() => _device.Collect(_clock.UtcNow, _identity.DeviceId)),
            Hardware = Safe(() => _hardware.Collect()),
            Storage = Safe(() => _storage.Collect()),
            Network = Safe(() => _network.Collect()),
            Software = Safe(() => _software.Collect(null)),
            Performance = perfMetrics ?? Safe(() => _perf.Collect())
        }, ct).ConfigureAwait(false);
    }

    public DeviceFingerprint GetFingerprint()
    {
        return _fingerprint.Get();
    }

    // one failing source must not block registration; the missing part simply arrives later through the normal collectors
    private static T Safe<T>(Func<T> f) where T : class
    {
        try { return f(); } catch (Exception) { return null; }
    }
}
