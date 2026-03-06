-- Add optional action payload to alert rules for profile switching on trigger.
-- Stored as JSON: {"type": "switch_profile", "profile_id": "...", "revert_after_clear": true}
ALTER TABLE alert_rules ADD COLUMN action_json TEXT;
