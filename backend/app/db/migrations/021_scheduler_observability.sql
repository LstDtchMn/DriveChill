-- Scheduler observability fields on report_schedules.
ALTER TABLE report_schedules ADD COLUMN last_error TEXT;
ALTER TABLE report_schedules ADD COLUMN last_attempted_at TEXT;
ALTER TABLE report_schedules ADD COLUMN consecutive_failures INTEGER NOT NULL DEFAULT 0;
