-- Baseline migration: stamps the monolithic CREATE TABLE IF NOT EXISTS schema
-- as version 0.  No DDL — all tables are created by DbService.EnsureInitialisedAsync.
-- This file exists solely so the migration runner records version 0 as applied.
SELECT 1;
