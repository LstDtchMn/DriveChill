-- 013_api_key_role.sql
-- Add role and created_by columns to api_keys table so that viewer-created
-- keys cannot be used for write operations (GAP-1: API key role ceiling).
ALTER TABLE api_keys ADD COLUMN created_by TEXT;
ALTER TABLE api_keys ADD COLUMN role       TEXT NOT NULL DEFAULT 'admin';
