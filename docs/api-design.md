# API Design — licensing protocol v1 (implementation-ready, reuse-first)

Status: design only — **no production code changed in this phase.**
Sources of truth: the completed audit (`docs/security-audit.md`), the baseline inventory
(`docs/feature-gap-analysis.md`), the proposal (`docs/architecture-proposal.md`), and the
lived behavior of this repository as read during those phases (every route, error code and
limit below was taken from code, not invented).

Machine-readable companion: **`docs/openapi-proposal.yaml`** (additive draft; merging it
into the served `docs/openapi.yaml` is an implementation task). Example payloads:
**`docs/examples/*.json`**.

---

## 1. Design principles

1. **Server is authoritative.** Online status, activation limits, device records and all
   administrative mutations exist only server-side; clients receive *signed assertions*,
   never shared secrets.
2. **Reuse before invention.** 9 logical operations map onto 6 existing routes + 3
   proposed additions; nothing existing is renamed or reshaped.
3. **Language-agnostic by construction.** HTTPS + JSON, RFC 3339 UTC timestamps,
   `snake_case`, standard status codes, Ed25519 (RFC 8032) signatures, SHA-256 (FIPS 180-4)
   digests, base64url (RFC 4648 §5). The only crypto a client needs offline is Ed25519
   verification + JSON — available in every mandated language.
4. **Contract-tested invariants.** One HTTP status per error code (enforced by
   `pkg/response/contract_test.go`), uniform `{success,data|error}` envelope, pagination
   semantics as documented in `docs/api-contract.md`.

### 1.1 Reuse matrix (logical operation → route → action)

| # | Logical operation | Route | Action |
|---|---|---|---|
| 1 | License activation | `POST /api/v1/license/activate` | **reuse as-is** (document exactly) |
| 2 | License status verification | `POST /api/v1/license/verify` | **reuse as-is** |
| 3 | Device activation inspection | `GET /api/v1/portal/licenses/:license_key/activations` (owner/seat) and `GET /api/v1/admin/licenses/:id` (includes `activations[]`) | **reuse**; optional SDK variant `POST /api/v1/license/activations` = **PROPOSED** (§4) |
| 4 | Device deactivation | `POST /api/v1/license/deactivate` (by identifier), `DELETE /api/v1/portal/licenses/:license_key/activations/:activation_id`, `DELETE /api/v1/admin/activations/:id` | **reuse as-is** |
| 5 | Authorized license transfer | — | **PROPOSED**: admin `POST /api/v1/admin/licenses/:id/transfer` (§6) and portal two-step `POST /api/v1/portal/licenses/:license_key/transfer` (§6) |
| 6 | License revocation | `POST /api/v1/admin/licenses/:id/revoke` (+ `/suspend`, `/reinstate`, `/refund`) | **reuse as-is** |
| 7 | Offline license issuance | `POST /license/activate` / `POST /license/verify` → signed token (existing) | **reuse**; offline *revocation provisioning* `GET /api/v1/license/revocations` = **PROPOSED** (§7) |
| 8 | Public key discovery | `GET /api/v1/license/pubkey` | **reuse**, additive extension `keys[]`/`kid` = **PROPOSED** (§8) |
| 9 | Release & integrity manifest | `GET /api/v1/releases/:product_slug/{feed.xml,feed.json,upgrade.json,velopack/*}` + `POST /api/v1/license/download` | **reuse as-is**; public artifact-key discovery `GET /api/v1/releases/:product_slug/signing-key` = **PROPOSED optional** (§9) |

Everything marked PROPOSED lives in `docs/openapi-proposal.yaml` and is additive: existing
clients are unaffected whether or not it ships.

---

## 2. Protocol conventions

