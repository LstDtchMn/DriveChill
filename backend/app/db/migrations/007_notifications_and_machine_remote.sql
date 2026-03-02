-- Migration 007: push notification subscriptions, email notification settings,
-- and machine remote-control tracking fields.

-- ─────────────────────────────────────────────────────────────────────────────
-- Push subscriptions (Web Push / VAPID)
-- ─────────────────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS push_subscriptions (
    id          TEXT PRIMARY KEY,
    endpoint    TEXT NOT NULL UNIQUE,
    p256dh      TEXT NOT NULL,   -- client public key (base64url)
    auth        TEXT NOT NULL,   -- client auth secret (base64url)
    user_agent  TEXT,
    created_at  TEXT NOT NULL DEFAULT (datetime('now')),
    last_used_at TEXT
);

CREATE INDEX IF NOT EXISTS idx_push_subs_created ON push_subscriptions (created_at);

-- ─────────────────────────────────────────────────────────────────────────────
-- Email notification settings (singleton row, id = 1)
-- ─────────────────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS email_notification_settings (
    id              INTEGER PRIMARY KEY CHECK (id = 1),
    enabled         INTEGER NOT NULL DEFAULT 0,
    smtp_host       TEXT NOT NULL DEFAULT '',
    smtp_port       INTEGER NOT NULL DEFAULT 587,
    smtp_username   TEXT NOT NULL DEFAULT '',
    smtp_password   TEXT NOT NULL DEFAULT '',   -- stored encrypted via Fernet
    sender_address  TEXT NOT NULL DEFAULT '',
    recipient_list  TEXT NOT NULL DEFAULT '[]', -- JSON array of addresses
    use_tls         INTEGER NOT NULL DEFAULT 1,  -- STARTTLS
    use_ssl         INTEGER NOT NULL DEFAULT 0,  -- implicit SSL (port 465)
    updated_at      TEXT NOT NULL DEFAULT (datetime('now'))
);

-- Seed the singleton row so UPDATE always works.
INSERT OR IGNORE INTO email_notification_settings (id) VALUES (1);

-- ─────────────────────────────────────────────────────────────────────────────
-- Extend machines table with remote-control tracking
-- ─────────────────────────────────────────────────────────────────────────────
ALTER TABLE machines ADD COLUMN capabilities_json TEXT NOT NULL DEFAULT '[]';
ALTER TABLE machines ADD COLUMN last_command_at   TEXT;
