CREATE TABLE IF NOT EXISTS virtual_sensors (
    id               TEXT PRIMARY KEY,
    name             TEXT NOT NULL,
    type             TEXT NOT NULL DEFAULT 'max',
    source_ids_json  TEXT NOT NULL DEFAULT '[]',
    weights_json     TEXT,
    window_seconds   REAL,
    "offset"         REAL NOT NULL DEFAULT 0.0,
    enabled          INTEGER NOT NULL DEFAULT 1,
    created_at       TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at       TEXT NOT NULL DEFAULT (datetime('now'))
);
