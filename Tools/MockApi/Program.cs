using System;
using System.Linq;
using System.Threading;

namespace Agent.MockApi;

/// <summary>Standalone launcher: dotnet run --project Tools/MockApi -- --port 5099 --token my-token [--manual-approval]</summary>
public static class Program
{
    public static int Main(string[] args)
    {
        int port = 5099;
        string token = "test-token";
        bool manual = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--port" && i + 1 < args.Length) port = int.Parse(args[++i]);
            else if (args[i] == "--token" && i + 1 < args.Length) token = args[++i];
            else if (args[i] == "--manual-approval") manual = true;
        }
        using (var server = new MockApiServer(port) { RegistrationToken = token, AutoApprove = !manual }.Start())
        {
            Console.WriteLine("Mock API listening on " + server.BaseUrl + "  (registration token: " + token + ", approval: " + (manual ? "manual via POST /admin/approve/{deviceId}" : "automatic") + ")");
            Console.WriteLine("Set Agent:Api:AllowInsecureLoopback=true in appsettings.json to talk to it over http. Press Ctrl+C to stop.");
            var stop = new ManualResetEventSlim();
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; stop.Set(); };
            int lastCount = 0;
            while (!stop.Wait(2000))
            {
                int n = server.UniqueRecordCount;
                if (n != lastCount)
                {
                    Console.WriteLine(DateTime.Now.ToString("HH:mm:ss") + "  records received: " + n + "   devices: " + server.Devices.Count);
                    lastCount = n;
                }
            }
        }
        return 0;
    }
}
