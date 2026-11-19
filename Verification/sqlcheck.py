"""Executes the agent's real SQL (extracted from SqlStatements.cs) against SQLite and checks outbox semantics."""
import re, sqlite3, sys, datetime

src = open(sys.argv[1], encoding="utf-8").read()

schema = re.search(r'Schema = @"(.*?)";\s*// END-SCHEMA', src, re.S).group(1).replace('""', '"')

def const(name):
    m = re.search(r'public const string ' + name + r'\s*=\s*(.*?);\n', src, re.S)
    parts = re.findall(r'"((?:[^"\\]|\\.)*)"', m.group(1))
    return "".join(parts)

# C# @param -> sqlite :param
def q(name):
    return re.sub(r'@(\w+)', r':\1', const(name))

db = sqlite3.connect(":memory:")
db.execute("PRAGMA journal_mode=WAL")
db.executescript(schema)
fails = 0

def check(cond, msg):
    global fails
    print(("  PASS  " if cond else "  FAIL  ") + msg)
    if not cond: fails += 1

def iso(minutes=0):
    return (datetime.datetime(2026, 11, 18, 10, 0, 0) + datetime.timedelta(minutes=minutes)).strftime("%Y-%m-%dT%H:%M:%S.0000000Z")

def enqueue(module, key, prio, minutes=0, cat=None):
    cur = db.execute(q("Enqueue"), dict(module=module, category=cat or module, key=key, collected=iso(minutes),
                                       payload="{}", priority=prio, created=iso(minutes)))
    return cur.rowcount

check(enqueue("performance", "k1", 6) == 1, "enqueue inserts")
check(enqueue("performance", "k1", 6) == 0, "duplicate idempotency key ignored")
enqueue("alerts", "a1", 1, 1)
enqueue("events", "e1", 2, 2)
enqueue("heartbeat", "h1", 5, 3)

mods = [r[0] for r in db.execute(q("ReadyModules"), dict(now=iso(10)))]
check(mods == ["alerts", "events", "heartbeat", "performance"], "modules ordered by priority: %s" % mods)

rows = db.execute(q("Dequeue"), dict(module="performance", now=iso(10), max=10)).fetchall()
check(len(rows) == 1 and rows[0][3] == "k1", "dequeue returns pending record")

# retry with backoff hides the record until due
rid = rows[0][0]
db.execute(q("MarkRetryCounted"), dict(next=iso(30), error="HTTP 503", id=rid))
check(len(db.execute(q("Dequeue"), dict(module="performance", now=iso(10), max=10)).fetchall()) == 0, "backed-off record hidden")
check(len(db.execute(q("Dequeue"), dict(module="performance", now=iso(31), max=10)).fetchall()) == 1, "record returns when due")
check(db.execute("SELECT attempts FROM outbox WHERE id=?", (rid,)).fetchone()[0] == 1, "attempt counted")
db.execute(q("MarkRetryUncounted"), dict(next=iso(40), error="offline", id=rid))
check(db.execute("SELECT attempts FROM outbox WHERE id=?", (rid,)).fetchone()[0] == 1, "offline retry not counted as attempt")

# synced / dead / stats
db.execute(q("MarkSynced"), dict(now=iso(5), id=rid))
dead_id = db.execute("SELECT id FROM outbox WHERE idem_key='e1'").fetchone()[0]
db.execute(q("MarkDead"), dict(error="400 validation", id=dead_id))
pending, dead, synced, oldest = db.execute(q("Stats")).fetchone()
check((pending, dead, synced) == (2, 1, 1), "stats pending/dead/synced = %s/%s/%s" % (pending, dead, synced))
check(db.execute(q("Dequeue"), dict(module="events", now=iso(99), max=5)).fetchall() == [], "dead record never dequeued")

# prune
db.execute(q("PruneSynced"), dict(cutoff=iso(60)))
check(db.execute("SELECT COUNT(*) FROM outbox WHERE status=1").fetchone()[0] == 0, "synced rows pruned after retention")
db.execute(q("PruneOldPending"), dict(cutoff=iso(2)))
check(db.execute("SELECT COUNT(*) FROM outbox WHERE idem_key='a1'").fetchone()[0] == 0, "old pending pruned by age")

# overflow drops lowest-priority (highest number) first
db.execute("DELETE FROM outbox")
enqueue("alerts", "A", 1, 0); enqueue("performance", "P1", 6, 1); enqueue("performance", "P2", 6, 2); enqueue("events", "E", 2, 3)
db.execute(q("PruneOverflow"), dict(excess=2))
left = sorted(r[0] for r in db.execute("SELECT idem_key FROM outbox"))
check(left == ["A", "E"], "overflow keeps alerts/events, drops performance: %s" % left)

# kv state upsert
db.execute(q("StateSet"), dict(key="k", value="1")); db.execute(q("StateSet"), dict(key="k", value="2"))
check(db.execute(q("StateGet"), dict(key="k")).fetchone()[0] == "2", "state upsert")
db.execute(q("StateDelete"), dict(key="k"))
check(db.execute(q("StateGet"), dict(key="k")).fetchone() is None, "state delete")

# snapshots upsert
db.execute(q("SnapshotSave"), dict(category="hw", json="a", hash="h1", now=iso())); db.execute(q("SnapshotSave"), dict(category="hw", json="b", hash="h2", now=iso()))
check(db.execute(q("SnapshotGet"), dict(category="hw")).fetchone() == ("b", "h2"), "snapshot upsert")

# recurring problems
for i in range(5):
    db.execute(q("ProblemAdd"), dict(key="app_error:chrome.exe", source="Application Error", eventId=1000, desc="crash", occurred=iso(i)))
db.execute(q("ProblemAdd"), dict(key="bsod:1001", source="BugCheck", eventId=1001, desc="bugcheck", occurred=iso(1)))
rec = db.execute(q("ProblemRecurring"), dict(since=iso(-5), min=3)).fetchall()
check(len(rec) == 1 and rec[0][4] == 5, "recurring problem grouping")
check(db.execute(q("ProblemCount"), dict(prefix="bsod:%", since=iso(-5))).fetchone()[0] == 1, "problem count by prefix")

# changes + health
db.execute(q("ChangeAdd"), dict(category="hardware", **{"class": "ram_changed"}, type="modified", kind="memory_module", itemkey="k", name="n", summary="s", before=None, after=None, detected=iso(1)))
check(len(db.execute(q("ChangeRecent"), dict(since=iso(0), max=5)).fetchall()) == 1, "change history stored and queried")
db.execute(q("HealthAdd"), dict(score=90, status="healthy", json="{}", now=iso(0)))
db.execute(q("PruneHealth"), dict(cutoff=iso(5)))
db.execute(q("PruneChanges"), dict(cutoff=iso(5)))
check(db.execute("SELECT COUNT(*) FROM health_history").fetchone()[0] == 0, "health history pruned")

print("\nSQL checks: %s" % ("OK" if fails == 0 else "%d FAILED" % fails))
sys.exit(1 if fails else 0)
