using System;
using System.Threading;
using System.Threading.Tasks;
using Agent.Collectors.Hardware;
using Agent.Collectors.Network;
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
    private readonly IDeviceFingerprintProvider _fingerprint;
    private readonly DeviceIdentityService _identity;
    private readonly IClock _clock;

    public RegistrationInventoryProvider(DeviceInfoCollector device, HardwareInventoryCollector hardware, StorageCollector storage,
        NetworkConfigCollector network, IDeviceFingerprintProvider fingerprint, DeviceIdentityService identity, IClock clock)
    {
        _device = device;
        _hardware = hardware;
        _storage = storage;
        _network = network;
        _fingerprint = fingerprint;
        _identity = identity;
        _clock = clock;
    }

    public Task<RegistrationInventory> CollectAsync(CancellationToken ct)
    {
        return Task.Run(() => new RegistrationInventory
        {
            Device = Safe(() => _device.Collect(_clock.UtcNow, _identity.DeviceId)),
            Hardware = Safe(() => _hardware.Collect()),
            Storage = Safe(() => _storage.Collect()),
            Network = Safe(() => _network.Collect())
        }, ct);
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
