using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using Agent.Collectors.Events;
using Agent.Collectors.Hardware;
using Agent.Collectors.Network;
using Agent.Collectors.Performance;
using Agent.Collectors.Platform;
using Agent.Collectors.Security;
using Agent.Collectors.Software;
using Agent.Collectors.Storage;
using Agent.Collectors.Support;
using Agent.Communication;
using Agent.Configuration;
using Agent.Core;
using Agent.Logging;
using Agent.Models;
using Agent.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Agent.Service;

public static class Program
{
#if NETFX
    public const string Runtime = "net48";
#else
    public const string Runtime = "net8";
#endif

    public static string Version
    {
        get
        {
            var a = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            return a != null ? a.InformationalVersion.Split('+')[0] : typeof(Program).Assembly.GetName().Version.ToString(3);
        }
    }

    public static int Main(string[] args)
    {
        if (args.Any(a => a == "--version" || a == "-v")) { Console.WriteLine("EndpointMonitoringAgent " + Version + " (" + Runtime + ")"); return 0; }
        if (args.Any(a => a == "--help" || a == "-h" || a == "/?")) { PrintHelp(); return 0; }

        var paths = ResolvePaths();
        try { paths.EnsureCreated(); }
        catch (Exception ex) { Console.Error.WriteLine("Cannot create the data directory " + paths.Root + ": " + ex.Message); return 3; }

        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            // written by the installer (registration token, API URL, intervals); lives outside the install folder so updates never overwrite it
            .AddJsonFile(Path.Combine(paths.ConfigDirectory, "appsettings.local.json"), optional: true, reloadOnChange: false)
            .Build();
        var options = new AgentOptions();
        config.GetSection(AgentOptions.SectionName).Bind(options);

        var serilog = LoggingSetup.Create(paths.LogDirectory, options.Logging.MinimumLevel, options.Logging.RetainedFileCount,
            options.Logging.FileSizeLimitBytes, options.Logging.WriteToEventLog, options.ServiceName);

        try
        {
            try { ApiClient.ValidateBaseUrl(options.Api); }
            catch (Exception ex)
            {
                serilog.Fatal("Invalid configuration: {Message}. Edit {Path} or re-run the installer.", ex.Message, Path.Combine(paths.ConfigDirectory, "appsettings.local.json"));
                Console.Error.WriteLine("Invalid configuration: " + ex.Message);
                return 4;
            }

            var host = BuildHost(options, paths, serilog);

            if (args.Length > 0 && args[0] == "--dump") return Dump(host, args.Skip(1).FirstOrDefault());
            if (args.Length > 0 && args[0] == "--check") return Check(host, options);

            host.Run();
            return 0;
        }
        catch (Exception ex)
        {
            serilog.Fatal(ex, "The agent terminated unexpectedly");
            return 1;
        }
        finally
        {
            (serilog as IDisposable)?.Dispose();
        }
    }

    private static AgentPaths ResolvePaths()
    {
        var overrideDir = Environment.GetEnvironmentVariable("ENDPOINT_AGENT_DATA");
        return string.IsNullOrWhiteSpace(overrideDir) ? AgentPaths.Default() : new AgentPaths(overrideDir);
    }

    public static IHost BuildHost(AgentOptions options, AgentPaths paths, Serilog.ILogger serilog)
    {
        return new HostBuilder()
            .UseContentRoot(AppContext.BaseDirectory)                      // a service's working directory is System32, never rely on it
            .UseWindowsService(o => o.ServiceName = options.ServiceName)
            .ConfigureLogging(l => { l.ClearProviders(); l.AddSerilog(serilog, dispose: false); })
            .ConfigureServices(s => Register(s, options, paths))
            .Build();
    }

