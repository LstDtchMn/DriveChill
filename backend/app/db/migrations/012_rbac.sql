-- 012_rbac.sql
-- Add role-based access control: role column on users table.
-- Roles: 'admin' (full read/write) | 'viewer' (read-only).
-- Existing users default to 'admin' to preserve backwards compatibility.

ALTER TABLE users ADD COLUMN role TEXT NOT NULL DEFAULT 'admin';
