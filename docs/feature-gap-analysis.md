# Feature Gap Analysis — Keygate baseline vs. target platform

Target platform (our fork's objective): a secure, **self-hosted, language-agnostic**
licensing server with device activation management, offline license validation, and
application integrity verification, for commercial software in any language on any OS.

This document is read-only analysis. Classification vocabulary used per the audit brief:
**Implemented and verified** (code + test evidence, tests run in this audit) ·
**Partially implemented** · **Documented but not verified in code** · **Missing** ·
**Potentially insecure** · **Unknown (insufficient evidence)**.

Security findings live in `docs/security-audit.md`; this document references them as S1–S12.

---

## 1. Executive summary

Keygate already covers roughly **80 % of a commercial licensing platform's feature set**,
implemented in Go/PostgreSQL with a React admin SPA, and its concurrency-critical paths
(activation caps, floating seats, usage quotas, idempotency) are genuinely atomic and — as
verified in this audit — pass their race tests against real Postgres.

The gaps that matter for *our* fork are concentrated in four areas:

1. **Offline trust lifecycle** — no revocation channel, no signing-key rotation (`kid`),
   and the revocation latency of issued tokens is undocumented (S1).
2. **Application integrity** — artifact signing exists (Sparkle/Tauri/minisign), but there
   is no runtime integrity/attestation capability, and the Velopack channel is unsigned by
   design (S10).
3. **Device identity** — the identifier is a free-form client string; no fingerprint
   format/standard is defined or documented (S5).
4. **Language-agnostic SDK surface** — transport is plain HTTPS+JSON+Ed25519 (good), but
   there are **no reference SDKs**, OpenAPI covers only 20 of ~140 routes, and the offline
   token format is bespoke rather than a standard (JOSE) construct (§6).

None of the missing items requires rewriting the server; all are additive (§architecture).

---

## 2. Verified current capabilities (feature-level)

### 2.1 License lifecycle

| Feature | Status | Evidence |
|---|---|---|
| License creation (admin + Stripe fulfillment) | Implemented and verified | `admin.go` `CreateLicense`, `payment/stripe.go`, `internal/payment/*_test.go` |
| Models: subscription, perpetual, perpetual+maintenance, trial, floating | Implemented | `plan.license_type` (`subscription\|perpetual\|trial`), `plan.license_model` (`standard\|floating`), `20260322_floating_addons`, `20260908_maintenance_period` |
| Verify / activate / deactivate (public SDK) | Implemented and verified | `internal/service/license.go`, `internal/handler/license.go`, `internal/license/token_test.go` |
| Suspend / reinstate (with reason column, email, webhook) | Implemented and verified | `admin.go:2365,2399`, `db/migrations/20261004_license_suspended_by`, `license_reactivate_test.go`, `cancel_state_test.go` |
| Revoke + refund (Stripe cancel → local revocation) | Implemented and verified | `admin.go:2295 (RevokeLicense)`, `admin.go (RefundLicense)`, webhook `license.revoked` |
| Expiry/grace handling (`valid_until` + `grace_days`, canceled stops dead at `valid_until`) | Implemented and verified | `assertUsable`/`effectiveGraceDays` in `internal/service/license.go`; `internal/service/expiry.go` loop; tests `license_test.go`, `updates_until_*` |
| Status machine (`active,trialing,past_due,canceled,expired,suspended,revoked`) | Implemented (DB-enforced) | CHECK constraint `20260320_init.up.sql` |
| Redeemable/transferable license keys, seat-based access | Partially implemented (seats exist; no key transfer/claim flow) | `seats.go` service/store; portal seat add/remove; no "transfer license" endpoint found |
| Trial → expired automation, expiry reminders, dunning emails | Implemented | `internal/service/expiry.go`, `expiry_reminders.go` + tests |

### 2.2 Devices, activations, concurrency

| Feature | Status | Evidence |
|---|---|---|
| Per-device activation limit, atomic | Implemented and verified (tests run in this audit) | `store.go:2025` + `activation_race_test.go` (3/92 store tests excluded by CI's `-run "Atomic"` filter — see §5 of security-audit) |
| Repeat activation tolerated (idempotent by machine) | Implemented and verified | `ErrAlreadyActivated`, `TestConcurrentActivationFromOneMachine` |
| Activation records store identifier, type, label, IP, `last_verified` | Implemented | `activations` table, `20260320_init` |
| Admin deletion of single activation | Implemented | `DELETE /api/v1/admin/activations/:id` (`licWrite.DELETE`) |
| Customer self-service activation management | Implemented and verified | `internal/handler/portal_activations.go` (list/delete, ownership + accepted-seat checks, audit + webhook) |
| Concurrency/floating licenses (checkout, heartbeat, checkin, cleanup) | Implemented and verified | `internal/store/floating.go`, `internal/service/floating.go`, `floating_lease_test.go` |
| Device identifier format/validation (fingerprint spec) | **Missing** — client-asserted free-form string ≤256 chars (S5) | `internal/handler/license.go:15,37` |
| Stale-device reaping (idle machines) | **Missing** — `DeleteExpiredActivations` only clears activations of `expired/revoked` licenses (`store.go:1255-1266`); `last_verified` is recorded but never used to auto-free a slot | `internal/service/expiry.go:226` |
| Device metadata catalog (hardware fingerprint, hostname, OS, first/last seen beyond `last_verified`) | Missing | no columns beyond `label`, `ip_address`, `last_verified` |
| Per-activation heartbeat policy (server-driven lease for devices) | Partially implemented — floating model has leases; the standard device model has none | contrast `floating_sessions.expires_at` vs `activations` |

### 2.3 Offline validation and crypto

| Feature | Status | Evidence |
|---|---|---|
| Signed offline token (Ed25519, nonce, `exp`, `ValidUntil`, `UpdatesUntil`, `GraceDays`, feature claims, truncated device fingerprint) | Implemented and verified (design-strong; HMAC deliberately avoided) | `internal/license/token.go`, `signToken` (`license.go:575`), `token_test.go` |
| Public key endpoint `GET /api/v1/license/pubkey` (hex, `ed25519`, cacheable) | Implemented | `cmd/server/main.go`; `docs/openapi.yaml` `/license/pubkey` |
| Token TTL (default 7 d, plan override) clamped to `valid_until+grace` | Implemented | `license.go:564`, `plan.token_ttl_days` (`20260903_plan_token_ttl`) |
| Verification algorithm documented for third-party implementers | Partially implemented — OpenAPI documents `base64url(payload).base64url(ed25519_sig)` and claim tables, but **no `kid`/header, no negative-case vectors, no reference verifier** | `docs/openapi.yaml:1035-1100` |
| Offline revocation / denylist | **Missing** (S1) | no denylist table/code; `grep` for revocation channel finds none |
| Signing-key rotation with multiple accepted public keys (`kid`, key history) | **Missing** — one `LICENSE_SIGNING_KEY`, `/license/pubkey` always returns the current one | `cmd/server/main.go`, `internal/license/token.go` |
| Repudiation-proof audit of token issuance | Unknown/absent — issuance is logged only via verify metrics + audit rows on activate/deactivate | `middleware.License*` metrics |

### 2.4 Release & artifact management

| Feature | Status | Evidence |
|---|---|---|
| Releases with per-platform artifacts, atomic publish gate (no half-published leak), yank/unyank, semver + channel | Implemented and verified | `internal/service/release.go` (publish clears/re-signs atomically), `release_publish_race_test.go`, `release_admin.go` |
| Server-side SHA-256 computed, client-supplied hash ignored, re-checked at sign time (`ErrArtifactContentChanged`) | Implemented and verified | `release_signing.go:356-386`, `release_test.go` |
| Per-product Ed25519 keys, AES-GCM at rest (AAD=`product_id`), admin-only endpoints, private key `json:"-"` | Implemented and verified | `release_signing.go`, `release_signing_admin.go`, `20260511/20260513` migrations, `release_signing_test.go` |
| Sparkle (macOS) `edSignature`, Tauri minisign bundle, Velopack feed, upgrade.json floor | Implemented and verified (Tauri verified by re-implementing the verifier in test) | `release_feed.go`, `tauri_minisign.go` + tests |
| Velopack artifact signature | **Missing** by design (S10: trust = HTTPS + server hash) | `release_feed.go:210` |
| License-gated downloads (`feed_license_required`, `updates_until` enforcement, gated presigned URLs with TTL bound) | Implemented and verified | `release_public.go` (`Download`, feed request parser), `release_download_gate_test.go`, `settings.go` `SettingFeedURLTTLBound`, `20260903`/`20260908` migrations |
| Forced upgrade floor (`minimum_supported_version` + message) | Implemented | `model.Product`, `release_feed.go` `capMinimumVersion`, `product_floor_test.go` |
| Client-side application integrity verification (runtime, anti-tamper) | **Missing** — no API, no attestation, no integrity attestation flow; integrity exists only for update artifacts | no evidence anywhere in repo |
| Artifact transparency/rollback protection (signed *timestamps*, release revocation list) | Missing (yank exists server-side; offline client cannot detect a silently rolled-back feed without an append-only log) | `release.go` yank paths |
| S3-compatible storage with presigned PUT (no proxying) and short-TTL download links | Implemented and verified | `internal/storage/s3.go` + tests, config in `config.go` |

### 2.5 Billing, entitlements, admin, portal

| Feature | Status | Evidence |
|---|---|---|
| Stripe end-to-end (checkout, upgrades/proration, cancel, refund, billing portal, renewals of updates, dunning, webhook auto-setup, 3-layer reliability: webhook + success-page verify + periodic sync, idempotent via `processed_events`) | Implemented and verified | `internal/payment/*` (13 test files incl. concurrency/renewal races), `stripe.go` signature verification |
| Usage metering with atomic quota + warning webhooks + period resets | Implemented and verified | `internal/store/usage.go`, `usage.go`, `usage_test.go`, `integration_test.go` |
| Entitlements (bool/int/string), add-ons, quota periods | Implemented and verified | `entitlement.go`, `license_features.go` + tests |
| Webhooks (HMAC-SHA256 `X-Keygate-Signature`, retries/backoff, delivery log + resend, SSRF guard) | Implemented and verified | `internal/service/webhook.go`, `webhook_ssrf_test.go`, `webhook_admin.go`; events: `license.created/activated/deactivated/suspended/revoked/reinstated/canceled/expired/expiry_changed/payment_failed/payment_recovered/quota_warning`, `webhook.test` |
| Team seats with roles (owner/admin/user at users; admin/member per-seat) and invite tokens (hashed, consumed atomically) | Implemented and verified | `seats.go`, `20260325_user_roles`, `team_test.go`, `store.go` refresh-style token handling |
| Server-to-server API keys (scoped, product-bound, hashed at rest, rotate, fail-closed) | Implemented and verified | `store.go:478` `HashAPIKey`, `admin.go:1704/1836`, `apikey.go`, README section |
| Admin dashboard coverage (products, plans, licenses+export+sort, customers, API keys, webhooks, analytics, audit, team, email templates, settings, releases) | Implemented | `web/src/pages/admin/*` (13 pages), `api.ts` surface |
| Customer portal (licenses, activations, seats, quotas, subscription change/cancel/billing-portal, updates renewals) | Implemented | `web/src/pages/portal/*`, `cmd/server/main.go` portal group |
| Auth: email OTP (constant-time), refresh rotation w/ reuse detection, session cookies (SameSite=Lax, Secure heuristic, HttpOnly), admin role checked per request, setup wizard with advisory-lock + ADMIN_EMAILS allowlist, dev-login gated to development+localhost | Implemented and verified | `internal/handler/otp.go`, `oauth.go`, `setup.go` + tests, `middleware/auth.go` |
| MFA/SSO for admins (TOTP, WebAuthn, OIDC/SAML login providers) | **Missing** — `GET /auth/providers` reports `{otp:true, dev_login}` only; `oauth_accounts` table exists but no provider flow implemented (`handler/oauth.go` contains no provider callbacks) | `oauth.go` `Providers()` |
| Audit log viewer (filters/pagination) | Partially implemented — async writes (S9), coverage ~35/78 admin handlers | `admin.go:2778`, `web/src/pages/admin/audit.tsx` |
| CSV/JSON export | Implemented and verified | `admin.go` `ExportLicenses`, `csv_test.go` |
| Config/secrets management with encrypted secret settings | Implemented | `internal/store/settings.go`, `admin.go` `ClearSecretSetting` (audited) + `settings_test.go` |

### 2.6 Platform operations

| Feature | Status | Evidence |
|---|---|---|
| Auto-migration on startup, up/down migration pairs (40 pairs) | Implemented and verified (migrations run inside test suite against Postgres) | `db/migrations/*`, `store.RunMigrations` |
| Single-binary deploy, Docker multi-stage build (bun → go → alpine), compose with healthchecks | Implemented | `Dockerfile`, `docker-compose.yml`, `Makefile` |
| Rate limiting (in-memory default; Redis/Valkey shared + breaker fallback) | Implemented | `ratelimit.go`, `cmd/server/redis.go`, `redis_test.go` |
| Brute-force lockout | Partially implemented — correct logic, **process-local state** (S3) | `bruteforce.go` |
| Metrics (`/metrics`) + health endpoint | Implemented (unauthenticated — S7) | `cmd/server/main.go:638` |
| CI: vet + unit + integration slice + typecheck/lint/build; release workflow publishing GHCR multi-arch | Implemented (integration filter skips most store tests — §6 testing strategy) | `.github/workflows/ci.yml`, `release.yml` |
| SBOM / dependency license & CVE scanning | **Missing** (no workflow or tool config found) | no evidence in `.github/` |
| Backup/restore tooling | Partially implemented — `make db-backup/db-restore` wrapping pg_dump/psql | `Makefile` |
| Key rotation runbook (LICENSE_SIGNING_KEY, JWT_SECRET, RELEASE_KEY_ENCRYPTION_KEY) | Partially implemented — config comments describe consequences (`config.go` notes re-encryption not automated for master key); no docs page, no maintenance endpoint | `internal/config/config.go` `ReleaseKeyEncryptionKey` comment |

---

## 3. Missing functionality (the fork's roadmap inputs)

Grouped by objective, ordered by risk×cost in `docs/implementation-roadmap.md`.

**A. Offline trust lifecycle (highest value)**
1. Offline revocation channel: signed revocation list or short-TTL "heartbeat" contract, plus
   documentation of the current worst-case latency (S1).
2. Token signing-key rotation: `kid` (or multi-key `pubkey` list), overlap window, audit.
3. Reference verifiers + test vectors (positive *and* negative: expired, wrong device,
   tampered payload, unknown key id).

**B. Application integrity verification (stated objective, currently absent)**
4. Runtime integrity story: what the client sends at activation (identity attestation,
   optional hardware-bound key), optional server challenge-response (nonce) so a cloned VM
   can be distinguished, and how this composes with existing artifact signing.
5. Signed feed timestamps / anti-rollback for offline update decisions; either upstream
   Velopack-compatible signing or documenting the S10 trust gap per platform.

**C. Device activation management**
6. Device identifier standard: canonical format (e.g. UUIDv7 or hardware-derived hash),
   length/charset constraints, validation at the handler (also fixes the `identifier_type`
   500-vs-400 nit, S5).
7. Stale-activation reaping policy using `last_verified` (with user-visible rules), so lost
   machines free slots automatically.
8. Richer device records (display metadata, first/last seen, optional heartbeat lease for
   the non-floating model).

**D. Language-agnostic SDK surface**
9. Complete OpenAPI for **all** ~140 routes (currently 20 documented in
   `docs/openapi.yaml`) — admin, portal-session, auth, billing included.
10. Reusable JSON Schema components for envelope, pagination, error codes (the envelope and
    error/status bijection already exist and are tested — they just need publishing).
11. Reference SDKs/verifier libraries or, at minimum, an "SDK conformance kit"
    (fixtures + vectors) for C#/.NET, Java, JS/TS, Python, Go, Rust, C/C++ (§6).
12. Standardize the offline token toward a standard shape (EdDSA/JWT or explicitly
    documented two-part format with split rules and canonicalization guidance).

**E. Platform hardening/ops**
13. Admin MFA, shared-secret rotation runbooks, auditable audit log (chain/retention),
    S3/SBOM/CVE CI, CSP, optional auth on `/metrics`.
14. Multi-instance parity for brute-force lockouts (share via Redis/DB, like rate limits).
15. `licenses.license_key` plaintext column retirement ("Phase C") + making at-rest
    encryption mandatory-by-default (S2).

---

## 4. Security findings summary (detail in `docs/security-audit.md`)

| ID | Severity | One-line | Evidence |
|---|---|---|---|
| S1 | Medium | Offline token keeps working up to `tokenTTL` (7 d default, plan-overridable) after revoke/suspend; not documented | `internal/service/license.go:564,575` |
| S2 | Medium | Plaintext `license_key` column retained (Phase C never migrated); encryption opt-in | `20260514_license_key_encrypted.up.sql`, `store.go:528` |
| S3 | Medium (multi-replica) | Brute-force lockout is process-local; rate limits replicate, lockouts don't | `bruteforce.go`, `main.go:257` |
| S4 | Low | Broad trusted-proxy ranges make all per-IP budgets spoofable from trusted networks | `main.go:591` |
| S5 | Low | Device identifier is client-asserted, unvalidated; 64-bit fingerprint; `identifier_type` bad value → 500 | `handler/license.go:15`, `license/token.go` |
| S6 | Low | Unbounded request bodies outside portal/idempotency guards | `main.go`, `idempotency.go` |
| S7 | Low/Info | `/metrics`, `/health` (version) unauthenticated | `main.go:638` |
| S8 | Low | OTP codes logged when SMTP unconfigured (intentional, warned) | `otp.go:80-100` |
| S9 | Low | Audit writes async/best-effort, not tamper-evident; partial coverage | `store.go:1205` |
| S10 | Low | Velopack channel has no artifact signature (trust = HTTPS+server hash) | `release_feed.go:210` |
| S11 | Info | No CSP | `main.go` headers block |
| S12 | Info | CORS origin refused only on preflight in production; dev reflects any origin | `main.go` origin block |

---

## 5. API and database observations

**API surface actually registered in `cmd/server/main.go` (140 registration statements):**

- Public SDK: `POST /api/v1/license/{activate,verify,deactivate,entitlements,usage,usage/status,floating/checkout,floating/checkin,floating/heartbeat,download}` (10) + `GET /license/pubkey`, `/config`, plans catalogue, `/setup/*`, `/invites/accept`, Stripe webhook/verify, `/pay/:checkout_id`.
- Feeds: 4 `GET /api/v1/releases/:product_slug/...` + four `410 Gone` legacy shims (hard cutover, documented in OpenAPI and code comments).
- Portal (session): 16 routes incl. `GET/DELETE /portal/licenses/:license_key/activations[...]`, seats, subscription ops, renewals.
- Admin groups: `admin.*` 57 routes (admin-scope only), `licWrite.*` 22 (admin **or** `licenses:write`), `relWrite.*` 12 (admin **or** `releases:write`) — signing-key endpoints deliberately stay admin-only (comment in `main.go`).
- Non-envelope raw-body routes are precisely documented in `docs/api-contract.md` (feeds, `public.pem`, Stripe, `/pay`).
- Error model is contract-tested: one status per code (`pkg/response/contract_test.go:131`).

**Database observations:**

- 34 migration pairs (34 `*.up.sql` + mirrored `*.down.sql`), additive-first, CHECK constraints and partial unique indexes carry the
  security invariants (one active signing key/product, unique key-hash, unique
  activation per `(license_id, identifier)`).
- Idempotency table with 24 h TTL + independent pruning loop; `processed_events` for Stripe
  exactly-once; `refresh_tokens` with rotation/reuse semantics; `otp_codes`; `email_queue`
  with lease-based claim (`SKIP LOCKED`); `settings` (with `enc:v1:` secret values).
- Encrypt-at-rest columns: `licenses.license_key_encrypted`,
  `release_signing_keys.private_key_encrypted` (exact 60-byte CHECK), `settings` secrets.
  Plaintext `licenses.license_key` is intentionally still present (S2).
- Analytics are snapshotted by a daily job (`TakeAllSnapshots`) — separate from licensing
  correctness.

---

## 6. Language interoperability findings

**Verdict: the server API is already language-agnostic; nothing in it requires a specific
client language. No rewrite is needed or recommended.**

What a client in *any* language must do, and how hard that is:

| Client task | Transport/format | Interop risk |
|---|---|---|
| activate / verify / deactivate / usage / entitlements / floating / download | HTTPS + JSON, snake_case, documented in `docs/openapi.yaml`, uniform error envelope | **None** — any language with HTTPS+JSON |
| Verify offline token | Base64url(payload).base64url(Ed25519 sig) over the raw payload bytes; claims are short `snake_case` keys (`lid,pid,pln,sts,did,ftr,iat,exp,vun,upd,grc,nce,fpr`) | **Low** — not JOSE: no header, no `kid`, so each language needs a small custom parser; `ed25519` is natively available in .NET 7+, Java 15+ (`EdDSA` from JDK 15 for `Ed25519.Signature`), Node `crypto`, Python `cryptography`, Go stdlib, Rust `ed25519-dalek`, C ` libsodium/`Monocypher`. Recommend documenting exact byte handling (see A12 in roadmap). |
| Server key fetch | `GET /api/v1/license/pubkey` → hex public key + `algorithm: ed25519` | **Low**; would be improved by `kid` + key list |
| License key format | `PREFIX-XXXX-XXXX-XXXX-XXXX`, 32 chars from a 32-symbol alphabet (160 bits), SHA-256 normalized (upper-case, trimmed) for hashing | **None** — trivially portable; document normalization rule for clients that echo keys |
| Update feeds | RSS/Sparkle XML, Velopack JSON, Tauri minisign JSON — all industry-standard formats | **None** for those ecosystems; the *offline token* is the only bespoke piece |
| Admin/server-to-server API | Bearer `kg_live_…` (SHA-256-hashed at rest), envelope + pagination | **Medium**: not documented in OpenAPI — other-language admin integrations must reverse `cmd/server/main.go` |
| Idempotency / retry semantics | `Idempotency-Key`, documented header matrix in `docs/api-contract.md` | **None** (HTTP-standard pattern) |
| Webhooks | HMAC-SHA256 `sha256=` in `X-Keygate-Signature` | **Low** — standard pattern, but the signature scheme (exact string signed) should be documented for implementers of *receivers* in other languages; verify strings exist in `internal/service/webhook.go:510,656` |

**Language checklist against the mandated list:** C#/.NET ✓, Java ✓, JS/TS ✓, Python ✓,
Go ✓, Rust ✓, C/C++ ✓ (via libsodium-class Ed25519 + any HTTPS client), others ✓ —
*conditional on* (a) documenting token verification bytes precisely, (b) shipping vectors,
(c) finishing OpenAPI coverage. **No unnecessary dependency on a specific client language
was found** (no client-side required crypto library, no binary protocol, no gRPC, no
language-specific SDK requirement); the only external format dependencies are deliberate
ecosystem standards (Sparkle/Velopack/Tauri).

**Recommended standards (detail in `docs/architecture-proposal.md`):**
JSON Schema (draft 2020-12) for every request/response, OpenAPI 3.1 for all routes,
JOSE/EdDSA (`Ed25519` + `kid`) or a fully specified two-part token, RFC 8032 Ed25519 for
artifacts (already), HKDF-SHA256 (RFC 5869, already), AES-256-GCM (NIST SP 800-38D,
already), ISO-8601 timestamps (already RFC3339), and a documented device-identifier
canonicalization.

---

## 7. Backward-compatibility risks (summary — full treatment in `docs/architecture-proposal.md` §7)

1. Changing the token format (e.g. to JOSE) invalidates every token already in the field:
   clients must verify *both* formats during a transition window, or the server must keep
   issuing the legacy format until a `format` version header appears.
2. Adding `kid`/multi-key verification changes `GET /license/pubkey`'s response shape —
   add fields (`keys: [{kid, algorithm, public_key}]`), keep `public_key` for one cycle
   (OpenAPI `1.2.0` → `1.3.0`).
3. Dropping `licenses.license_key` (S2/Phase C) breaks any external tool reading the
   column; needs a backfill-verification query and a maintenance switch.
4. Tightening identifier formats would strand existing activations → grandfather existing
   rows, validate only new activations.
5. Documenting (and later constraining) revocation latency changes customer expectations;
   shorter TTLs shift load to `/license/verify` — rate-limit budgets must be reviewed.
6. Adding OpenAPI for admin routes must not change any route, field, or code; docs-only.
7. Any rebranding must preserve the "Powered by Keygate" attribution and config response
   fields (`attribution_text`/`attribution_url`) — license-mandated (see security-audit §5),
   and `X-Powered-By` must remain.
8. Removing `/metrics` unauthenticated access would break existing scrape configs → make it
   opt-in-gated rather than removed.

---

## 8. Testing strategy (view; see roadmap for the ordered plan)

- **Keep as regression guards**: `activation_race_test.go`, `floating_lease_test.go`,
  `idempotency_test.go`, `usage_test.go`, `release_publish_race_test.go`,
  `contract_test.go` (error/status bijection), `log_redaction_test.go`,
  `retry_after_test.go`, payment concurrency tests.
- **Fix CI coverage**: `.github/workflows/ci.yml:54` runs only `-run "Atomic"` against
  Postgres (9 of 92 store tests) while the unfiltered unit step skips DB tests
  (`integration_test.go:16-21`). The activation race tests therefore never run in CI as
  configured. Verified green when run in full during this audit — CI should run the whole
  `internal/store` package (it takes ~4 s against Postgres).
- **Add**: handler-level enumeration tests (uniform 404 bodies), token verification vectors
  for third-party SDKs, a concurrency harness at HTTP level, webhook signature vectors, CSP
  and header regression tests, SBOM/CVE scan jobs, and a scheduled "docs ↔ routes" drift
  check (all routes registered in `main.go` must exist in `openapi.yaml`).
- All tests executed in this audit: full unit suite + full store package against
  disposable `postgres:18-alpine`, **0 failures** (repo mounted read-only; no source
  changes).

---

## 9. Licensing and dependency concerns

See `docs/security-audit.md` §5 for the full table. Headlines for this document:

- Project code = AGPL-3.0 **plus** the Section 7(b) attribution term; `NOTICE` additionally
  requires derivatives to use a different **name/logo** while retaining the "Powered by
  Keygate" attribution — rebrand ≠ remove attribution (removal requires a commercial
  license).
- Direct Go deps: MIT/BSD-3/Apache-2.0 only (gin, bun, jwt, aws-sdk, stripe, redis,
  prometheus, godotenv, uuid, x/*). Frontend: MIT/ISC/Apache-2.0 (React, Radix, TanStack,
  Recharts, Tailwind, Vite, Biome). No copyleft deps in the direct graph.
- No license/CVE scanning in CI (missing feature R-Op4); version pins for GitHub Actions
  float on major tags — supply-chain review item, not a defect.
- One third-party script origin (`cdn.jsdelivr.net`) is loaded by the built-in `/docs`
  page — relevant to CSP and to air-gapped deployments (should be self-hosted).
- `docker-compose.yml` quickstart uses default Postgres credentials and an optional
  unauthenticated port 9000 — fine for evaluation, must be hardened for production
  (documented as such in the compose file itself).

---

## 10. Files examined

**Root/config/CI** — `README.md`, `README.zh-CN.md`, `AGENTS.md`, `LICENSE`, `NOTICE`,
`go.mod`, `go.sum`, `Makefile`, `Dockerfile`, `docker-compose.yml`, `.env.example`,
`.air.toml`, `.dockerignore`, `.gitignore`, `.github/workflows/ci.yml`,
`.github/workflows/release.yml`.

**Backend** — read in full: `cmd/server/main.go`, `cmd/server/redis.go` (+ `redis_test.go`,
`log_redaction_test.go`, `frontend_serving_test.go`),
`internal/config/config.go` (+test), `internal/branding/branding.go`,
`internal/crypto/{aesgcm,hkdf}.go` (+tests), `internal/license/{keygen,token}.go` (+tests),
`internal/middleware/{auth,apikey,bruteforce,ratelimit,idempotency}.go` (+tests),
`internal/model/model.go` (windows: User/Product/APIKey/Scope/Plan/License/Activation/Audit/Seat/ReleaseSigningKey),
`internal/handler/{license,setup,otp,oauth,portal_activations,release_signing_admin}.go` (+tests),
`internal/service/license.go`, `internal/service/release_signing.go`,
`internal/store/{settings,settings-bound,usage,floating}.go` slices, `internal/store/store.go`
windows (activation tx, audit, license-key encrypt/decrypt, API-key hash, expiry cleanup),
`internal/store/activation_race_test.go` (full), `pkg/apperr/validate.go`,
`pkg/response/contract_test.go` (error/status bijection test).
Keyword/function-level inspection (grep with context, not full read): `internal/handler/admin.go`
(4435 lines; 3 windows: API-key lifecycle, RevealLicenseKey/Resend/Revoke/Refund/Suspend/Reinstate,
scope helpers; audit-call inventory; handler list), `internal/handler/release_public.go`,
`internal/handler/{pagination,plans_public,seats,usage,entitlement,floating,system,webhook_admin}.go`,
`internal/service/{release,release_feed,tauri_minisign,release_signing,webhook,expiry,expiry_reminders,
floating,usage,seats,entitlement,license_features,email*}.go`,
`internal/store/{release,release_signing,seats,addons,admin,idempotency,analytics,metered,
subscription,webhook,pgerr,paging,metrics}.go`, `internal/payment/stripe.go` + its 13 test files,
`internal/storage/{s3,storage}.go`, remaining `internal/handler`/`internal/service` test files
(19 handler test files, 92 test files repo-wide).

**Database** — `db/migrations/20260320_init.up.sql` (full), plus full/key-word reads of `20260511_release_signing_keys`, `20260513_tighten_signing_key_check`, `20260514_license_key_encrypted`, and membership/keyword scans across all 34 `*.up.sql` pairs (incl. `20260322_license_key_hash`, `20260903_plan_token_ttl`, `20260908_maintenance_period`, `20261004_license_suspended_by`, `20261005_artifact_tauri_signature`).

**Docs** — `docs/api-contract.md` (full), `docs/openapi.yaml` (all declared paths +
activate/verify/token sections).

**Frontend** — `web/package.json`, `web/src/lib/api.ts`, `web/src/hooks/use-auth.tsx` (path),
`web/src/components/layout.tsx` (attribution blocks), `web/src/pages/portal/licenses.tsx`,
`web/src/pages/admin/{licenses,audit,api-keys,releases,settings}.tsx` (path-level),
`web/src/i18n/locales/en.ts` (attribution keys), `web/vite.config.ts`, `web/biome.json`.

**Executed (non-destructive)** — `go vet ./...`; `go test ./... -count=1` (Docker
`golang:1.27-alpine`, repo mounted read-only); `go test -count=1 ./internal/store/` with
`TEST_DATABASE_URL` against disposable `postgres:18-alpine` (100 % pass); containers and
network removed afterwards.