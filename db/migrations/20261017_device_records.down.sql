-- Rollback of 20261017_device_records.up.sql
-- Dropping `status` collapses every tombstone back into a plain row;
-- callers treat rows without a status column as active (the Go model
-- only reads these columns when the migration has been applied).

DROP INDEX IF EXISTS idx_activations_license_active;

ALTER TABLE activations
    DROP COLUMN IF EXISTS scheme,
    DROP COLUMN IF EXISTS status,
    DROP COLUMN IF EXISTS deactivated_at,
    DROP COLUMN IF EXISTS deactivated_reason;
