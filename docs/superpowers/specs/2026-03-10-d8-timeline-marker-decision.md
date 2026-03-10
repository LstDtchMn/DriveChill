# D8 Decision: Timeline-Marker Data Source

**Decision date:** 2026-03-10
**Status:** DECIDED — Option (c) Generic Event Log

## Problem

Alert events are in-memory only. There is no `alert_events` database table.
Timeline markers (for trend charts) and event annotations both need persistent
event storage. The data source must be chosen before either feature can be built.

## Options Evaluated

| Option | Description | Pros | Cons |
|--------|-------------|------|------|
| (a) `alert_events` table | Dedicated alert event persistence | Clean, focused | Write load on every alert; only covers alerts |
| (b) `webhook_deliveries` proxy | Reuse existing delivery records | No new tables | Misses alerts with no configured channels |
| **(c) Generic `event_log` table** | Reusable log for alerts, annotations, profile changes, startup | Supports E4 (annotations) + timeline markers with same infra | More design upfront |

## Decision

**Option (c): Generic `event_log` table.**

### Rationale

1. E4 (Event Annotations) needs persistent event storage with the same
   requirements (timestamp, label, optional payload). A generic table serves
   both use cases without schema duplication.
2. Future features (profile change history, startup events, maintenance logs)
   can reuse the same table with different `event_type` values.
3. The write load concern from option (a) applies equally here but is mitigated
   by the existing SQLite WAL mode and the low event frequency (alerts fire
   infrequently compared to sensor reads).

### Schema

```sql
CREATE TABLE IF NOT EXISTS event_log (
  id TEXT PRIMARY KEY,
  event_type TEXT NOT NULL,          -- 'alert' | 'annotation' | 'profile_change' | ...
  timestamp_utc TEXT NOT NULL,
  label TEXT NOT NULL,
  description TEXT,
  metadata_json TEXT,                -- type-specific payload (JSON)
  created_at TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS idx_event_log_type_ts ON event_log(event_type, timestamp_utc);
```

### Migration

This table will be created in migration `016_event_log.sql` when E4
(Event Annotations) is implemented. The alert persistence wiring can be
added at the same time or deferred until timeline markers are needed.

## Impact

- **E4 (Event Annotations):** Unblocked — use `event_type = 'annotation'`
- **Timeline markers:** Unblocked — query `event_type = 'alert'` entries
- **F4 (PDF Export):** Can include event markers in printed charts
