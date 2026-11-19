using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Agent.Models;
using Microsoft.Extensions.Logging;

namespace Agent.Core;

/// <summary>
/// Collector output -> durable outbox. Also runs change detection and alert rules, whose results are queued as their own records.
/// Everything is persisted BEFORE any network attempt (write-ahead), so nothing is lost while offline.
/// </summary>
public sealed class CollectionPipeline : ICollectionSink
{
    private static readonly HashSet<string> SuppressWhenUnchanged = new HashSet<string>
    {
        Categories.Peripherals, Categories.NetworkConfig, Categories.Software, Categories.Device
    };

    private readonly IOutbox _outbox;
    private readonly ISnapshotStore _snapshots;
    private readonly IHistoryStore _history;
    private readonly ChangeDetector _changes;
    private readonly AlertEvaluator _alerts;
    private readonly MetricsCache _cache;
    private readonly IClock _clock;
    private readonly Func<string> _deviceId;
    private readonly ILogger _log;

    public CollectionPipeline(IOutbox outbox, ISnapshotStore snapshots, IHistoryStore history, ChangeDetector changes,
        AlertEvaluator alerts, MetricsCache cache, IClock clock, Func<string> deviceId, ILogger<CollectionPipeline> log)
    {
        _outbox = outbox;
        _snapshots = snapshots;
        _history = history;
        _changes = changes;
        _alerts = alerts;
        _cache = cache;
        _clock = clock;
        _deviceId = deviceId;
        _log = log;
    }

    public Task HandleAsync(string category, object payload, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var json = Json.Serialize(payload);
        _cache.Update(category, payload, now);

        // 1) derived records first so a crash between steps never produces a change without its snapshot
        var changeRecords = _changes.Detect(category, payload, now);
        foreach (var change in changeRecords)
        {
            try { _history.AddChange(change); } catch (Exception ex) { _log.LogWarning(ex, "Could not store change history"); }
            Enqueue(Categories.Changes, change.ItemKind + ":" + change.ItemKey + ":" + change.ChangeType, now, Json.Serialize(change));
        }

        foreach (var alert in _alerts.Evaluate(category, payload, now))
        {
            Enqueue(Categories.Alerts, alert.AlertKey + ":" + alert.State, alert.TimestampUtc, Json.Serialize(alert));
            _log.LogInformation("Alert {State} {Severity} {Rule} {Subject}: {Message}", alert.State, alert.Severity, alert.Rule, alert.Subject, alert.Message);
        }

        // 2) the snapshot itself
        if (SuppressWhenUnchanged.Contains(category))
        {
            var hash = Hashing.Sha256Hex(json);
            var last = _snapshots.GetHash("sent:" + category);
            var lastTime = _state(category);
            if (last == hash && lastTime.HasValue && now - lastTime.Value < TimeSpan.FromHours(24))
                return Task.CompletedTask;
            _snapshots.Save("sent:" + category, now.ToString("o", CultureInfo.InvariantCulture), hash, now);
        }

        Enqueue(category, null, now, json);
        return Task.CompletedTask;
    }

    private DateTime? _state(string category)
    {
        var s = _snapshots.GetJson("sent:" + category);
        DateTime d;
        return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out d) ? d : (DateTime?)null;
    }

    private void Enqueue(string category, string subjectKey, DateTime collectedUtc, string json)
    {
        var module = Categories.ModuleFor(category);
        var deviceId = _deviceId();
        var key = Hashing.Short(deviceId + "|" + category + "|" + (subjectKey ?? "") + "|"
                                + collectedUtc.Ticks.ToString(CultureInfo.InvariantCulture) + "|" + json, 32);
        var record = new OutboxRecord
        {
            Module = module,
            Category = category,
            IdempotencyKey = key,
            CollectedAtUtc = collectedUtc,
            PayloadJson = json,
            Priority = SyncPriority.For(module)
        };
        _outbox.Enqueue(record);
    }
}

/// <summary>Latest payload per category, in memory. Used by the health collector and heartbeat.</summary>
public sealed class MetricsCache
{
    private readonly Dictionary<string, KeyValuePair<object, DateTime>> _items = new Dictionary<string, KeyValuePair<object, DateTime>>();
    private readonly object _gate = new object();

    public void Update(string category, object payload, DateTime utc)
    {
        lock (_gate) _items[category] = new KeyValuePair<object, DateTime>(payload, utc);
    }

    public T Get<T>(string category) where T : class
    {
        lock (_gate)
        {
            KeyValuePair<object, DateTime> kv;
            return _items.TryGetValue(category, out kv) ? kv.Key as T : null;
        }
    }

    public DateTime? GetTime(string category)
    {
        lock (_gate)
        {
            KeyValuePair<object, DateTime> kv;
            return _items.TryGetValue(category, out kv) ? kv.Value : (DateTime?)null;
        }
    }
}
