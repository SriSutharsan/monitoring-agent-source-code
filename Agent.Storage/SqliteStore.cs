using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.IO;
using System.Linq;
using Agent.Core;
using Agent.Models;
using Microsoft.Extensions.Logging;

namespace Agent.Storage;

/// <summary>
/// Single SQLite database holding the offline outbox, key/value state, inventory snapshots and history.
/// WAL journal mode gives crash safety: a killed service or power loss never leaves a half-written record,
/// and a record is only removed after the API has acknowledged it.
/// </summary>
public sealed class SqliteStore : IOutbox, IStateStore, ISnapshotStore, IHistoryStore
{
    private readonly Func<DbConnection> _factory;
    private readonly string _path;
    private readonly ILogger _log;

    public SqliteStore(Func<DbConnection> connectionFactory, string databasePath, ILogger<SqliteStore> log)
    {
        _factory = connectionFactory;
        _path = databasePath;
        _log = log;
    }

    // ------------------------------------------------------------- lifecycle

    public void Initialize()
    {
        try
        {
            OpenAndMigrate();
        }
        catch (DbException ex)
        {
            _log.LogError(ex, "Local database is unreadable; quarantining it and starting a new one");
            Quarantine();
            OpenAndMigrate();
        }
    }

    private void OpenAndMigrate()
    {
        using (var c = Open())
        {
            var quick = ScalarString(c, "PRAGMA quick_check(1)");
            if (!string.Equals(quick, "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("integrity check failed: " + quick);
            Exec(c, "PRAGMA journal_mode=WAL");
            Exec(c, SqlStatements.Schema);
            Exec(c, "PRAGMA user_version=" + SqlStatements.SchemaVersion.ToString(CultureInfo.InvariantCulture));
        }
    }

    private void Quarantine()
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var f = _path + suffix;
            try { if (File.Exists(f)) File.Move(f, _path + ".corrupt-" + stamp + suffix); }
            catch (IOException) { try { File.Delete(f); } catch (IOException) { } }
        }
    }

    private DbConnection Open()
    {
        var c = _factory();
        c.Open();
        Exec(c, "PRAGMA busy_timeout=5000");
        Exec(c, "PRAGMA synchronous=NORMAL");
        return c;
    }

    // -------------------------------------------------------------- helpers

