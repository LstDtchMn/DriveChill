-- Migration 009: raise history_retention_hours default from 24 to 720 (30 days)
-- Only updates installations that still have the old default of 24 hours.
-- Installations where the user has already changed this setting are unaffected.
UPDATE settings
SET    value      = '720',
       updated_at = datetime('now')
WHERE  key   = 'history_retention_hours'
  AND  value = '24';
