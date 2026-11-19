using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agent.Models;

public static class SchemaInfo
{
    public const string SchemaVersion = "1.0";
    public const string ApiVersion = "v1";
}

/// <summary>Collector categories (scheduler keys) and the API "module" each one syncs through.</summary>
public static class Categories
{
    public const string Heartbeat = "heartbeat";
    public const string Device = "device";
    public const string Hardware = "hardware";
    public const string Peripherals = "hardware.peripherals";
    public const string Storage = "storage";
    public const string NetworkConfig = "network.config";
    public const string Wifi = "network.wifi";
    public const string PerfCpuMemory = "perf.cpu_memory";
    public const string PerfDisk = "perf.disk";
    public const string PerfNetwork = "perf.network";
    public const string Battery = "battery";
    public const string Software = "software";
    public const string Security = "security";
    public const string Events = "events";
    public const string Health = "health";
    public const string Diagnostics = "diagnostics";

    // Derived (pipeline-generated) record kinds
    public const string Alerts = "alerts";
    public const string Changes = "changes";

    public static readonly string[] AllCollectorCategories =
    {
        Heartbeat, Device, Hardware, Peripherals, Storage, NetworkConfig, Wifi, PerfCpuMemory, PerfDisk,
        PerfNetwork, Battery, Software, Security, Events, Health, Diagnostics
    };

    /// <summary>Maps a collector category to the API module (endpoint) that carries it.</summary>
    public static string ModuleFor(string category)
    {
        switch (category)
        {
            case Heartbeat: return Modules.Heartbeat;
            case Device: return Modules.Device;
            case Hardware:
            case Peripherals: return Modules.Hardware;
            case Storage: return Modules.Storage;
            case NetworkConfig:
            case Wifi: return Modules.Network;
            case PerfCpuMemory:
            case PerfDisk:
            case PerfNetwork:
            case Battery: return Modules.Performance;
            case Software: return Modules.Software;
            case Security: return Modules.Security;
            case Events: return Modules.Events;
            case Health: return Modules.Health;
            case Diagnostics: return Modules.Diagnostics;
            case Alerts: return Modules.Alerts;
            case Changes: return Modules.Changes;
            default: return category;
        }
    }
}

public static class Modules
{
    public const string Heartbeat = "heartbeat";
    public const string Device = "device";
    public const string Hardware = "hardware";
    public const string Storage = "storage";
    public const string Network = "network";
    public const string Performance = "performance";
    public const string Software = "software";
    public const string Security = "security";
    public const string Events = "events";
    public const string Health = "health";
    public const string Diagnostics = "diagnostics";
    public const string Alerts = "alerts";
    public const string Changes = "changes";
}

public static class Severity
{
    public const string Info = "info";
    public const string Warning = "warning";
    public const string Critical = "critical";
}

/// <summary>JSON helper so serializer settings live in exactly one place.</summary>
public static class Json
{
    private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private static readonly JsonSerializerOptions IndentedOptions = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    public static string Serialize(object value)
    {
        return JsonSerializer.Serialize(value, value == null ? typeof(object) : value.GetType(), Options);
    }

    public static string SerializeIndented(object value)
    {
        return JsonSerializer.Serialize(value, value == null ? typeof(object) : value.GetType(), IndentedOptions);
    }

    public static T Deserialize<T>(string json)
    {
        return JsonSerializer.Deserialize<T>(json, Options);
    }

    public static bool TryDeserialize<T>(string json, out T value)
    {
        value = default(T);
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            value = JsonSerializer.Deserialize<T>(json, Options);
            return value != null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static object Deserialize(string json, Type type)
    {
        return JsonSerializer.Deserialize(json, type, Options);
    }

    /// <summary>True when the text is syntactically valid JSON (used to validate payloads before queueing).</summary>
    public static bool IsValid(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using (JsonDocument.Parse(json)) return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