    public static string Iso(DateTime d)
    {
        var u = d.Kind == DateTimeKind.Utc ? d : d.ToUniversalTime();
        return u.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);
    }

    public static DateTime ParseIso(string s)
    {
        return DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
    }

    private static DbCommand Cmd(DbConnection c, string sql, DbTransaction tx = null, params object[] nameValuePairs)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        for (int i = 0; i + 1 < nameValuePairs.Length; i += 2)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = "@" + (string)nameValuePairs[i];
            p.Value = nameValuePairs[i + 1] ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }
        return cmd;
    }

    private static int Exec(DbConnection c, string sql, DbTransaction tx = null, params object[] args)
    {
        using (var cmd = Cmd(c, sql, tx, args)) return cmd.ExecuteNonQuery();
    }

    private static string ScalarString(DbConnection c, string sql, params object[] args)
    {
        using (var cmd = Cmd(c, sql, null, args))
        {
            var o = cmd.ExecuteScalar();
            return o == null || o is DBNull ? null : Convert.ToString(o, CultureInfo.InvariantCulture);
        }
    }

    private static long ScalarLong(DbConnection c, string sql, params object[] args)
    {
        var s = ScalarString(c, sql, args);
        long v;
        return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : 0;
    }

    // ---------------------------------------------------------------- outbox

    public bool Enqueue(OutboxRecord r)
    {
        using (var c = Open())
        {
            int n = Exec(c, SqlStatements.Enqueue, null,
                "module", r.Module, "category", r.Category, "key", r.IdempotencyKey, "collected", Iso(r.CollectedAtUtc),
                "payload", r.PayloadJson, "priority", r.Priority, "created", Iso(DateTime.UtcNow));
            return n > 0;
        }
    }

    public IReadOnlyList<string> ModulesWithReadyRecords(DateTime nowUtc)
    {
        var list = new List<string>();
        using (var c = Open())
        using (var cmd = Cmd(c, SqlStatements.ReadyModules, null, "now", Iso(nowUtc)))
        using (var rd = cmd.ExecuteReader())
            while (rd.Read()) list.Add(rd.GetString(0));
        return list;
    }

    public IReadOnlyList<OutboxRecord> Dequeue(string module, int max, DateTime nowUtc)
    {
        var list = new List<OutboxRecord>();
        using (var c = Open())
        using (var cmd = Cmd(c, SqlStatements.Dequeue, null, "module", module, "now", Iso(nowUtc), "max", max))
        using (var rd = cmd.ExecuteReader())
        {
            while (rd.Read())
            {
                list.Add(new OutboxRecord
                {
                    Id = rd.GetInt64(0),
                    Module = rd.GetString(1),
                    Category = rd.GetString(2),
                    IdempotencyKey = rd.GetString(3),
                    CollectedAtUtc = ParseIso(rd.GetString(4)),
                    PayloadJson = rd.GetString(5),
                    Priority = rd.GetInt32(6),
                    Attempts = rd.GetInt32(7)
                });
            }
        }
        return list;
    }

    private void InTransaction(IEnumerable<long> ids, string sql, Func<long, object[]> args)
    {
        var idList = ids.ToList();
        if (idList.Count == 0) return;
        using (var c = Open())
        using (var tx = c.BeginTransaction())
        {
            foreach (var id in idList) Exec(c, sql, tx, args(id));
            tx.Commit();
        }
    }

    public void MarkSynced(IEnumerable<long> ids, DateTime nowUtc)
    {
        var now = Iso(nowUtc);
        InTransaction(ids, SqlStatements.MarkSynced, id => new object[] { "now", now, "id", id });
    }

    public void MarkRetry(IEnumerable<long> ids, DateTime nextAttemptUtc, string error, bool countAttempt)
    {
        var next = Iso(nextAttemptUtc);
        var err = Text.Truncate(error, 500);
        InTransaction(ids, countAttempt ? SqlStatements.MarkRetryCounted : SqlStatements.MarkRetryUncounted,
            id => new object[] { "next", next, "error", err, "id", id });
    }

    public void MarkDead(IEnumerable<long> ids, string error)
    {
        var err = Text.Truncate(error, 500);
        InTransaction(ids, SqlStatements.MarkDead, id => new object[] { "error", err, "id", id });
    }

    public OutboxStats GetStats()
    {
        using (var c = Open())
        using (var cmd = Cmd(c, SqlStatements.Stats))
        using (var rd = cmd.ExecuteReader())
        {
            rd.Read();
            return new OutboxStats
            {
                Pending = Convert.ToInt32(rd.GetValue(0), CultureInfo.InvariantCulture),
                Dead = Convert.ToInt32(rd.GetValue(1), CultureInfo.InvariantCulture),
                Synced = Convert.ToInt32(rd.GetValue(2), CultureInfo.InvariantCulture),
                OldestPendingUtc = rd.IsDBNull(3) ? (DateTime?)null : ParseIso(rd.GetString(3))
            };
        }
    }

    public int Prune(RetentionConfig r, DateTime nowUtc)
    {
        int removed = 0;
        using (var c = Open())
        {
            removed += Exec(c, SqlStatements.PruneSynced, null, "cutoff", Iso(nowUtc.AddDays(-Math.Max(1, r.SyncedRetentionDays))));
            removed += Exec(c, SqlStatements.PruneDead, null, "cutoff", Iso(nowUtc.AddDays(-14)));
            removed += Exec(c, SqlStatements.PruneOldPending, null, "cutoff", Iso(nowUtc.AddDays(-Math.Max(1, r.PendingMaxAgeDays))));
            long pending = ScalarLong(c, SqlStatements.CountPending);
            if (pending > r.MaxPendingRecords)
            {
                var excess = (int)Math.Min(int.MaxValue, pending - r.MaxPendingRecords);
                removed += Exec(c, SqlStatements.PruneOverflow, null, "excess", excess);
                _log.LogWarning("Offline queue exceeded {Max} records; dropped {Dropped} lowest-priority records", r.MaxPendingRecords, excess);
            }
            if (removed > 1000) Exec(c, "PRAGMA wal_checkpoint(TRUNCATE)");
        }
        return removed;
    }

    // ----------------------------------------------------------------- state

    public string Get(string key)
    {
        using (var c = Open()) return ScalarString(c, SqlStatements.StateGet, "key", key);
    }

    public void Set(string key, string value)
    {
        using (var c = Open()) Exec(c, SqlStatements.StateSet, null, "key", key, "value", value);
    }

    public void Delete(string key)
    {
        using (var c = Open()) Exec(c, SqlStatements.StateDelete, null, "key", key);
    }

    // ------------------------------------------------------------- snapshots

    public string GetJson(string category)
    {
        using (var c = Open())
        using (var cmd = Cmd(c, SqlStatements.SnapshotGet, null, "category", category))
        using (var rd = cmd.ExecuteReader())
            return rd.Read() ? rd.GetString(0) : null;
    }

    public string GetHash(string category)
    {
        using (var c = Open())
        using (var cmd = Cmd(c, SqlStatements.SnapshotGet, null, "category", category))
        using (var rd = cmd.ExecuteReader())
            return rd.Read() ? rd.GetString(1) : null;
    }

    public void Save(string category, string json, string hash, DateTime nowUtc)
    {
        using (var c = Open())
            Exec(c, SqlStatements.SnapshotSave, null, "category", category, "json", json, "hash", hash, "now", Iso(nowUtc));
    }

    // --------------------------------------------------------------- history

    public void AddChange(ChangeRecord ch)
    {
        using (var c = Open())
            Exec(c, SqlStatements.ChangeAdd, null,
                "category", ch.Category, "class", ch.ChangeClass, "type", ch.ChangeType, "kind", ch.ItemKind, "itemkey", ch.ItemKey,
                "name", ch.DisplayName, "summary", ch.Summary, "before", ch.Before, "after", ch.After, "detected", Iso(ch.DetectedUtc));
    }

    public IReadOnlyList<MaintenanceEntry> RecentMaintenance(int days, int max)
    {
        var list = new List<MaintenanceEntry>();
        using (var c = Open())
        using (var cmd = Cmd(c, SqlStatements.ChangeRecent, null, "since", Iso(DateTime.UtcNow.AddDays(-days)), "max", max))
        using (var rd = cmd.ExecuteReader())
            while (rd.Read())
                list.Add(new MaintenanceEntry { TimestampUtc = ParseIso(rd.GetString(0)), Type = rd.GetString(1), Summary = rd.IsDBNull(2) ? null : rd.GetString(2) });
        return list;
    }

    public void RecordProblem(string key, string source, int eventId, string description, DateTime occurredUtc)
    {
        using (var c = Open())
            Exec(c, SqlStatements.ProblemAdd, null, "key", key, "source", source, "eventId", eventId, "desc", Text.Truncate(description, 300), "occurred", Iso(occurredUtc));
    }

    public IReadOnlyList<RecurringProblem> RecurringProblems(DateTime sinceUtc, int minOccurrences)
    {
        var list = new List<RecurringProblem>();
        using (var c = Open())
        using (var cmd = Cmd(c, SqlStatements.ProblemRecurring, null, "since", Iso(sinceUtc), "min", minOccurrences))
        using (var rd = cmd.ExecuteReader())
            while (rd.Read())
                list.Add(new RecurringProblem
                {
                    Key = rd.GetString(0),
                    Source = rd.IsDBNull(1) ? null : rd.GetString(1),
                    EventId = Convert.ToInt32(rd.GetValue(2), CultureInfo.InvariantCulture),
                    Description = rd.IsDBNull(3) ? null : rd.GetString(3),
                    Occurrences = Convert.ToInt32(rd.GetValue(4), CultureInfo.InvariantCulture),
                    FirstSeenUtc = ParseIso(rd.GetString(5)),
                    LastSeenUtc = ParseIso(rd.GetString(6))
                });
        return list;
    }

    public int CountProblems(string categoryPrefix, DateTime sinceUtc)
    {
        using (var c = Open())
            return (int)ScalarLong(c, SqlStatements.ProblemCount, "prefix", categoryPrefix + "%", "since", Iso(sinceUtc));
    }

    public void AddHealthSnapshot(DeviceHealth h, DateTime nowUtc)
    {
        using (var c = Open())
            Exec(c, SqlStatements.HealthAdd, null, "score", h.Score, "status", h.Status, "json", Json.Serialize(h), "now", Iso(nowUtc));
    }

    void IHistoryStore.Prune(RetentionConfig r, DateTime nowUtc)
    {
        using (var c = Open())
        {
            Exec(c, SqlStatements.PruneChanges, null, "cutoff", Iso(nowUtc.AddDays(-Math.Max(1, r.ChangeHistoryDays))));
            Exec(c, SqlStatements.PruneProblems, null, "cutoff", Iso(nowUtc.AddDays(-Math.Max(1, r.HealthHistoryDays))));
            Exec(c, SqlStatements.PruneHealth, null, "cutoff", Iso(nowUtc.AddDays(-Math.Max(1, r.HealthHistoryDays))));
        }
    }
}
