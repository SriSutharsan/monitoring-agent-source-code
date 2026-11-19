using System;
using System.Threading;
using System.Threading.Tasks;
using Agent.Communication;
using Agent.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Agent.Service;

/// <summary>
/// Runs the three long-lived loops (collectors, registration, sync). Each loop is supervised: an unexpected exception is logged
/// and the loop restarts after a delay, so one bug can never stop monitoring on the machine.
/// </summary>
public sealed class AgentHost : BackgroundService
{
    private readonly CollectionScheduler _scheduler;
    private readonly RegistrationManager _registration;
    private readonly SyncEngine _sync;
    private readonly DeviceIdentityService _identity;
    private readonly string _version;
    private readonly ILogger _log;

    public AgentHost(CollectionScheduler scheduler, RegistrationManager registration, SyncEngine sync, DeviceIdentityService identity,
        string version, ILogger<AgentHost> log)
    {
        _scheduler = scheduler;
        _registration = registration;
        _sync = sync;
        _identity = identity;
        _version = version;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("Endpoint monitoring agent {Version} starting. Device {DeviceId}", _version, _identity.DeviceId);
        _registration.Initialize();
        _log.LogInformation("Registration state: {State}", _registration.Status);

        await Task.WhenAll(
            Supervise("collectors", _scheduler.RunAsync, stoppingToken),
            Supervise("registration", _registration.RunAsync, stoppingToken),
            Supervise("sync", _sync.RunAsync, stoppingToken)).ConfigureAwait(false);
        _log.LogInformation("Endpoint monitoring agent stopped");
    }

    private async Task Supervise(string name, Func<CancellationToken, Task> run, CancellationToken ct)
    {
        int restarts = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await run(ct).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                restarts++;
                var delay = Backoff.Exponential(restarts - 1, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(5));
                _log.LogError(ex, "Loop {Loop} crashed (restart #{Count}); restarting in {Delay}", name, restarts, delay);
                try { await Task.Delay(delay, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
            }
        }
    }
}