    public static void Register(IServiceCollection s, AgentOptions options, AgentPaths paths)
    {
        s.AddSingleton(options);
        s.AddSingleton(paths);
        s.AddSingleton<IClock, SystemClock>();

        // ---- platform seams (the only classes that touch Windows APIs)
        s.AddSingleton<IWmiClient>(new WmiClient());
        s.AddSingleton<IRegistryReader, RegistryReader>();
        s.AddSingleton<IEventLogReader, EventLogReaderImpl>();
        s.AddSingleton<INativeSystem, NativeSystem>();
        s.AddSingleton<IWifiApi, WifiApi>();
        s.AddSingleton<IFirewallPolicyReader, FirewallPolicyReader>();
        s.AddSingleton<IWindowsUpdateClient, WindowsUpdateClient>();
        s.AddSingleton<IDiskIoctl, DiskIoctl>();
        s.AddSingleton<INetworkInterfaceSource, NetworkInterfaceSource>();
        s.AddSingleton<INetworkProbe, NetworkProbe>();
        s.AddSingleton<IProcessSource, ProcessSource>();
        s.AddSingleton<IDeviceFingerprintProvider, DeviceFingerprintProvider>();

        // ---- identity, storage, configuration
        s.AddSingleton(sp =>
        {
            var id = new DeviceIdentityService(paths.IdentityPath, sp.GetRequiredService<IDeviceFingerprintProvider>(), sp.GetRequiredService<IClock>());
            id.Initialize();
            return id;
        });
        s.AddSingleton<Func<string>>(sp => { var id = sp.GetRequiredService<DeviceIdentityService>(); return () => id.DeviceId; });
        s.AddSingleton(sp =>
        {
            var store = new SqliteStore(SqliteConnectionFactory.Create(paths.DatabasePath), paths.DatabasePath, sp.GetRequiredService<ILogger<SqliteStore>>());
            store.Initialize();
            return store;
        });
        s.AddSingleton<IOutbox>(sp => sp.GetRequiredService<SqliteStore>());
        s.AddSingleton<IStateStore>(sp => sp.GetRequiredService<SqliteStore>());
        s.AddSingleton<ISnapshotStore>(sp => sp.GetRequiredService<SqliteStore>());
        s.AddSingleton<IHistoryStore>(sp => sp.GetRequiredService<SqliteStore>());
        s.AddSingleton<ICredentialStore>(new DpapiCredentialStore(paths.CredentialPath));
        s.AddSingleton(new SignedEnvelopeVerifier(options.Security.ConfigSigningPublicKey, options.Security.RequireSignedConfig, TimeSpan.FromMinutes(options.Security.MaxClockSkewMinutes)));
        s.AddSingleton(sp =>
        {
            var p = new RemoteConfigProvider(options, sp.GetRequiredService<SignedEnvelopeVerifier>(), paths.CachedConfigPath,
                sp.GetRequiredService<Func<string>>(), sp.GetRequiredService<IClock>(), sp.GetRequiredService<ILogger<RemoteConfigProvider>>());
            p.LoadCached();
            return p;
        });
        s.AddSingleton<IConfigProvider>(sp => sp.GetRequiredService<RemoteConfigProvider>());

        // ---- collection pipeline
        s.AddSingleton<MetricsCache>();
        s.AddSingleton<ChangeDetector>();
        s.AddSingleton<AlertEvaluator>();
        s.AddSingleton<ClockSkewTracker>();
        s.AddSingleton<CollectionPipeline>();
        s.AddSingleton<ICollectionSink>(sp => sp.GetRequiredService<CollectionPipeline>());

        s.AddSingleton(sp => new DeviceInfoCollector(sp.GetRequiredService<IWmiClient>(), sp.GetRequiredService<INativeSystem>(), sp.GetRequiredService<IRegistryReader>(), sp.GetRequiredService<DeviceIdentityService>(), Version, Runtime));
        s.AddSingleton<HardwareInventoryCollector>();
        s.AddSingleton<PeripheralsCollector>();
        s.AddSingleton<StorageCollector>();
        s.AddSingleton<NetworkConfigCollector>();
        s.AddSingleton<WifiCollector>();
        s.AddSingleton<CpuMemoryCollector>();
        s.AddSingleton<DiskPerfCollector>();
        s.AddSingleton(sp =>
        {
            string host = ""; int port = 443;
            try { var u = ApiClient.ValidateBaseUrl(options.Api); host = u.Host; port = u.Port; } catch (Exception) { }
            return new NetworkPerfCollector(sp.GetRequiredService<INetworkInterfaceSource>(), sp.GetRequiredService<INetworkProbe>(), sp.GetRequiredService<IClock>(), host, port);
        });
        s.AddSingleton<BatteryCollector>();
        s.AddSingleton<SoftwareInventoryCollector>();
        s.AddSingleton<SecurityCollector>();
        s.AddSingleton<EventsCollector>();
        s.AddSingleton<DeviceHealthCollector>();
        s.AddSingleton<DiagnosticsCollector>();
        s.AddSingleton(sp => new HeartbeatCollector(sp.GetRequiredService<IOutbox>(), sp.GetRequiredService<IStateStore>(), sp.GetRequiredService<IConfigProvider>(),
            sp.GetRequiredService<INativeSystem>(), sp.GetRequiredService<MetricsCache>(), sp.GetRequiredService<ClockSkewTracker>(),
            () => sp.GetRequiredService<CollectionScheduler>(), sp.GetRequiredService<IClock>(), Version));

        s.AddSingleton<IEnumerable<ICollector>>(sp => new ICollector[]
        {
            sp.GetRequiredService<HeartbeatCollector>(), sp.GetRequiredService<DeviceInfoCollector>(), sp.GetRequiredService<HardwareInventoryCollector>(),
            sp.GetRequiredService<PeripheralsCollector>(), sp.GetRequiredService<StorageCollector>(), sp.GetRequiredService<NetworkConfigCollector>(),
            sp.GetRequiredService<WifiCollector>(), sp.GetRequiredService<CpuMemoryCollector>(), sp.GetRequiredService<DiskPerfCollector>(),
            sp.GetRequiredService<NetworkPerfCollector>(), sp.GetRequiredService<BatteryCollector>(), sp.GetRequiredService<SoftwareInventoryCollector>(),
            sp.GetRequiredService<SecurityCollector>(), sp.GetRequiredService<EventsCollector>(), sp.GetRequiredService<DeviceHealthCollector>(),
            sp.GetRequiredService<DiagnosticsCollector>()
        });
        s.AddSingleton(sp => new CollectionScheduler(sp.GetRequiredService<IEnumerable<ICollector>>(), sp.GetRequiredService<IConfigProvider>(),
            sp.GetRequiredService<ICollectionSink>(), sp.GetRequiredService<IClock>(), sp.GetRequiredService<Func<string>>(),
            sp.GetRequiredService<ILogger<CollectionScheduler>>(), options.Collection.MaxConcurrentCollections));
        s.AddSingleton<ICollectorStatusSource>(sp => sp.GetRequiredService<CollectionScheduler>());

        // ---- communication
        s.AddSingleton<IRegistrationInventoryProvider, RegistrationInventoryProvider>();
        s.AddSingleton<IApiClient>(sp => new ApiClient(new HttpClient(HttpHandlerFactory.Create(options.Api), true), options.Api,
            sp.GetRequiredService<ICredentialStore>(), sp.GetRequiredService<IClock>(), sp.GetRequiredService<ClockSkewTracker>(), Version,
            sp.GetRequiredService<ILogger<ApiClient>>()));
        s.AddSingleton(sp => new RegistrationManager(sp.GetRequiredService<IApiClient>(), sp.GetRequiredService<ICredentialStore>(), sp.GetRequiredService<IStateStore>(),
            sp.GetRequiredService<DeviceIdentityService>(), sp.GetRequiredService<IRegistrationInventoryProvider>(), options.Registration, Version,
            sp.GetRequiredService<IClock>(), sp.GetRequiredService<ILogger<RegistrationManager>>()));
        s.AddSingleton<IUpdateHandler>(sp => new SelfUpdateHandler(sp.GetRequiredService<SignedEnvelopeVerifier>(), paths, sp.GetRequiredService<IStateStore>(),
            sp.GetRequiredService<IClock>(), new HttpClient(HttpHandlerFactory.Create(options.Api), true) { Timeout = TimeSpan.FromMinutes(10) }, Version, Runtime,
            sp.GetRequiredService<ILogger<SelfUpdateHandler>>()));
        s.AddSingleton(sp => new SyncEngine(sp.GetRequiredService<IOutbox>(), sp.GetRequiredService<IApiClient>(), sp.GetRequiredService<RegistrationManager>(),
            sp.GetRequiredService<IStateStore>(), sp.GetRequiredService<IConfigProvider>(), sp.GetRequiredService<RemoteConfigProvider>(),
            sp.GetRequiredService<IUpdateHandler>(), sp.GetRequiredService<IClock>(), sp.GetRequiredService<Func<string>>(),
            options.Api.MaxRetryDelaySeconds, sp.GetRequiredService<ILogger<SyncEngine>>()));

        s.AddSingleton(sp => new AgentHost(sp.GetRequiredService<CollectionScheduler>(), sp.GetRequiredService<RegistrationManager>(),
            sp.GetRequiredService<SyncEngine>(), sp.GetRequiredService<DeviceIdentityService>(), Version, sp.GetRequiredService<ILogger<AgentHost>>()));
        s.AddSingleton<IHostedService>(sp => sp.GetRequiredService<AgentHost>());
    }

