-- Migration 008: drive monitoring subsystem
-- Tables: drives, drive_health_snapshots, drive_attributes_latest,
--         drive_self_test_runs, drive_settings_overrides

-- ─────────────────────────────────────────────────────────────────────────────
-- Drive inventory (latest known state; refreshed on rescan and health poll)
-- ─────────────────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS drives (
    id               TEXT PRIMARY KEY,  -- canonical drive_id (hash)
    name             TEXT NOT NULL,
    model            TEXT NOT NULL DEFAULT '',
    serial_full      TEXT NOT NULL DEFAULT '',  -- stored at rest; masked in list APIs
    device_path      TEXT NOT NULL DEFAULT '',  -- server-trusted; never user-supplied
    bus_type         TEXT NOT NULL DEFAULT 'unknown',  -- sata|nvme|usb|raid|unknown
    media_type       TEXT NOT NULL DEFAULT 'unknown',  -- hdd|ssd|nvme|unknown
    capacity_bytes   INTEGER NOT NULL DEFAULT 0,
    firmware_version TEXT NOT NULL DEFAULT '',
    smart_available  INTEGER NOT NULL DEFAULT 0,
    native_available INTEGER NOT NULL DEFAULT 0,
    supports_self_test INTEGER NOT NULL DEFAULT 0,
    supports_abort   INTEGER NOT NULL DEFAULT 0,
    last_seen_at     TEXT NOT NULL DEFAULT (datetime('now')),
    last_updated_at  TEXT NOT NULL DEFAULT (datetime('now'))
);

-- ─────────────────────────────────────────────────────────────────────────────
-- Health snapshots (used for trending and delta alert detection)
-- ─────────────────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS drive_health_snapshots (
    id                    INTEGER PRIMARY KEY AUTOINCREMENT,
    drive_id              TEXT NOT NULL REFERENCES drives(id) ON DELETE CASCADE,
    recorded_at           TEXT NOT NULL DEFAULT (datetime('now')),
    temperature_c         REAL,
    health_status         TEXT NOT NULL DEFAULT 'unknown',  -- good|warning|critical|unknown
    health_percent        REAL,
    predicted_failure     INTEGER NOT NULL DEFAULT 0,
    wear_percent_used     REAL,
    available_spare_percent REAL,
    reallocated_sectors   INTEGER,
    pending_sectors       INTEGER,
    uncorrectable_errors  INTEGER,
    media_errors          INTEGER,
    power_on_hours        INTEGER,
    unsafe_shutdowns      INTEGER
);

CREATE INDEX IF NOT EXISTS idx_drive_snapshots_drive_time
    ON drive_health_snapshots (drive_id, recorded_at);

-- ─────────────────────────────────────────────────────────────────────────────
-- Latest raw SMART/NVMe attribute payload (one row per drive, replaced on each poll)
-- ─────────────────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS drive_attributes_latest (
    drive_id        TEXT PRIMARY KEY REFERENCES drives(id) ON DELETE CASCADE,
    captured_at     TEXT NOT NULL DEFAULT (datetime('now')),
    attributes_json TEXT NOT NULL DEFAULT '[]'
);

-- ─────────────────────────────────────────────────────────────────────────────
-- Self-test run history
-- ─────────────────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS drive_self_test_runs (
    id               TEXT PRIMARY KEY,
    drive_id         TEXT NOT NULL REFERENCES drives(id) ON DELETE CASCADE,
    type             TEXT NOT NULL,     -- short|extended|conveyance
    status           TEXT NOT NULL DEFAULT 'queued',  -- queued|running|passed|failed|aborted|unsupported
    progress_percent REAL,
    started_at       TEXT NOT NULL DEFAULT (datetime('now')),
    finished_at      TEXT,
    failure_message  TEXT,
    provider_run_ref TEXT
);

CREATE INDEX IF NOT EXISTS idx_self_test_drive ON drive_self_test_runs (drive_id, started_at DESC);

-- ─────────────────────────────────────────────────────────────────────────────
-- Per-drive settings overrides
-- ─────────────────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS drive_settings_overrides (
    drive_id               TEXT PRIMARY KEY REFERENCES drives(id) ON DELETE CASCADE,
    temp_warning_c         REAL,       -- NULL = use global default
    temp_critical_c        REAL,
    alerts_enabled         INTEGER,    -- NULL = use global default
    curve_picker_enabled   INTEGER,    -- NULL = use global default
    updated_at             TEXT NOT NULL DEFAULT (datetime('now'))
);

-- ─────────────────────────────────────────────────────────────────────────────
-- Global drive monitoring settings (stored in existing settings table)
-- Seed defaults so reads always succeed.
-- ─────────────────────────────────────────────────────────────────────────────
INSERT OR IGNORE INTO settings (key, value, updated_at) VALUES
    ('drive_monitoring_enabled',       '1',          datetime('now')),
    ('drive_native_provider_enabled',  '1',          datetime('now')),
    ('drive_smartctl_provider_enabled','1',          datetime('now')),
    ('drive_smartctl_path',            'smartctl',   datetime('now')),
    ('drive_fast_poll_seconds',        '15',         datetime('now')),
    ('drive_health_poll_seconds',      '300',        datetime('now')),
    ('drive_rescan_poll_seconds',      '900',        datetime('now')),
    ('drive_hdd_temp_warning_c',       '45',         datetime('now')),
    ('drive_hdd_temp_critical_c',      '50',         datetime('now')),
    ('drive_ssd_temp_warning_c',       '55',         datetime('now')),
    ('drive_ssd_temp_critical_c',      '65',         datetime('now')),
    ('drive_nvme_temp_warning_c',      '65',         datetime('now')),
    ('drive_nvme_temp_critical_c',     '75',         datetime('now')),
    ('drive_wear_warning_percent_used','80',         datetime('now')),
    ('drive_wear_critical_percent_used','90',        datetime('now'));
