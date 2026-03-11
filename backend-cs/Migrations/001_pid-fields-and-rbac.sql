-- PID controller fields on temperature_targets + RBAC role column on users.
-- Migrated from the hardcoded ALTER TABLE loop in DbService.
--
-- For fresh databases these columns already exist in the CREATE TABLE DDL,
-- so these statements may throw "duplicate column" — the migration runner
-- treats per-statement SqliteException on ALTER TABLE ADD COLUMN as safe.
--
-- For upgraded databases this is the first time these columns appear.

ALTER TABLE temperature_targets ADD COLUMN pid_mode INTEGER NOT NULL DEFAULT 0;
ALTER TABLE temperature_targets ADD COLUMN pid_kp   REAL    NOT NULL DEFAULT 5.0;
ALTER TABLE temperature_targets ADD COLUMN pid_ki   REAL    NOT NULL DEFAULT 0.05;
ALTER TABLE temperature_targets ADD COLUMN pid_kd   REAL    NOT NULL DEFAULT 1.0;
ALTER TABLE users ADD COLUMN role TEXT NOT NULL DEFAULT 'admin';