| Concern | Rule | Origin |
|---|---|---|
| Transport | HTTPS; HTTP refused for sessions in production (warned at boot) | existing `ValidateSecurityDefaults` |
| Base path | `/api/v1` (URL versioning; see `docs/protocol-versioning.md`) | existing `main.go` |
| Envelope | `{"success":true,"data":…}` or `{"success":false,"error":{"code","message","details?"}}`; `204` is the only bodiless success | `docs/api-contract.md`, `pkg/response` |
| Raw-body exceptions | feeds (`feed.xml`, `feed.json`, `upgrade.json`, velopack paths), `public.pem`, `POST /webhook/stripe`, `GET /pay/:checkout_id` | `docs/api-contract.md` |
| Timestamps | RFC 3339, always UTC, field suffix `_at`; chart buckets are `YYYY-MM-DD` | `docs/api-contract.md` |
| Collections | `snake_case`, arrays `[]` never `null`, paginated lists default `limit=50` cap `200` (clamped, not refused) | `docs/api-contract.md` |
| Error codes | `SCREAMING_SNAKE_CASE`; **one status per code, always** | `contract_test.go:131` |
| Encoding | base64url **unpadded** (RFC 4648 §5) for tokens; base64 **standard** for artifact signatures; hex for keys/digests where the existing API already says so | `internal/license/token.go`, `release_signing.go` |
| Idempotency | `Idempotency-Key` on writes where duplication is harmful; keys live 24 h; errors documented below | `internal/middleware/idempotency.go` |
| Discovery | `GET /api/v1/version`, `GET /api/v1/config`, `GET /api/v1/license/pubkey` (no auth) | existing |

### 2.1 Authentication classes

| Class | Credential | Used by | Notes |
|---|---|---|---|
| **None** | — | `/license/*`, feeds, `/config`, `/version`, `/license/pubkey` | the `license_key` in the body *is* the credential; no API keys in binaries (deliberate, `main.go` comment block) |
| **License bearer** | `license_key` (+ optional signed `token` on feeds) | `/license/*` | tenant-scoped by `licenses.product_id`; product-capability gated (`desktop`/`saas`/`hybrid`) |
| **Customer session** | `session` JWT cookie (HS256, 24 h) + `refresh_token` (30 d, rotating) | `/portal/*` | role checked per request from DB |
| **Admin or API key** | session cookie/JWT **or** `Authorization: Bearer kg_live_…` | `/admin/*` | scopes: `admin` wildcard, `licenses:write`, `releases:write`; empty scopes = no access (fail-closed); product-bound keys get `403 PRODUCT_SCOPE_MISMATCH` |

### 2.2 Rate limits and brute-force (as implemented today)

| Surface | Limit | Key | Source |
|---|---|---|---|
| `/license/*` | `max(RATE_LIMIT_API×2, 120)` per minute | client IP within scope `license` | `main.go` `licRateLimit` |
| Feeds | `max(RATE_LIMIT_API×4, 240)` per minute | IP, scope `feed` | `main.go` |
| `/admin/*` | `RATE_LIMIT_ADMIN` (default 120)/min | IP, scope `admin` | `main.go` |
| `/auth` group | `RATE_LIMIT_AUTH` (60)/min; `otp_send` 30/h | IP, scoped buckets | `main.go` |
| Brute force on `/license/*` | 5 failures → 30 s, exponential to 30 min cap | IP (`BF_MAX_FAILS`, `BF_LOCKOUT_SECONDS`) → `429 LOCKED_OUT` + `Retry-After` | `middleware/bruteforce.go` |
| `/portal/*` | **none today** (session-auth only) | — | observed in `main.go`; DESIGN NOTE: add a scoped limiter in the same style (proposed, non-breaking) |

Shared across instances only when `REDIS_URL` is set (rate limits); brute-force lockout is
process-local today (audit finding S3 — roadmap R-12).

---

## 3. Operation 1 — License activation ✅ existing

- **Route:** `POST /api/v1/license/activate`
- **Auth:** none at the route layer (license key = credential). Wrapped by
  `LicenseBruteForceGuard`, `RateLimitByIPScoped("license",…)`, `Idempotency`.
- **Request schema** (`application/json`, required fields marked):

