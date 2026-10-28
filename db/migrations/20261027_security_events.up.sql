-- ============================================================
-- Migration: security event reporting and reaction policies
--
-- Adds the write side of the protection loop described in
-- docs/security-event-reporting.md:
--
--   * activations.device_public_key — an optional Ed25519 public key
--     generated on the customer's device at activation time. When it
--     is present, /license/security-events refuses unsigned reports,
--     so a leaked license key alone cannot file evidence against a
--     licence it does not run on.
--   * licenses.under_review / activation_blocked — the two
--     reversible states a policy may apply. They are deliberately
--     separate from `status`: a review flag must not break a
--     paying customer's existing install.
--   * licenses.suspended_until — an automatic suspension can be
--     time-boxed. NULL means "until an administrator lifts it".
--   * security_events — one row per accepted client report. The
--     client event id is unique per product so replays are no-ops.
--   * security_event_policies — per-product, per-event-type reaction
--     rules. No row means "use the conservative built-in default",
--     which is resolved in Go (see internal/model/security.go).
--
-- Nothing here stores a license key, a token, a private key or any
-- raw hardware identifier. `details` is a small, server-sanitized
-- JSON object; the device identifier is the same hashed kgid value
-- already stored on the activation row.
-- ============================================================

ALTER TABLE activations
    ADD COLUMN IF NOT EXISTS device_public_key TEXT NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS device_key_alg TEXT NOT NULL DEFAULT '';

ALTER TABLE licenses
    ADD COLUMN IF NOT EXISTS under_review BOOLEAN NOT NULL DEFAULT false,
    ADD COLUMN IF NOT EXISTS under_review_at TIMESTAMPTZ,
    ADD COLUMN IF NOT EXISTS under_review_reason TEXT NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS activation_blocked BOOLEAN NOT NULL DEFAULT false,
    ADD COLUMN IF NOT EXISTS suspended_until TIMESTAMPTZ;

CREATE TABLE IF NOT EXISTS security_events (
    id              TEXT PRIMARY KEY,
    product_id      TEXT NOT NULL REFERENCES products (id) ON DELETE CASCADE,
    license_id      TEXT NOT NULL REFERENCES licenses (id) ON DELETE CASCADE,
    -- Client-generated UUID. Unique per product so a retry or a
    -- replay cannot apply a second policy action.
    event_id        TEXT NOT NULL,
    event_type      TEXT NOT NULL,
    severity        TEXT NOT NULL DEFAULT 'medium',
    device_id       TEXT NOT NULL,
    -- True only when the report carried a valid Ed25519 signature
    -- from a key registered at activation. Automatic suspension and
    -- revocation require verified evidence.
    verified        BOOLEAN NOT NULL DEFAULT false,
    -- occurred_at is client-supplied (bounded by the service) and is
    -- kept apart from received_at, which is the server clock.
    occurred_at     TIMESTAMPTZ NOT NULL,
    received_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    source          TEXT NOT NULL DEFAULT 'sdk',
    ip_address      TEXT NOT NULL DEFAULT '',
    details         JSONB NOT NULL DEFAULT '{}'::jsonb,
    policy_action   TEXT NOT NULL DEFAULT '',
    policy_rule_id  TEXT NOT NULL DEFAULT '',
    status          TEXT NOT NULL DEFAULT 'received'
        CHECK (status IN ('received', 'confirmed', 'dismissed')),
    reviewed_at     TIMESTAMPTZ,
    reviewed_by     TEXT NOT NULL DEFAULT ''
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_security_events_product_event
    ON security_events (product_id, event_id);

CREATE INDEX IF NOT EXISTS idx_security_events_license_recent
    ON security_events (license_id, received_at DESC);

CREATE INDEX IF NOT EXISTS idx_security_events_license_type_verified
    ON security_events (license_id, event_type, received_at DESC)
    WHERE verified;

CREATE TABLE IF NOT EXISTS security_event_policies (
    id             TEXT PRIMARY KEY,
    product_id     TEXT NOT NULL REFERENCES products (id) ON DELETE CASCADE,
    -- '*' is the product-wide fallback. A type-specific row wins.
    event_type     TEXT NOT NULL,
    action         TEXT NOT NULL
        CHECK (action IN ('log', 'flag', 'reject', 'block_activation', 'suspend', 'revoke')),
    -- How many verified events of this type inside window_seconds
    -- trigger `action`. Events below the threshold still mark the
    -- licence for review (flag) unless action is 'log'.
    threshold      INTEGER NOT NULL DEFAULT 1 CHECK (threshold BETWEEN 1 AND 100),
    window_seconds INTEGER NOT NULL DEFAULT 86400 CHECK (window_seconds BETWEEN 60 AND 2592000),
    -- 0 means "until an administrator lifts it". >0 arms a timed
    -- suspension that the licence check treats as expired once past.
    suspend_days   INTEGER NOT NULL DEFAULT 0 CHECK (suspend_days BETWEEN 0 AND 3650),
    enabled        BOOLEAN NOT NULL DEFAULT true,
    created_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (product_id, event_type)
);
