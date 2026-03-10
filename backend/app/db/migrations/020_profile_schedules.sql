CREATE TABLE IF NOT EXISTS profile_schedules (
  id TEXT PRIMARY KEY,
  profile_id TEXT NOT NULL,
  start_time TEXT NOT NULL,       -- HH:MM (24h local time)
  end_time TEXT NOT NULL,         -- HH:MM (24h local time)
  days_of_week TEXT NOT NULL,     -- comma-separated: "0,1,2,3,4,5,6" (0=Monday)
  timezone TEXT NOT NULL DEFAULT 'UTC',
  enabled INTEGER NOT NULL DEFAULT 1,
  created_at TEXT NOT NULL DEFAULT (datetime('now')),
  FOREIGN KEY (profile_id) REFERENCES profiles(id) ON DELETE CASCADE
);