| field | type | req | constraints |
|---|---|---|---|
| `license_key` | string | ✓ | format `PREFIX-XXXX-XXXX-XXXX-XXXX`; lookup is normalized UPPER+trim (`HashKey`) |
| `identifier` | string | ✓ | trim, ≤ 256 chars (today unvalidated format); **PROPOSED**: canonical profile per §3.1 |
| `identifier_type` | `"device"`\|`"user"` | – | default `device`; DB CHECK rejects other values (today surfaces as 500 — nit to fix by validating to 400) |
| `label` | string | – | display name, today unbounded → cap 256 (proposed) |

- **Response 200:** `data = {status: "activated"|"already_activated", license_id, token, meta}` —
  `meta = {"server":"Keygate","url":"https://keygate.app"}` (attribution also rides here and
  must be preserved — license term).
- **Errors (as implemented):**

| code | status | when |
|---|---|---|
| `BAD_REQUEST` | 400 | missing/bad JSON; `identifier` > 256 |
| `INVALID_IDEMPOTENCY_KEY` | 400 | malformed `Idempotency-Key` (1–256 chars, no controls) |
| `BODY_TOO_LARGE` | 413 | idempotency layer sees > 256 KiB |
| `IDEMPOTENCY_KEY_CONFLICT` | 422 | same key, different body |
| `IDEMPOTENCY_IN_FLIGHT` | 409 (+`Retry-After: 1`) | concurrent replay |
| `LICENSE_NOT_FOUND` | 404 | unknown key or product mismatch |
| `FEATURE_NOT_AVAILABLE` | 404 | product type lacks activations (e.g. `saas`) |
| `LICENSE_EXPIRED` / `LICENSE_CANCELED` / `LICENSE_SUSPENDED` / `LICENSE_REVOKED` / `LICENSE_INVALID` | 403 | **deliberately specific on activate** (unlike verify): the buyer needs to learn why |
| `ACTIVATION_LIMIT` | 409, `details: {max, current}` | plan cap reached |
| `LOCKED_OUT` | 429, `details.retry_after` + `Retry-After` | IP brute-force lockout |
| `RATE_LIMITED` | 429 | per-IP bucket |
| `INTERNAL_ERROR` | 500 | opaque, logged server-side |

- **Idempotency:** header supported (24 h replay, `Idempotent-Replayed: true`); without the
  header the operation is *naturally idempotent* per `(license_id, identifier)` —
  `already_activated` + fresh token, no slot consumed (verified by race tests).
- **Rate limiting:** §2.2 license bucket + brute-force guard; failures of **unknown keys
  also count** toward lockout (`recordFailure`).
- **Audit:** `audit_logs {entity:"license", action:"activated", actor_type:"sdk",
  ip, changes:{identifier,type,label}}` — implemented (async write, audit finding S9).
  Webhook `license.activated` dispatched; Prometheus `LicenseActivations{result}`.
- **Compatibility:** stable since v1; additive-only evolution allowed (new optional fields).
  **PROPOSED** additions (all optional): echo `expires_at` of the returned token for client
  convenience (client can decode it — no wire change required).

### 3.1 PROPOSED identifier profile (applies to new activations only)

Accepted forms: UUIDv4/UUIDv7 (`^[0-9a-fA-F-]{36}$`, lowercase canonical) or
`sha256:<64 hex>` (hardware-derived digest). Legacy rows grandfathered. Reject with
`BAD_REQUEST` at the handler so unknown `identifier_type` and malformed ids never reach the
DB (fixes the 500 nit). Binding claim in the token (`fpr`) keeps being
`hex(SHA256(identifier + ":" + product_id)[:8])`.

---

## 4. Operation 2 — License status verification ✅ existing · Operation 3 — Device inspection ↩ reuse

### 4.1 Verify

- **Route:** `POST /api/v1/license/verify` · **Auth:** none (license key) · guards: brute
  force + license bucket · **no** idempotency layer (read-only, naturally idempotent).
