using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Agent.Core;
using Agent.Models;
using Microsoft.Extensions.Logging;

namespace Agent.Collectors.Events;

/// <summary>
/// Incremental Windows event collection. Each log has a persisted record-id cursor, so every event is read exactly once even across
/// service restarts; a cleared log (record ids restart) is detected and handled. One XPath query per log keeps the cursor correct.
/// Reading the Security log needs administrator rights (the service runs as LocalSystem); logs that cannot be read are skipped and
/// reported once, never fatal.
/// </summary>
public sealed class EventsCollector : CollectorBase<WindowsEventBatch>
{
    private const string CursorPrefix = "events.cursor.";
    private const string LastRunKey = "events.last_run_utc";

    private readonly IEventLogReader _reader;
    private readonly IStateStore _state;
    private readonly IHistoryStore _history;
    private readonly IClock _clock;
    private readonly ILogger _log;
    private readonly HashSet<string> _reportedUnreadable = new HashSet<string>();

    public EventsCollector(IEventLogReader reader, IStateStore state, IHistoryStore history, IClock clock, ILogger<EventsCollector> log)
    {
        _reader = reader;
        _state = state;
        _history = history;
        _clock = clock;
        _log = log;
    }

    public override string Category { get { return Categories.Events; } }
    public override TimeSpan Timeout { get { return TimeSpan.FromSeconds(90); } }

    public override Task<WindowsEventBatch> CollectTypedAsync(CollectionContext ctx, CancellationToken ct)
    {
        return Task.FromResult(Collect(ctx.DeviceId, ctx.Config.Events ?? new EventConfig(), ct));
    }

    public WindowsEventBatch Collect(string deviceId, EventConfig cfg, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var lastRunText = _state.Get(LastRunKey);
        DateTime lastRun;
        var from = DateTime.TryParse(lastRunText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out lastRun)
            ? lastRun : now.AddMinutes(-cfg.FirstRunLookbackMinutes);

        var events = new List<WindowsEventRecord>();
        int budget = Math.Max(10, cfg.MaxEventsPerCycle);

        foreach (var log in EventCatalog.Logs)
        {
            ct.ThrowIfCancellationRequested();
            if (budget <= 0) break;
            var predicate = EventCatalog.PredicateFor(log, cfg);
            if (predicate == null) continue;

            long cursor = ParseCursor(_state.Get(CursorPrefix + log));
            long last = SafeLastId(log);
            if (cursor > 0 && last > 0 && last < cursor)
            {
                _log.LogInformation("Event log {Log} was cleared (record ids restarted); resetting its cursor", log);
                cursor = 0;
            }

            var spec = new EventQuerySpec
            {
                LogName = log,
                MinRecordIdExclusive = cursor,
                SinceUtc = cursor == 0 ? (DateTime?)(lastRunText == null ? now.AddMinutes(-cfg.FirstRunLookbackMinutes) : from) : null,
                Predicate = predicate,
                MaxEvents = budget
            };

            string error;
            var raw = _reader.Read(spec, out error);
            if (error != null)
            {
                if (_reportedUnreadable.Add(log)) _log.LogWarning("Event log {Log} cannot be read: {Error}", log, error);
                continue;
            }
            _reportedUnreadable.Remove(log);

            long maxSeen = cursor;
            foreach (var e in raw.OrderBy(r => r.RecordId))
            {
                if (e.RecordId > maxSeen) maxSeen = e.RecordId;
                var cls = EventCatalog.Classify(log, e, cfg);
                if (cls == null) continue;
                events.Add(Build(deviceId, log, e, cls, cfg));
                budget--;
            }

            bool truncated = raw.Count >= spec.MaxEvents;
            // everything up to "last" was inspected when the result was not truncated, so skip ahead and avoid rescanning big logs every minute
            long newCursor = truncated ? maxSeen : Math.Max(maxSeen, last);
            if (newCursor != cursor) _state.Set(CursorPrefix + log, newCursor.ToString(CultureInfo.InvariantCulture));
        }

        _state.Set(LastRunKey, now.ToString("o", CultureInfo.InvariantCulture));
        if (events.Count == 0) return null;

        foreach (var e in events)
        {
            if (!EventCatalog.ProblemCategories.Contains(e.Category)) continue;
            try
            {
                var meta = e.Metadata ?? new Dictionary<string, string>();
                var key = EventCatalog.ProblemKey(e.Category, new RawEventRecord { Provider = e.Source, EventId = e.EventId }, meta);
                _history.RecordProblem(key, e.Source, e.EventId, e.Category, e.TimestampUtc);
            }
            catch (Exception ex) { _log.LogDebug(ex, "Could not record problem history"); }
        }

        return new WindowsEventBatch
        {
            Events = events.OrderBy(e => e.TimestampUtc).ToList(),
            DroppedCount = 0,
            FromUtc = from,
            ToUtc = now
        };
    }

    private long SafeLastId(string log)
    {
        try { return _reader.GetLastRecordId(log); } catch (Exception) { return 0; }
    }

    private static long ParseCursor(string s)
    {
        long v;
        return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) && v > 0 ? v : 0;
    }

    private static WindowsEventRecord Build(string deviceId, string log, RawEventRecord e, EventClassification cls, EventConfig cfg)
    {
        var message = e.Message == null ? "" : System.Text.RegularExpressions.Regex.Replace(e.Message, @"\s+", " ").Trim();
        if (message.Length == 0 && e.Data != null)
            message = Text.Truncate(string.Join("; ", e.Data.Values.Where(v => !string.IsNullOrWhiteSpace(v)).Take(6)), 300);
        return new WindowsEventRecord
        {
            DeviceId = deviceId,
            Log = log,
            RecordId = e.RecordId,
            EventId = e.EventId,
            Source = e.Provider,
            Level = EventCatalog.LevelName(e.Level),
            Message = Text.Truncate(message, Math.Max(100, cfg.MaxMessageLength)),
            TimestampUtc = e.TimestampUtc,
            Category = cls.Category,
            Severity = cls.Severity,
            Computer = e.Computer,
            Metadata = EventCatalog.Metadata(log, cls.Category, e)
        };
    }
}
