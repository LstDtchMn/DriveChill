CREATE TABLE IF NOT EXISTS temperature_targets (
    id            TEXT PRIMARY KEY,
    name          TEXT NOT NULL DEFAULT '',
    drive_id      TEXT,
    sensor_id     TEXT NOT NULL,
    fan_ids_json  TEXT NOT NULL DEFAULT '[]',
    target_temp_c REAL NOT NULL,
    tolerance_c   REAL NOT NULL DEFAULT 5.0,
    min_fan_speed REAL NOT NULL DEFAULT 20.0,
    enabled       INTEGER NOT NULL DEFAULT 1,
    created_at    TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at    TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS idx_temp_targets_sensor ON temperature_targets (sensor_id);