- **Request:** `{license_key: string ✓, identifier: string ✓}` (identifier trim ≤256).
- **Response 200 `data`:** `{status, plan_id, plan_name, valid_until?, updates_until?,
  features: {}, token, grace_days, meta, external_customer_id?, external_workspace_id?}`
  — exact schema already published (`docs/openapi.yaml` `VerifyResponse`, incl. the token
  claim table `lid,pid,pln,sts,did,ftr,iat,exp,vun,upd,grc,nce,fpr`).
- **Errors:** the anti-enumeration contract — **every license-knowable failure collapses to
  `404 LICENSE_NOT_FOUND`** (unknown key, other product, suspended/revoked/expired, not
  activated for this identifier). Exceptions: `400 BAD_REQUEST`, `404
  FEATURE_NOT_AVAILABLE` (product gate), `429 LOCKED_OUT`/`RATE_LIMITED`, `500
  INTERNAL_ERROR`. **Verify never answers 403** — an SDK must not branch on license state
  here; it learns state from the token claims or the portal.
- **Audit:** **none** (by design: per-call rows would swamp the log) — observability is via
  `LicenseVerifications{result}` metrics only. Documented explicitly so operators don't
  expect audit rows for online checks.
- **Compatibility:** stable; `updates_until`/external ids are optional fields added within
  v1 (already shipped).

### 4.2 Device activation inspection

| Variant | Route | Auth | Response | Audit |
|---|---|---|---|---|
| **Customer self-service (existing)** | `GET /api/v1/portal/licenses/:license_key/activations` | customer session; owner (`license.email`, case-insensitive) **or accepted seat** (pending invites confer nothing); otherwise uniform `404 NOT_FOUND` (no existence oracle) | `{activations: [{id, identifier, identifier_type, label?, ip_address?, created_at, last_verified}], max}` | list = none; **delete side is audited** |
| **Admin (existing)** | `GET /api/v1/admin/licenses/:id` | session/admin-scope or `licenses:write`; product-bound key enforced (`PRODUCT_SCOPE_MISMATCH`, 403) | license object incl. `activations[]` | read: none |
| **SDK variant (PROPOSED, optional)** | `POST /api/v1/license/activations` | license key = credential; guards: brute force + license bucket | `{activations:[{identifier,label?,last_verified,created_at}], max, current}` — **identifier list only, no IPs** (privacy: IPs stay portal/admin-only) | none (metrics only) |

PROPOSED rationale/decision record: a key holder can already *deactivate* any identifier it
can name, so listing identifiers to the *same* credential does not enlarge lateral power —
it enlarges *disclosure* (device names travel with the key). Default recommendation:
ship the portal/admin variants only; add the SDK variant behind an explicit per-product
flag if customers ask. Anti-enumeration: unknown key → `404 LICENSE_NOT_FOUND` (same
collapse rule), rate-limited as §2.2.

- **Errors (portal variant, as implemented):** `401 UNAUTHORIZED`, `404 NOT_FOUND`
  (unknown key *or* not yours — identical body), `500 INTERNAL_ERROR`.
- **Idempotency:** read-only, header ignored.
- **Compatibility:** both inspection routes predate this design and are unchanged; the SDK
  variant is optional/proposed-only, and *not shipping it changes nothing* for existing
  clients (absent route ⇒ standard envelope `404 NOT_FOUND`).

---

## 5. Operation 4 — Device deactivation ✅ existing (3 routes, one logical op)

- **SDK route:** `POST /api/v1/license/deactivate` `{license_key ✓, identifier ✓}` —
  license key = credential, brute-force + bucket guards.
  - **Response 200:** `{status:"deactivated"}`.
  - **Errors:** `400 BAD_REQUEST`, `404 LICENSE_NOT_FOUND` for *both* "no such license"
    and "no activation for this identifier" (oracle suppression, `license.go:383`),
    `404 FEATURE_NOT_AVAILABLE`, `429 LOCKED_OUT`/`RATE_LIMITED`, `500`.
  - **Idempotency:** natural (second call → `404`; documented so SDKs treat it as
    "already free"), header ignored.
  - **Audit:** `{action:"deactivated", actor_type:"sdk", changes:{identifier}}` +
    webhook `license.deactivated`.
