CREATE TABLE IF NOT EXISTS report_schedules (
  id TEXT PRIMARY KEY,
  frequency TEXT NOT NULL CHECK(frequency IN ('daily', 'weekly')),
  time_utc TEXT NOT NULL,
  timezone TEXT NOT NULL DEFAULT 'UTC',
  enabled INTEGER NOT NULL DEFAULT 1,
  last_sent_at TEXT,
  created_at TEXT NOT NULL DEFAULT (datetime('now'))
);
