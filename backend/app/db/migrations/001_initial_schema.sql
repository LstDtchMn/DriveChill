-- 001_initial_schema.sql
-- Creates all v1.0 tables: profiles, curves, settings, sensor labels,
-- alert rules, quiet hours, and auth log.
-- The existing sensor_log table is left as-is (created by LoggingService).

-- Profiles
CREATE TABLE IF NOT EXISTS profiles (
    id          TEXT PRIMARY KEY,
    name        TEXT NOT NULL,
    preset      TEXT NOT NULL DEFAULT 'custom',
    is_active   INTEGER NOT NULL DEFAULT 0,
    created_at  TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at  TEXT NOT NULL DEFAULT (datetime('now'))
);

-- Fan curves (many-to-one with profiles)
CREATE TABLE IF NOT EXISTS fan_curves (
    id          TEXT PRIMARY KEY,
    profile_id  TEXT NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
    name        TEXT NOT NULL,
    sensor_id   TEXT NOT NULL,
    fan_id      TEXT NOT NULL,
    enabled     INTEGER NOT NULL DEFAULT 1,
    points_json TEXT NOT NULL DEFAULT '[]'
);

CREATE INDEX IF NOT EXISTS idx_fan_curves_profile ON fan_curves(profile_id);

-- Application settings (key-value store)
CREATE TABLE IF NOT EXISTS settings (
    key         TEXT PRIMARY KEY,
    value       TEXT NOT NULL,
    updated_at  TEXT NOT NULL DEFAULT (datetime('now'))
);

-- Sensor labels (user-assigned friendly names)
CREATE TABLE IF NOT EXISTS sensor_labels (
    sensor_id   TEXT PRIMARY KEY,
    label       TEXT NOT NULL,
    updated_at  TEXT NOT NULL DEFAULT (datetime('now'))
);

-- Alert rules
CREATE TABLE IF NOT EXISTS alert_rules (
    id              TEXT PRIMARY KEY,
    sensor_id       TEXT NOT NULL,
    threshold       REAL NOT NULL,
    direction       TEXT NOT NULL DEFAULT 'above',
    enabled         INTEGER NOT NULL DEFAULT 1,
    cooldown_seconds INTEGER NOT NULL DEFAULT 300,
    created_at      TEXT NOT NULL DEFAULT (datetime('now'))
);

-- Quiet hours schedule
CREATE TABLE IF NOT EXISTS quiet_hours (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    day_of_week     INTEGER NOT NULL,   -- 0=Monday ... 6=Sunday
    start_time      TEXT NOT NULL,       -- HH:MM (24h)
    end_time        TEXT NOT NULL,       -- HH:MM (24h)
    profile_id      TEXT NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
    enabled         INTEGER NOT NULL DEFAULT 1
);

-- Auth audit log
CREATE TABLE IF NOT EXISTS auth_log (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    timestamp   TEXT NOT NULL DEFAULT (datetime('now')),
    event_type  TEXT NOT NULL,
    ip_address  TEXT,
    username    TEXT,
    outcome     TEXT NOT NULL,
    detail      TEXT
);

CREATE INDEX IF NOT EXISTS idx_auth_log_ts ON auth_log(timestamp);

-- Ensure sensor_log table exists (same schema LoggingService creates,
-- but now managed under the migration system)
CREATE TABLE IF NOT EXISTS sensor_log (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    timestamp   TEXT NOT NULL,
    sensor_id   TEXT NOT NULL,
    sensor_name TEXT NOT NULL,
    sensor_type TEXT NOT NULL,
    value       REAL NOT NULL,
    unit        TEXT
);

CREATE INDEX IF NOT EXISTS idx_sensor_log_ts     ON sensor_log(timestamp);
CREATE INDEX IF NOT EXISTS idx_sensor_log_sensor ON sensor_log(sensor_id, timestamp);