- **Portal route (existing):** `DELETE /api/v1/portal/licenses/:license_key/activations/:activation_id`
  — session; ownership check; verifies the activation belongs to *that* license; race-safe
  (`sql.ErrNoRows` → success); audit `{entity:"activation", action:"deleted",
  actor_type:"portal"}` + webhook.
- **Admin route (existing):** `DELETE /api/v1/admin/activations/:id` — admin/`licenses:write`.
- **Compatibility:** all three predate this design; no change proposed. Cross-cutting
  proposal: always pair deactivation with the audit/webhook path if new variants are added.

---

## 6. Operation 5 — Authorized license transfer ⚠️ PROPOSED (no route exists)

No transfer route exists today (the audit classified license transfer as missing). Two
variants, both additive, both **reuse existing trust machinery** (mailbox proof via OTP,
seat-invite token hashing, admin scopes):

### 6.1 Admin / server-to-server transfer (primary)

- **Route:** `POST /api/v1/admin/licenses/:id/transfer` — **PROPOSED**
- **Auth:** admin session **or** API key with scope `admin` or `licenses:write`; product
  binding enforced via the existing `checkLicenseScope` helper (403
  `PRODUCT_SCOPE_MISMATCH`).
- **Request:** `{new_email: string ✓ (validated `apperr.ValidateEmail`),
  release_activations: bool (default true), reason?: string (≤256)}`.
- **Response 200:** `{status:"transferred", license_id, email, activations_released:N}`.
- **Errors (proposed codes marked ✱):** `400 BAD_REQUEST`, `400 INVALID_INPUT` ✱ (bad
  email), `401 UNAUTHORIZED`, `403 FORBIDDEN`/`INSUFFICIENT_SCOPE`, `403
  PRODUCT_SCOPE_MISMATCH`, `404 NOT_FOUND`, `409 IDENTICAL_EMAIL` ✱, `429 RATE_LIMITED`,
  `500 INTERNAL_ERROR`.
- **Idempotency:** n/a for admin routes today; `release_activations:false` makes repeats
  harmless (email already equals `new_email` → success or `409 IDENTICAL_EMAIL`):
  documented idempotence-by-state, recommended wrapper `Idempotency-Key` in a later cycle.
- **Rate limit:** admin bucket (120/min).
- **Audit (required):** two rows — `{entity:"license", action:"email_transferred",
  actor}` plus, when slots are released, per the deletion semantics used by revoke;
  webhook **proposed** event `license.transferred` `{license_id, from_hash?, to_email}` —
  *new event name ⇒ additive for webhook consumers (unknown events must be ignored, which
  the retry contract already implies; document it)*.
- **Privacy:** existing revoke/refund webhooks already carry `email`; transfer follows the
  same rule but the recommended payload contains `license_id` only + email, never the key.

### 6.2 Customer-initiated transfer (two-step, mailbox-proofed)

- **Route:** `POST /api/v1/portal/licenses/:license_key/transfer` — **PROPOSED**
- **Auth:** customer session, **owner only** (accepted seats excluded — a transfer changes
  who owns the license; matches the seat-mutation precedent where only owner/accepted-admin
  mutate, tightened further here).
- **Step 1:** `{new_email}` → server stores a **hashed confirmation token** (same pattern as
  `seats.invite_token_hash`), mails a confirmation link to **both** mailboxes (source
  cancel-link, target accept-link), responds `{status:"pending", expires_at}`.
- **Step 2 (existing-shaped companion route):**
  `POST /api/v1/transfer/confirm` `{token ✓}` → completes transfer, consumes token,
  mints a session for the new owner exactly the way `POST /invites/accept` does today
  (token = mailbox proof). Rate-limited like invites (60/min/IP).
