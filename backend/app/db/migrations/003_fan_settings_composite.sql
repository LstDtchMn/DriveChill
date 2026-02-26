-- 003_fan_settings_composite.sql
-- Per-fan configuration (minimum speed floor, zero-RPM capability)
-- and composite curve support (multi-sensor input).

CREATE TABLE IF NOT EXISTS fan_settings (
    fan_id           TEXT PRIMARY KEY,
    min_speed_pct    REAL NOT NULL DEFAULT 0,
    zero_rpm_capable INTEGER NOT NULL DEFAULT 0,
    updated_at       TEXT NOT NULL DEFAULT (datetime('now'))
);

-- Composite curve support: optional multi-sensor temperature input.
-- When non-empty JSON array, curve evaluates against MAX of listed sensors.
ALTER TABLE fan_curves ADD COLUMN sensor_ids_json TEXT NOT NULL DEFAULT '[]';