    // ------------------------------------------------------------------ diagnostics CLI (read-only)

    /// <summary>--dump &lt;category&gt;: runs ONE collector against this machine and prints its JSON. Nothing is queued or sent.</summary>
    private static int Dump(IHost host, string category)
    {
        var collectors = host.Services.GetRequiredService<IEnumerable<ICollector>>().ToList();
        var match = collectors.FirstOrDefault(c => string.Equals(c.Category, category, StringComparison.OrdinalIgnoreCase));
        if (match == null)
        {
            Console.Error.WriteLine("Unknown category. Available: " + string.Join(", ", collectors.Select(c => c.Category).OrderBy(x => x)));
            return 2;
        }
        var cfg = host.Services.GetRequiredService<IConfigProvider>().Current;
        var ctx = new CollectionContext { DeviceId = host.Services.GetRequiredService<DeviceIdentityService>().DeviceId, NowUtc = DateTime.UtcNow, Config = cfg };
        using (var cts = new CancellationTokenSource(match.Timeout))
        {
            var payload = match.CollectAsync(ctx, cts.Token).GetAwaiter().GetResult();
            Console.WriteLine(payload == null ? "(nothing to report)" : Json.SerializeIndented(payload));
        }
        return 0;
    }

    /// <summary>--check: validates configuration and API reachability; does not register or send data.</summary>
    private static int Check(IHost host, AgentOptions options)
    {
        var native = host.Services.GetRequiredService<INativeSystem>();
        Console.WriteLine("Agent version : " + Version + " (" + Runtime + ")");
        Console.WriteLine("Elevated      : " + native.IsElevated());
        Console.WriteLine("Device id     : " + host.Services.GetRequiredService<DeviceIdentityService>().DeviceId);
        Console.WriteLine("API base URL  : " + options.Api.BaseUrl);
        Console.WriteLine("Signing key   : " + (host.Services.GetRequiredService<SignedEnvelopeVerifier>().HasKey ? "configured" : "MISSING (signed config / updates will be rejected)"));
        bool ok = host.Services.GetRequiredService<IApiClient>().PingAsync(CancellationToken.None).GetAwaiter().GetResult();
        Console.WriteLine("API reachable : " + (ok ? "yes" : "NO"));
        var reg = host.Services.GetRequiredService<RegistrationManager>(); reg.Initialize();
        Console.WriteLine("Registration  : " + reg.Status);
        return ok ? 0 : 5;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Endpoint Monitoring Agent " + Version);
        Console.WriteLine("  (no arguments)        run (as a Windows service when started by the Service Control Manager)");
        Console.WriteLine("  --version             print the version");
        Console.WriteLine("  --check               validate configuration and API connectivity");
        Console.WriteLine("  --dump <category>     collect one category once and print the JSON (nothing is stored or sent)");
        Console.WriteLine("Categories: heartbeat device hardware hardware.peripherals storage network.config network.wifi perf.cpu_memory perf.disk perf.network battery software security events health diagnostics");
    }
}
