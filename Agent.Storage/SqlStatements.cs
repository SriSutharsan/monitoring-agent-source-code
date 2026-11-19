namespace Agent.Storage;

/// <summary>All SQL used by the agent. Kept in one dependency-free file so it can be validated against a real SQLite engine in CI.</summary>
public static class SqlStatements
{
    public const int SchemaVersion = 1;

    // BEGIN-SCHEMA
    public const string Schema = @"
CREATE TABLE IF NOT EXISTS outbox (
    id               INTEGER PRIMARY KEY AUTOINCREMENT,
    module           TEXT    NOT NULL,
    category         TEXT    NOT NULL,
    idem_key         TEXT    NOT NULL UNIQUE,
    collected_utc    TEXT    NOT NULL,
    payload          TEXT    NOT NULL,
    priority         INTEGER NOT NULL,
    status           INTEGER NOT NULL DEFAULT 0,
    attempts         INTEGER NOT NULL DEFAULT 0,
    next_attempt_utc TEXT    NULL,
    last_error       TEXT    NULL,
    created_utc      TEXT    NOT NULL,
    synced_utc       TEXT    NULL
);
CREATE INDEX IF NOT EXISTS ix_outbox_ready  ON outbox (status, module, priority, id);
CREATE INDEX IF NOT EXISTS ix_outbox_synced ON outbox (status, synced_utc);

CREATE TABLE IF NOT EXISTS kv_state (
    key   TEXT PRIMARY KEY,
    value TEXT NULL
);

CREATE TABLE IF NOT EXISTS snapshots (
    category    TEXT PRIMARY KEY,
    json        TEXT NOT NULL,
    hash        TEXT NOT NULL,
    updated_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS changes (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    category     TEXT NOT NULL,
    change_class TEXT NOT NULL,
    change_type  TEXT NOT NULL,
    item_kind    TEXT NOT NULL,
    item_key     TEXT NOT NULL,
    display_name TEXT NULL,
    summary      TEXT NULL,
    before_json  TEXT NULL,
    after_json   TEXT NULL,
    detected_utc TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_changes_time ON changes (detected_utc);

CREATE TABLE IF NOT EXISTS problems (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    key          TEXT NOT NULL,
    source       TEXT NULL,
    event_id     INTEGER NOT NULL,
    description  TEXT NULL,
    occurred_utc TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_problems_key  ON problems (key, occurred_utc);

CREATE TABLE IF NOT EXISTS health_history (
    id      INTEGER PRIMARY KEY AUTOINCREMENT,
    score   INTEGER NOT NULL,
    status  TEXT NOT NULL,
    json    TEXT NOT NULL,
    utc     TEXT NOT NULL
);
";
    // END-SCHEMA

    public const string Enqueue =
        "INSERT OR IGNORE INTO outbox (module, category, idem_key, collected_utc, payload, priority, status, attempts, created_utc) " +
        "VALUES (@module, @category, @key, @collected, @payload, @priority, 0, 0, @created)";

    public const string ReadyModules =
        "SELECT module FROM outbox WHERE status = 0 AND (next_attempt_utc IS NULL OR next_attempt_utc <= @now) " +
        "GROUP BY module ORDER BY MIN(priority), MIN(id)";

    public const string Dequeue =
        "SELECT id, module, category, idem_key, collected_utc, payload, priority, attempts FROM outbox " +
        "WHERE status = 0 AND module = @module AND (next_attempt_utc IS NULL OR next_attempt_utc <= @now) " +
        "ORDER BY priority, id LIMIT @max";

    public const string MarkSynced = "UPDATE outbox SET status = 1, synced_utc = @now, last_error = NULL WHERE id = @id";
    public const string MarkRetryCounted = "UPDATE outbox SET attempts = attempts + 1, next_attempt_utc = @next, last_error = @error WHERE id = @id AND status = 0";
    public const string MarkRetryUncounted = "UPDATE outbox SET next_attempt_utc = @next, last_error = @error WHERE id = @id AND status = 0";
    public const string MarkDead = "UPDATE outbox SET status = 2, last_error = @error WHERE id = @id";

    public const string Stats =
        "SELECT " +
        "COALESCE(SUM(CASE WHEN status = 0 THEN 1 ELSE 0 END), 0), " +
        "COALESCE(SUM(CASE WHEN status = 2 THEN 1 ELSE 0 END), 0), " +
        "COALESCE(SUM(CASE WHEN status = 1 THEN 1 ELSE 0 END), 0), " +
        "MIN(CASE WHEN status = 0 THEN collected_utc END) FROM outbox";

    public const string PruneSynced = "DELETE FROM outbox WHERE status = 1 AND synced_utc < @cutoff";
    public const string PruneDead = "DELETE FROM outbox WHERE status = 2 AND created_utc < @cutoff";
    public const string PruneOldPending = "DELETE FROM outbox WHERE status = 0 AND collected_utc < @cutoff";
    public const string CountPending = "SELECT COUNT(*) FROM outbox WHERE status = 0";
    public const string PruneOverflow =
        "DELETE FROM outbox WHERE id IN (SELECT id FROM outbox WHERE status = 0 ORDER BY priority DESC, id ASC LIMIT @excess)";

    public const string StateGet = "SELECT value FROM kv_state WHERE key = @key";
    public const string StateSet = "INSERT INTO kv_state (key, value) VALUES (@key, @value) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
    public const string StateDelete = "DELETE FROM kv_state WHERE key = @key";

    public const string SnapshotGet = "SELECT json, hash FROM snapshots WHERE category = @category";
    public const string SnapshotSave =
        "INSERT INTO snapshots (category, json, hash, updated_utc) VALUES (@category, @json, @hash, @now) " +
        "ON CONFLICT(category) DO UPDATE SET json = excluded.json, hash = excluded.hash, updated_utc = excluded.updated_utc";

    public const string ChangeAdd =
        "INSERT INTO changes (category, change_class, change_type, item_kind, item_key, display_name, summary, before_json, after_json, detected_utc) " +
        "VALUES (@category, @class, @type, @kind, @itemkey, @name, @summary, @before, @after, @detected)";
    public const string ChangeRecent =
        "SELECT detected_utc, change_class, summary FROM changes WHERE detected_utc >= @since ORDER BY detected_utc DESC LIMIT @max";

    public const string ProblemAdd = "INSERT INTO problems (key, source, event_id, description, occurred_utc) VALUES (@key, @source, @eventId, @desc, @occurred)";
    public const string ProblemRecurring =
        "SELECT key, MAX(source), MAX(event_id), MAX(description), COUNT(*), MIN(occurred_utc), MAX(occurred_utc) FROM problems " +
        "WHERE occurred_utc >= @since GROUP BY key HAVING COUNT(*) >= @min ORDER BY COUNT(*) DESC LIMIT 20";
    public const string ProblemCount = "SELECT COUNT(*) FROM problems WHERE key LIKE @prefix AND occurred_utc >= @since";

    public const string HealthAdd = "INSERT INTO health_history (score, status, json, utc) VALUES (@score, @status, @json, @now)";
    public const string PruneChanges = "DELETE FROM changes WHERE detected_utc < @cutoff";
    public const string PruneProblems = "DELETE FROM problems WHERE occurred_utc < @cutoff";
    public const string PruneHealth = "DELETE FROM health_history WHERE utc < @cutoff";
}
