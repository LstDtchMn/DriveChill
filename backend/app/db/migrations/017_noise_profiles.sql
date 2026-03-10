CREATE TABLE IF NOT EXISTS noise_profiles (
  id         TEXT PRIMARY KEY,
  fan_id     TEXT NOT NULL,
  mode       TEXT NOT NULL CHECK(mode IN ('quick', 'precise')),
  data_json  TEXT NOT NULL,
  created_at TEXT NOT NULL DEFAULT (datetime('now')),
  updated_at TEXT NOT NULL DEFAULT (datetime('now'))
);
