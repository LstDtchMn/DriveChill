-- 006_api_keys_webhooks.sql
-- API keys for machine-to-machine auth and webhook configuration/delivery logs.

CREATE TABLE IF NOT EXISTS api_keys (
    id          TEXT PRIMARY KEY,
    name        TEXT NOT NULL,
    key_prefix  TEXT NOT NULL,
    key_hash    TEXT NOT NULL UNIQUE,
    scopes_json TEXT NOT NULL DEFAULT '[]',
    created_at  TEXT NOT NULL DEFAULT (datetime('now')),
    revoked_at  TEXT,
    last_used_at TEXT
);

CREATE INDEX IF NOT EXISTS idx_api_keys_created ON api_keys(created_at);
CREATE INDEX IF NOT EXISTS idx_api_keys_revoked ON api_keys(revoked_at);

CREATE TABLE IF NOT EXISTS webhooks (
    id                    INTEGER PRIMARY KEY CHECK (id = 1),
    enabled               INTEGER NOT NULL DEFAULT 0,
    target_url            TEXT NOT NULL DEFAULT '',
    signing_secret        TEXT,
    timeout_seconds       REAL NOT NULL DEFAULT 3.0,
    max_retries           INTEGER NOT NULL DEFAULT 2,
    retry_backoff_seconds REAL NOT NULL DEFAULT 1.0,
    updated_at            TEXT NOT NULL DEFAULT (datetime('now'))
);

INSERT OR IGNORE INTO webhooks (
    id, enabled, target_url, signing_secret,
    timeout_seconds, max_retries, retry_backoff_seconds, updated_at
)
VALUES (1, 0, '', NULL, 3.0, 2, 1.0, datetime('now'));

CREATE TABLE IF NOT EXISTS webhook_delivery_log (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    timestamp   TEXT NOT NULL DEFAULT (datetime('now')),
    event_type  TEXT NOT NULL,
    target_url  TEXT NOT NULL,
    payload_json TEXT NOT NULL,
    attempt     INTEGER NOT NULL,
    success     INTEGER NOT NULL,
    http_status INTEGER,
    latency_ms  INTEGER,
    error       TEXT
);

CREATE INDEX IF NOT EXISTS idx_webhook_log_ts ON webhook_delivery_log(timestamp);

ALTER TABLE machines ADD COLUMN api_key_id TEXT;
