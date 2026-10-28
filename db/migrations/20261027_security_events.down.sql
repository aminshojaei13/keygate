DROP TABLE IF EXISTS security_event_policies;
DROP TABLE IF EXISTS security_events;

ALTER TABLE licenses
    DROP COLUMN IF EXISTS suspended_until,
    DROP COLUMN IF EXISTS activation_blocked,
    DROP COLUMN IF EXISTS under_review_reason,
    DROP COLUMN IF EXISTS under_review_at,
    DROP COLUMN IF EXISTS under_review;

ALTER TABLE activations
    DROP COLUMN IF EXISTS device_key_alg,
    DROP COLUMN IF EXISTS device_public_key;
