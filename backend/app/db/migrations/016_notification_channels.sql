-- Notification channels: ntfy.sh, Discord webhook, Slack webhook, generic webhook
CREATE TABLE IF NOT EXISTS notification_channels (
    id           TEXT PRIMARY KEY,
    type         TEXT NOT NULL,  -- 'ntfy', 'discord', 'slack', 'generic_webhook'
    name         TEXT NOT NULL DEFAULT '',
    enabled      INTEGER NOT NULL DEFAULT 1,
    config_json  TEXT NOT NULL DEFAULT '{}',
    created_at   TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at   TEXT NOT NULL DEFAULT (datetime('now'))
);