- **Errors:** `400 BAD_REQUEST`, `401 UNAUTHORIZED`, `404 NOT_FOUND` (includes *invalid or
  already-used token* — one code, no oracle: mirrors `409`/`404` invite behavior; final
  choice `404 NOT_FOUND` to match `SEAT_INVITE_INVALID` semantics), `429 RATE_LIMITED`,
  `409 TRANSFER_PENDING` ✱.
- **Audit:** `{action:"transfer_requested"}` + `{action:"transferred", actor_type:"portal"}`,
  both with `ip`; webhook `license.transferred`.
- **Compatibility:** entirely new; no existing client affected. Existing portals keep
  showing the key to whoever owns the license afterwards (ownership is by email — reusing
  `ListLicensesByEmail`).

> Decision for implementation: ship **6.1 first** (covers the support-ticket use case with
> existing authz and audit plumbing); 6.2 only if self-serve transfers are a product need.

---

## 7. Operations 6 & 7 — Revocation and offline license issuance

### 7.1 Online revocation (existing, authoritative)

- **Route:** `POST /api/v1/admin/licenses/:id/revoke` (+ `/suspend`, `/reinstate`,
  `/refund`) — admin session or key scopes `admin`/`licenses:write`, product-bound keys
  enforced.
- **Behavior as implemented:** deletes the license's activations, sets
  `status='revoked'`, marks the linked subscription revoked (`store.go:417`
  `RevokeLicense`), audits `{action:"revoked", actor}` (+ `suspended`, `reinstated`,
  `refunded`), dispatches `license.revoked` webhook; suspend additionally emails the
  customer.
- **Errors:** `401 UNAUTHORIZED`, `403 FORBIDDEN`/`INSUFFICIENT_SCOPE`, `403
  PRODUCT_SCOPE_MISMATCH`, `404 NOT_FOUND` ("license not found" — used for both unknown id
  and failed update), `409 LICENSE_NOT_SUSPENDABLE` (suspend only), `409 EXPIRY_PASSED`
  (reinstate), `429 RATE_LIMITED`, `500 INTERNAL_ERROR`.
- **Idempotency:** none (admin group is not wrapped); revoke is **idempotent by state**
  (repeat = same UPDATE → same 200), suspend is guarded by state (`LICENSE_NOT_SUSPENDABLE`).
- **Effect on SDKs:** next `/license/verify` → `404 LICENSE_NOT_FOUND` (collapsed); stored
  offline tokens remain valid until `exp` (≤ `token_ttl_days`, default 7 d, clamped to
  `valid_until+grace`) — **audit finding S1**. This is why 7.2 exists.

### 7.2 Offline license issuance & offline revocation provisioning

**Issuance already exists**: `/license/activate` and `/license/verify` each return the
Ed25519-signed token; that *is* the offline license grant. Its normative rules (now
codified):

- Claims (JWT-free, two-part format): `lid,pid,pln,sts,did,ftr,iat,exp,vun,upd,grc,nce,fpr`
  (see `docs/openapi.yaml` `VerifyResponse` claim table and `docs/cryptographic-design.md` §3).
- `exp = min(now + token_ttl_days(7 default), vun + grc days)`; `vun`/`upd` inside the
  signed payload; `grc = effectiveGraceDays` (0 for canceled/revoked/suspended/…).
- Verification: fetch key once (`GET /license/pubkey`, cacheable 1 h), verify offline
  forever within `exp`; re-verify to refresh.
- **No separate "offline issuance" endpoint is proposed** — a second issuance path would
  duplicate state; extend policy via plan knobs instead.

**PROPOSED:** offline revocation provisioning, closing S1:

- **Route:** `GET /api/v1/license/revocations` — **PROPOSED**
- **Auth:** none (contents are digests, not identifiers of persons) or `license_key`-scoped
  if operators prefer per-product filtering: `?product=<product_id>` optional.
- **Response 200 `data` (proposed schema):**

