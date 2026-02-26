-- 002_alert_rules_name.sql
-- Add name column to alert_rules for user-friendly rule labels.

ALTER TABLE alert_rules ADD COLUMN name TEXT NOT NULL DEFAULT '';
