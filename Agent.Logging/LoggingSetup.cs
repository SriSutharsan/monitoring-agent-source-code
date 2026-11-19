using System;
using System.IO;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Agent.Logging;

/// <summary>Scrubs secrets from rendered messages, exception text and string properties.</summary>
public sealed class RedactingEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory factory)
    {
        foreach (var key in new System.Collections.Generic.List<string>(GetKeys(logEvent)))
        {
            LogEventPropertyValue value;
            if (!logEvent.Properties.TryGetValue(key, out value)) continue;
            var sv = value as ScalarValue;
            if (sv != null && sv.Value is string)
            {
                var original = (string)sv.Value;
                var clean = SecretRedactor.Redact(original);
                if (!ReferenceEquals(original, clean) && original != clean)
                    logEvent.AddOrUpdateProperty(new LogEventProperty(key, new ScalarValue(clean)));
            }
        }
    }

    private static System.Collections.Generic.IEnumerable<string> GetKeys(LogEvent e)
    {
        foreach (var kv in e.Properties) yield return kv.Key;
    }
}

public static class LoggingSetup
{
    /// <summary>Rolling file log (always) plus Windows Event Log (warnings and above) when permitted.</summary>
    public static Serilog.ILogger Create(string logDirectory, string minimumLevel, int retainedFiles, long fileSizeLimit, bool eventLog, string eventSource)
    {
        Directory.CreateDirectory(logDirectory);
        LogEventLevel level;
        if (!Enum.TryParse(minimumLevel, true, out level)) level = LogEventLevel.Information;

        var cfg = new LoggerConfiguration()
            .MinimumLevel.Is(level)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .Enrich.With(new RedactingEnricher())
            .Enrich.WithProperty("Pid", System.Diagnostics.Process.GetCurrentProcess().Id)
            .WriteTo.File(
                Path.Combine(logDirectory, "agent-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: retainedFiles,
                fileSizeLimitBytes: fileSizeLimit,
                rollOnFileSizeLimit: true,
                shared: false,
                flushToDiskInterval: TimeSpan.FromSeconds(5),
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}");

        if (eventLog)
        {
            try
            {
                cfg = cfg.WriteTo.EventLog(eventSource, manageEventSource: false, restrictedToMinimumLevel: LogEventLevel.Warning);
            }
            catch (Exception) { /* event source missing or no rights: file log still works */ }
        }
        return cfg.CreateLogger();
    }
}