```json
{
  "rev": 17,
  "generated_at": "2026-10-17T09:00:00Z",
  "algorithm": "ed25519",
  "entries": [
    {"key_hash": "9f86d081884c7d65a93ef7bf47c0ef2b7aef6e3c8a5e6a8a6b7a1d8e9f0a1b2c",
     "reason": "revoked", "revoked_at": "2026-10-16T12:00:00Z"}
  ],
  "token": "<base64url(payload).base64url(signature)> as in §7.3"
}
```

  - `key_hash = hex(SHA256(normalize(license_key)))` — the client can compute its own from
    the key it holds; the list itself discloses no identities (keys are 160-bit).
  - Style reuses the existing token envelope: `token` signs canonical JSON of
    `{rev,generated_at,entries}` — same `license.Sign` construction, no new format
    (see `docs/cryptographic-design.md` §5).
  - Client policy: fetch at least every `exp/2`; on match → treat license as revoked
    offline even if a cached token is still valid; `rev` monotonic protects reordering
    (rollback attack) between fetches.
- **Errors:** `429 RATE_LIMITED`, `500 INTERNAL_ERROR`. Feed-cache headers
  `Cache-Control: max-age≤300`.
- **Idempotency:** read-only. **Audit:** reads not audited; generation needs none (derives
  from `licenses.status`).
- **Compatibility:** brand-new route; absent ⇒ clients just behave as today.

---

## 8. Operation 8 — Public verification-key discovery ✅ existing (+ additive extension)

- **Route (existing):** `GET /api/v1/license/pubkey` — no auth; `Cache-Control: public,
  max-age=3600`.
- **Today's response:** `{algorithm:"ed25519", public_key:"<hex, 64 chars>",
  format:"hex"}`.
- **PROPOSED additive extension** (breaking for no one — clients read known keys):

```json
{ "algorithm": "ed25519", "public_key": "<hex>", "format": "hex",
  "keys": [ {"kid": "ksk_2026_10", "algorithm": "ed25519",
             "public_key": "<hex>", "format": "hex", "status": "active"},
            {"kid": "ksk_2026_04", "status": "retired"} ],
  "protocol": {"version": 1, "token_format": "legacy-ed25519",
               "revocations": true, "challenge": false} }
