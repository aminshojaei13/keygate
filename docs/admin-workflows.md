# Keygate Administrative Workflows & Product Security Guide

This document specifies the administrative workflows, device lifecycle management, license transfer controls, and release integrity architecture in the Keygate management interface (`web/`).

---

## 1. Architectural Principles

All security-critical policies in Keygate are **authoritatively enforced by the server**. The web administration interface acts as an ergonomic, accessible interface for authorized operators, adhering to the following rules:

1. **Server-Enforced Authorization & Quotas**: The client UI never acts as the sole gatekeeper for operations. Activation quotas, plan limits, state transitions, and role boundaries are validated inside database transactions on the server.
2. **Credential Redaction by Default**: License keys and signing secrets are never exposed in bulk list or detail payloads. License keys are masked by default (`KG-••••-<hint>`). Revealing a raw key requires an explicit operator action that generates an audited `key_revealed` event.
3. **Audit Trail Completeness**: Destructive actions, state modifications, credential reveals, device lifecycle transitions, and transfers produce structured audit logs containing actor ID, entity reference, IP address, and payload deltas.
4. **Mandatory Legal Attribution**: Per AGPL v3 Section 7(b), the "Powered by Keygate" attribution notice is preserved across all administrative surfaces.

---

## 2. License Detail & Operational Controls

The License Detail view (`LicenseDetail` in `web/src/pages/admin/licenses.tsx`) provides an interactive hub for managing an issued license.

### 2.1 Overview & Metadata
* **Status Badge**: Visual color-coded status badge (`active`, `trialing`, `past_due`, `expired`, `suspended`, `canceled`, `revoked`).
* **Masked Key & Audited Reveal**: The license key is masked. Clicking the "Reveal" button calls `GET /admin/licenses/:id/key` to retrieve the key into ephemeral memory, recording an audit entry.
* **Plan & Validity**:
  * Displays associated Product and Plan.
  * Start and Expiry timestamps. For non-subscription licenses, expiry is editable via local calendar date selection (`POST /admin/licenses/:id/valid-until`).
  * Perpetual maintenance windows are tracked via `updates_until` (`POST /admin/licenses/:id/updates-until`).
* **Subscription Unlink**: If the license is linked to a billing provider (e.g. Stripe), administrators can unlink it (`POST /admin/licenses/:id/stripe/unlink`) once the provider subscription is canceled.

### 2.2 Lifecycle Actions
* **Resend Email**: Re-queues the delivery of the license credential to the registered customer email (`POST /admin/licenses/:id/resend-email`).
* **Suspend / Reinstate**: Temporarily blocks license checks and activations without deleting slots or customer records (`POST /admin/licenses/:id/suspend` and `POST /admin/licenses/:id/reinstate`).
* **Revoke**: Irrevocably terminates the license. Requires operator confirmation (`POST /admin/licenses/:id/revoke`).
* **Plan Change**: Safely migrates the license between approved plans (`POST /admin/licenses/:id/change-plan`).

---

## 3. Device Management & Activation Lifecycles

Desktop and hybrid software products enforce per-device activation quotas against the plan's `max_activations` allocation.

### 3.1 Device Identity & Scheme Support
Activations record the hardware or platform fingerprint scheme submitted by client SDKs:
* **`kgid-v1`**: Keygate versioned device identity format.
* **`sha256-v1`**: SHA-256 digest of normalized platform attributes (`sha256:<64 hex>`).
* **`uuid`**: Standard RFC 4122 UUID identifier.
* **`legacy`**: Historical records or opaque identifiers without scheme prefixing.

### 3.2 Slot Accounting & Capacity Visualizations
* The header displays real-time slot occupancy: `{active} / {max} slots active`.
* When `active >= max_activations`, the UI surfaces a prominent `Capacity Reached` indicator.
* SaaS products omit device activation tables, relying on the Seat allocation system instead.

### 3.3 Activation States & Administrative Controls
Each activation record exists in one of two lifecycle states:
1. **Active (`status = 'active'`)**:
   * Consumes one activation slot against the license's plan limit.
   * Displays last verified timestamp, client IP, platform label, and identifier scheme.
   * **Action: Deactivate** (`POST /admin/activations/:id/deactivate`):
     * Soft-deactivates the device into a tombstone.
     * Sets `status = 'deactivated'`, `deactivated_at = now()`, and `deactivated_reason = 'admin'`.
     * Frees the slot immediately for other devices.
     * Logs an audit record and dispatches a `license.deactivated` webhook.
