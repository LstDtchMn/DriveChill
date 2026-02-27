-- 005_machine_registry.sql
-- Multi-machine hub registry for v1.5.

CREATE TABLE IF NOT EXISTS machines (
    id                      TEXT PRIMARY KEY,
    name                    TEXT NOT NULL,
    base_url                TEXT NOT NULL,
    api_key                 TEXT,
    enabled                 INTEGER NOT NULL DEFAULT 1,
    poll_interval_seconds   REAL NOT NULL DEFAULT 2.0,
    timeout_ms              INTEGER NOT NULL DEFAULT 1200,
    status                  TEXT NOT NULL DEFAULT 'unknown',
    last_seen_at            TEXT,
    last_error              TEXT,
    consecutive_failures    INTEGER NOT NULL DEFAULT 0,
    created_at              TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at              TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE INDEX IF NOT EXISTS idx_machines_enabled ON machines(enabled);
CREATE INDEX IF NOT EXISTS idx_machines_status ON machines(status);