```

- `kid` enables rotation: tokens/revocations gain an optional `kid` claim; clients verify
  against any listed key (§7 of `docs/architecture-proposal.md`).
- `protocol` block = capability discovery (see `docs/protocol-versioning.md` §3).
- **Errors:** none possible beyond infra (`500`, `503` on misconfig). **Audit:** none.
- **Idempotency/rate:** read-only, license bucket not applied (top-level route), feed-style
  caching applies.

---

## 9. Operation 9 — Release & integrity retrieval ✅ existing (+ optional PROPOSED)

Existing surface (all documented in `docs/openapi.yaml`, reuse as-is):

| Route | Auth | Artifact integrity content |
|---|---|---|
| `GET /api/v1/releases/:product_slug/feed.xml` | none (Sparkle); optional `X-License-Key` header or `license_token` query **when product has `feed_license_required`** — the license key is **refused in the URL** (400 `BAD_REQUEST` with instructions) | per-asset `sparkle:edSignature` = base64 raw 64-byte Ed25519 over the file bytes, lenient shape check (malformed → attribute omitted), SHA-256 length attributes |
| `GET /api/v1/releases/:product_slug/feed.json` | none (Velopack) | `SHA256` per asset; **no signature** (documented trust = HTTPS + server-side hash — audit S10) |
| `GET /api/v1/releases/:product_slug/upgrade.json` | none (Tauri) | minisign-style `signature` bundle + `pubkey` supplied by the app at build time, `pubDate`/`version` + product `minimum_supported_version` floor |
| `GET /api/v1/releases/:product_slug/velopack/*` | none | index/package redirects or feed body |
| `POST /api/v1/license/download` | license key | license-gated presigned URL (short TTL ≤ `STORAGE_DOWNLOAD_TTL`), server-computed digest, `409 ARTIFACT_MODIFIED` if bytes changed post-finalize |

Feed-gating codes as implemented: `401 LICENSE_KEY_REQUIRED` (product requires license on
feeds), `409 FEED_CACHE_DRAINING` / `FEED_GATE`-family codes (carrier `feedGateProblem`
at 409), `404 RELEASE_NOT_FOUND`, `410 FEED_PATH_REMOVED` (legacy paths).

**PROPOSED (optional, P2):** `GET /api/v1/releases/:product_slug/signing-key` — no auth;
returns the product's active artifact-verification key set
(`{keys:[{kid, algorithm:"ed25519", public_key_pem, public_key_b64, tauri_pubkey}]}`)
so rotation need not ship a new binary. Today the equivalent downloads are admin-scoped
(`/admin/products/:id/signing-key/public.pem`), and embedding at build time is the
documented practice — this endpoint is additive and its risk (public keys are public) is
nil; keep feeds requiring it never to change what key *verifies*, only discovery.

- **Idempotency/rate:** all read-only; feed bucket (240/min default). **Audit:** downloads
  are not audited today (presigned URL fetch is out-of-band); accept as documented.
- **Compatibility:** every feed/download route and its response shape is frozen as-is
  (`docs/openapi.yaml` 1.2.0 already specifies them; the legacy no-slug paths stay `410
  FEED_PATH_REMOVED`); the proposed signing-key route is additive — clients that embed
  keys at build time (today’s documented practice) are unaffected.

---

## 10. Example payloads (also in `docs/examples/`)

| File | Content |
|---|---|
| `docs/examples/activate-request.json` | §3 request |
| `docs/examples/activate-response.json` | §3 success (`activated`) |
| `docs/examples/verify-response.json` | §4.1 success incl. full token + claim echo |
| `docs/examples/error-activation-limit.json` | §3 `409 ACTIVATION_LIMIT` with `details` |
| `docs/examples/error-generic-404.json` | §4.1 collapsed `404 LICENSE_NOT_FOUND` |
| `docs/examples/deactivate-response.json` | §5 success |
| `docs/examples/portal-activations-list.json` | §4.2 inspection |
| `docs/examples/admin-revoke-response.json` | §7.1 success |
| `docs/examples/proposed-revocations.json` | §7.2 (PROPOSED) |
| `docs/examples/proposed-pubkey-v2.json` | §8 (PROPOSED) |

A worked request/response transcript for activation:

```http
POST /api/v1/license/activate HTTP/1.1
Host: licensing.example.com
Content-Type: application/json
Idempotency-Key: 2f8f6f2c-6d4b-4a1f-8a77-2c61a0a2b91d

{"license_key":"KG-ABCD2345-EFGH6789-IJKL1234-MNOP5678",
 "identifier":"550e8400-e29b-41d4-a716-446655440000",
 "identifier_type":"device","label":"Workstation"}
```
```json
{"success":true,"data":{"status":"activated","license_id":"lic_01HQ8Z3M5K",
 "token":"<base64url(payload).base64url(sig)>",
 "meta":{"server":"Keygate","url":"https://keygate.app"}}}
```

---

## 11. Compatibility summary (details in `docs/protocol-versioning.md`)

- **Zero breaking changes in this document.** Existing routes, fields, codes and headers
  are reproduced, not restyled; every addition is an *optional field* or a *new path*.
- New error codes only appear on new routes (`INVALID_INPUT`/`IDENTICAL_EMAIL`/
  `TRANSFER_PENDING` etc., marked ✱), so the one-status-per-code invariant never shifts an
  existing mapping.
- Webhook event addition `license.transferred` must be treated as ignorable by receivers.
- The identifier canonical profile (§3.1) validates only *new* activations — grandfathering
  keeps field devices working.
- Review checklist for the implementer: (a) every route in this doc exists in
  `cmd/server/main.go` with the middleware chain described; (b) every code listed resolves
  to the stated status under `contract_test.go`; (c) `docs/openapi.yaml` + proposal merge
  under a minor bump (1.2.0 → 1.3.0).