2. **Deactivated (`status = 'deactivated'`)**:
   * Does **not** consume an activation slot. Retained for historical audit and support troubleshooting.
   * Displays the timestamp and reason for deactivation (e.g., `admin`, `transfer`, `portal`).
   * **Action: Reactivate** (`POST /admin/activations/:id/reactivate`):
     * Requires operator confirmation.
     * Server verifies whether `active_activations < max_activations`.
     * If the plan quota is full, the server returns `409 Conflict` with `ACTIVATION_LIMIT`, and the UI displays a clean notification to the operator without desynchronizing state.
3. **Action: Delete / Revoke** (`DELETE /admin/activations/:id`):
   * Soft-deactivates / removes the activation row when an operator needs to permanently clean up obsolete device records. Requires confirmation.

---

## 4. License Ownership Transfer Workflow

License ownership transfer allows reassigning a license from one customer email address to another (e.g., corporate restructuring, employee turnover, or support reassignments).

### 4.1 Transfer Procedure
1. The operator clicks **Transfer** in the license action bar.
2. The `TransferLicenseDialog` opens, displaying the current owner's email address.
3. **Form Inputs**:
   * **New Owner Email**: The target recipient email (syntax-validated). Identical email addresses are blocked client-side and rejected with `409 IDENTICAL_EMAIL` on the server.
   * **Release Active Activations** (Checkbox, default: `true`):
     * When checked: Atomically soft-deactivates all currently active device activations on the license, setting `deactivated_reason = 'transfer'`. This ensures the new owner starts with fresh activation slots without inherited device locks.
     * When unchecked: Retains existing device activations under the new owner.
   * **Reason** (Optional, max 256 characters): Documented rationale recorded in the audit trail.
4. **Execution & Concurrency**:
   * The client submits `POST /admin/licenses/:id/transfer` with `{ new_email, release_activations, reason }`.
   * The server executes the transfer inside an atomic transaction guarded by `SELECT ... FOR UPDATE` row locking.
   * Audit log is written with `action = 'transferred'`, `changes = { from_email, to_email, activations_released, reason }`.
   * A `license.transferred` webhook is dispatched to external subscribers.

---

## 5. Audit Trail & Compliance Inspection

The **Audit** tab in `LicenseDetail` provides chronological traceability for compliance and security forensics:
* **Action Types**: Tracks `transferred`, `deactivated`, `reactivated`, `created`, `key_revealed`, `valid_until_changed`, `updates_until_changed`, `suspended`, `reinstated`, and `revoked`.
* **Actor Attribution**: Identifies whether the action was taken by an administrative user (`admin`), the customer (`portal`), an external client (`client`), or an automated cron task (`system`).
* **Payload Deltas**: Renders formatted inspection summaries of affected fields (e.g., email migration paths, reason strings, device identifiers).
* **Network Context**: Records the client or operator IP address associated with the audit entry.

---

## 6. Release Integrity & Verification Architecture

Keygate’s release management view (`web/src/pages/admin/releases.tsx`) coordinates the distribution of software release bundles and their cryptographic manifests.

### 6.1 Artifact Metadata & Digest Presentation
For each platform binary inside a release bundle:
* **Platform Identifier**: Target operating system and architecture (e.g. `darwin-arm64`, `windows-x64`, `linux-x64`).
* **Digest & Size**: Formatted file size and SHA-256 digest preview (`sha256:<12 hex>...`).
* **Signing Key Identifier (`kid`)**: Displays the active public key ID (`kid:<8 hex>...`) used to sign the artifact manifest.
* **Signature Status**: Indicates whether Ed25519 digital signatures have been finalized for the artifact.

### 6.2 Scope of Integrity Guarantees
The UI explicitly clarifies the boundaries of integrity protection:
> *Artifact integrity is verified via SHA-256 digests and Ed25519 signatures upon publication. Runtime verification is performed locally by installed client SDKs.*

* **Release Distribution Integrity**: Guaranteed by the server during upload and publication. Artifacts without valid SHA-256 checksums or digital signatures cannot be served to update feeds.
* **Client Runtime Integrity**: The server does **not** claim real-time knowledge of client memory or binary execution state without an active telemetry reporting mechanism. Client SDKs verify local installed file integrity and digital signatures independently before loading protected code.
