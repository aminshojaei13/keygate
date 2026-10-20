-- ============================================================
-- Migration: device / activation record metadata
--
-- Adds to `activations`:
--   scheme              how the client identifier is shaped
--                       ('legacy' for rows that predate this
--                       migration; new rows record 'uuid',
--                       'sha256-v1' or 'opaque' — detection only,
--                       no format enforcement, so existing SDK
--                       clients keep working).
--   status              'active' consumes an activation slot;
--                       'deactivated' is a tombstone that keeps
--                       the device visible for inspection without
--                       counting against the plan limit.
--   deactivated_at      when the slot was released (NULL while active).
--   deactivated_reason  who/what released it: 'client' (SDK
--                       /license/deactivate), 'portal' (customer
--                       self-service), 'admin'. The revocation and
--                       expiry cleanup paths keep their existing
--                       hard-delete behavior and set nothing here.
--
-- Data minimization: no new personal information is collected — no
-- hardware serials, no new identifiers beyond the scheme *label*
-- derived from the identifier the client already sends.
--
-- Slot accounting: activation counting and "already activated"
-- lookups filter on status='active'. A partial index keeps those
-- queries as cheap as the old unfiltered ones.
-- ============================================================

ALTER TABLE activations
    ADD COLUMN IF NOT EXISTS scheme TEXT NOT NULL DEFAULT 'legacy',
    ADD COLUMN IF NOT EXISTS status TEXT NOT NULL DEFAULT 'active'
        CHECK (status IN ('active', 'deactivated')),
    ADD COLUMN IF NOT EXISTS deactivated_at TIMESTAMPTZ,
    ADD COLUMN IF NOT EXISTS deactivated_reason TEXT NOT NULL DEFAULT '';

CREATE INDEX IF NOT EXISTS idx_activations_license_active
    ON activations (license_id)
    WHERE status = 'active';
