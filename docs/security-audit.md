# Security Audit — Keygate baseline (read-only phase)

Status: audit complete, **no code, schema, API, dependency, or Docker changes were made**.
Method: static inspection of the actual implementations in this repository plus non-destructive
verification (test execution against a disposable local Postgres). No production system was
contacted, no destructive tests were run.

Severity scale: **Critical / High / Medium / Low / Info**. A finding is only listed when the
evidence exists in this repository; where evidence is insufficient the item is marked
*Unknown* instead of being asserted.

---

## 1. Executive summary

Keygate is a single-binary Go 1.27 (gin + bun/PostgreSQL) licensing server with a React/Vite
admin SPA and customer portal. The security-relevant core — activation limits, quota
enforcement, floating leases, idempotency, license-key hashing/encryption, offline tokens,
release signing — is **implemented, tested, and in most cases concurrency-tested**.

Verification performed for this audit:

- `go vet ./...` — clean.
- `go test ./... -count=1` (Docker `golang:1.27-alpine`, repo mounted read-only) — **all
  packages pass** (`internal/store` unit-only pass).
- `go test -count=1 ./internal/store/` with `TEST_DATABASE_URL` against a disposable
  `postgres:18-alpine` — **all 92 store tests pass**, including
  `TestConcurrentActivationFromOneMachine`, `TestConcurrentActivationRespectsTheLimit`,
  `TestRepeatActivationReturnsExistingRow`, `TestFloatingCheckOutWithLimit_Atomic`,
  `TestIncrementUsageCounterWithLimit_Atomic`, `TestIdempotencyClaim_Atomic`.

No Critical or High-severity vulnerability was found with sufficient evidence. The most
important findings are **Medium**: offline revocation latency that is inherent to the token
design but not documented for offline clients (S1), and the fact that license keys remain in
a plaintext database column by design until an unaudited "Phase C" migration runs (S2).
Multi-replica deployments weaken brute-force/rate controls (S3, S4). Several Low/Info items
are listed with evidence.

The API is already language-agnostic at the transport level (HTTPS + JSON + Ed25519,
base64url), but offline-token verification and admin/portal API documentation are incomplete
for third-language SDK authors — see `docs/feature-gap-analysis.md` §6 and
`docs/architecture-proposal.md`.

---

## 2. Verified current capabilities (security-relevant)

| Capability | Status | Evidence |
|---|---|---|
| Activation limit enforcement, atomic | **Implemented and verified** | `internal/store/store.go:2025` `ActivateWithinLimit` — `SELECT … FOR UPDATE` on the license row, count, insert, in one tx; race tests `internal/store/activation_race_test.go` pass against Postgres 18 |
| Concurrent requests cannot exceed device limit | **Implemented and verified** | `TestConcurrentActivationRespectsTheLimit` (12 contenders, max 3, expects exactly 3) — ran green in this audit |
| Repeat activation from same identifier does not consume a slot | **Implemented and verified** | `ErrAlreadyActivated` path, `TestConcurrentActivationFromOneMachine`, `TestRepeatActivationReturnsExistingRow` |
| Floating (concurrency) seat cap | **Implemented and verified** | `internal/store/floating.go` `CheckOutFloatingWithLimit` (`FOR UPDATE` on license row, active-session count under lock); `HeartbeatFloating` refuses renewing a lapsed lease (`expires_at > now()` guard) |
| Usage quota enforcement under concurrency | **Implemented and verified** | `internal/store/usage.go` `IncrementUsageCounterWithLimit` — `FOR UPDATE` + overflow guard (`ErrUsageQuantityTooLarge`) |
| Idempotency-Key replay semantics | **Implemented and verified** | `internal/middleware/idempotency.go` (409 in-flight, 422 body mismatch, 413 over 256 KiB, 24 h TTL, 5xx not cached) + `internal/middleware/idempotency_test.go`, `internal/store/idempotency_test.go` |
| License key storage: SHA-256 hash for lookup, optional AES-256-GCM at rest (AAD = license id) | **Implemented** (hash always; encryption only when master key configured) | `internal/license/keygen.go` `HashKey`; `internal/store/store.go:528` `DecryptLicenseKey`, `prepareLicenseForInsert`; `internal/crypto/aesgcm.go`, `internal/crypto/hkdf.go` (`DeriveSubkey`, purpose `license-key`) |
| Offline tokens: Ed25519 over `base64url(payload).base64url(sig)`, nonce, device fingerprint, `exp`/`vun`/`upd` in the signed payload | **Implemented and verified** | `internal/license/token.go` (`Sign`, `Verify`, `Fingerprint`); `internal/service/license.go:575` `signToken`; tests `internal/license/token_test.go` |
| Public key distribution for offline verification | **Implemented** (single key, no `kid`) | `GET /api/v1/license/pubkey` in `cmd/server/main.go`; `[GET /license/pubkey]` in `docs/openapi.yaml` |
| Log redaction of license keys/tokens (query params, path segments, panic recovery, gin log formatter) | **Implemented and verified** | `cmd/server/main.go` (`credentialQueryParams`, `redactCredentialSegments`, `redactPath`, `redactedRecovery`, `redactedLogFormatter`); `cmd/server/log_redaction_test.go` |
| Admin endpoint authorization (session or scoped API key, fail-closed scopes, per-request DB role check) | **Implemented** | `cmd/server/main.go` (three `/admin` groups × `RequireScope`), `internal/middleware/auth.go` (`SessionAuth`, `SessionOrAPIKey`, `RequireScope`), `internal/handler/admin.go:2850` `requireKeyProductScope` (product-bound keys → `403 PRODUCT_SCOPE_MISMATCH`) |
| Enumeration resistance on public SDK endpoints (uniform 404) | **Implemented** | `internal/service/license.go:413-423` `licenseNotFound`, applied in `Verify`/`Deactivate`/`loadLicenseForSDK`; comment blocks explain the oracle reasoning |
| Brute-force lockout with exponential backoff on `/license/*` | **Implemented (per-process)** | `internal/middleware/bruteforce.go`, wired in `cmd/server/main.go:257`; parameterized by `BF_MAX_FAILS` / `BF_LOCKOUT_SECONDS` |
| Rate limiting (scoped per-IP buckets; Redis-backed with in-memory failover) | **Implemented** | `internal/middleware/ratelimit.go` (Lua INCR+EXPIRE, breaker, `RateLimitByIPScoped`), `cmd/server/redis.go` |
| Release signing: per-product Ed25519, private keys AES-GCM at rest with AAD=`product_id`, never returned by any API | **Implemented** | `internal/service/release_signing.go` (`GenerateForProduct`, `SignArtifactWith`, hash-recheck before signing); `PrivateKeyEncrypted []byte json:"-"` at `internal/model/model.go:918`; routes are admin-scope-only (`cmd/server/main.go`) |
| Sparkle `edSignature` + Tauri minisign formats, server-side SHA-256 | **Implemented and verified** | `internal/service/release_feed.go`, `internal/service/tauri_minisign.go`; round-trip tests `tauri_minisign_test.go`, `release_feed_test.go` |
| Webhook signing (HMAC-SHA256 `X-Keygate-Signature`) + SSRF guard (private targets refused unless `WEBHOOK_ALLOW_PRIVATE`) | **Implemented** | `internal/service/webhook.go:510,656`; `internal/service/webhook_ssrf_test.go`; `config.go` `WebhookAllowPrivate` |
| Stripe webhook signature verification (+ livemode guard) | **Implemented** | `internal/payment/stripe.go:293-306` `ConstructEventWithOptions`, previous-secret drain, API-version guard |
| Session lifecycle: JWT HS256 24 h cookie, rotating refresh tokens (hashed at rest), reuse detection revokes all user tokens | **Implemented** | `internal/handler/oauth.go`, `internal/middleware/auth.go`, `internal/store/store.go` refresh-token tx; `internal/store/refresh_grace_test.go` |
| OTP login: constant-time compare, per-email 10-min quota (3), 5 attempts, signup gate | **Implemented** | `internal/handler/otp.go` (`hmac.Equal`, `CountRecentOTPCodes`, `IncrementOTPAttempts`, `signupAllowed`) |
| Startup secret validation (fatal on short JWT secret, malformed Ed25519 seed, partial storage config, missing master key when storage enabled) | **Implemented** | `internal/config/config.go` `ValidateSecurityDefaults`, called from `cmd/server/main.go` |
| Security headers, HSTS under https, `X-Powered-By` attribution | **Implemented** | `cmd/server/main.go` middleware block; `internal/branding/branding.go` |
| Audit trail for security-sensitive operations | **Partially implemented** | `internal/store/store.go:1205` `Audit` (async, best-effort); 35 audit call sites in `internal/handler/admin.go` (78 handler methods) incl. `key_revealed`, `key_emailed`, `revoked`, `suspended`, `reinstated`, `refunded`, API-key `created`/`rotated`, `secret_cleared`; plus `internal/handler/oauth.go`, `otp.go`, `portal_activations.go`, `release_signing_admin.go`, `payment/stripe.go`. Which admin handlers lack audit statements is catalogued in `docs/feature-gap-analysis.md` |

---

## 3. Security findings

### S1 — Offline revocation latency is real, bounded, and **not documented for offline clients**
*Severity: Medium | Category: design/documentation gap (evidence-backed, not an implementation bug)*

A revocation or suspension flips `licenses.status` and takes effect on the next **online**
call (`assertUsable`, `internal/service/license.go`). Tokens already issued remain
cryptographically valid until their own `exp`. `exp` is `min(now + tokenTTL,
valid_until + grace)` where `tokenTTL` defaults to **7 days** (`internal/service/license.go:564`)
and can be lengthened per plan via `plans.token_ttl_days`
(`TokenTTLDays`, `internal/model/model.go:261`; migration `20260903_plan_token_ttl`).
There is no token denylist, no revocation list, no server-side push of revocation to offline
clients — `internal/license/token.go` packages contain no revocation channel.

- Evidence: `internal/service/license.go:285-305` (verify path), `:423` (`licenseNotFound`),
  `:564` (`tokenTTL`), `:575` (`signToken` clamp only to `valid_until+grace`).
- Literature gap: `docs/openapi.yaml` documents that tokens "expire with `token_ttl_days`",
  but neither `README.md`, `docs/api-contract.md`, nor `docs/openapi.yaml` states the
  revocation-latency consequence (a revoked license can keep working offline up to that
  window, shortened only by `valid_until + grace`).
- This is inherent to offline validation; the defect is that operators and clients are not
  told the bound. Fix = document the worst case + offer a plan-level/token-level knob
  (see roadmap R1).

### S2 — License keys remain in a plaintext column by design ("Phase C" never executed)
*Severity: Medium | Category: data at rest*

`licenses.license_key TEXT NOT NULL UNIQUE` (`db/migrations/20260320_init.up.sql`) is never
dropped. Migration `20260514_license_key_encrypted.up.sql` added
`license_key_encrypted BYTEA` and states "Phase C will drop the plaintext column" — no later
migration does (`grep "DROP COLUMN"` across `db/migrations/` finds no `licenses.license_key`).
`DecryptLicenseKey` (`internal/store/store.go:528`) explicitly **falls back to the plaintext
column** when ciphertext is missing. Encryption is also **off** unless
`RELEASE_KEY_ENCRYPTION_KEY` is set — startup logs only a warning in that case
(`internal/config/config.go` `ValidateSecurityDefaults` and `cmd/server/main.go`
`license key encryption: DISABLED` warning).

- Consequence: a database dump yields live license keys regardless of encryption config.
  README's "License keys hashed with SHA-256, encrypted at rest" is therefore
  conditional: hashing (`key_hash`, `20260322_license_key_hash`) is unconditional;
  encryption-at-rest is opt-in and incomplete until Phase C.
- Not a vulnerability by itself (an admin DB admin can also just call the reveal endpoint),
  but it contradicts the documented security claim and is directly relevant to a platform
  whose product is licensing.

### S3 — Brute-force lockout state is process-local (does not replicate)
*Severity: Medium in multi-replica deployments, Low otherwise*

`NewBruteForceProtection` (`internal/middleware/bruteforce.go`) is an in-process map,
constructed once in `cmd/server/main.go:257`. Rate limiting has a Redis backend
(`internal/middleware/ratelimit.go`, `cmd/server/redis.go`); **the brute-force lockout does
not participate in it**. With N replicas an attacker gets N × `BF_MAX_FAILS` attempts
against `/license/verify` before any lockout, and a lockout on replica A does not affect
replica B.

- The default deployment is single-process (`docker-compose.yml`: one container), so this
  matters mainly for scaled-out installs — which the Redis rate-limiter feature explicitly
  promotes (`.env.example`: "When set, rate limits are shared across instances").

### S4 — Per-IP controls are only as good as the trusted-proxy configuration
*Severity: Low | Category: deployment-dependent*

`cmd/server/main.go:591` trusts `X-Forwarded-For`/`X-Real-Ip` from
`10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16, fc00::/7`; `c.ClientIP()` keys rate limits,
OTP-send caps, brute-force lockouts and audit rows. Any client able to reach the process
from a *trusted* network range (another container on the same Docker network, a sidecar,
a compromised co-hosted service) can rotate the observed IP and reset all per-IP budgets.

- Evidence: `cmd/server/main.go:591-593`; `middleware.RateLimitByIPScoped`,
  `middleware.FailureKeyIP`, `Store.Audit(… IPAddress: c.ClientIP())`.
- No bypass possible from a plain internet client behind a correctly wired reverse proxy;
  flagged because self-hosted deployments frequently place Keygate directly on a shared
  container network without a stripping proxy.

### S5 — Device identifiers are accepted exactly as asserted (no canonical format, no validation)
*Severity: Low (trust-model note) | Category: input trust*

`POST /api/v1/license/activate` takes any `identifier` string, trimmed, ≤ 256 chars
(`internal/handler/license.go:15,37`; `maxIdentifierLen = 256`). There is no format
constraint (no UUID/hash requirement), no server-side proof-of-possession, and no reference
to a client-side device-fingerprint algorithm. `identifier_type` is accepted from the client
(`device`/`user` per the enum in OpenAPI, but the handler does not reject other values —
the service stores what it is given; the DB CHECK on `activations.identifier_type` from
`20260320_init` only allows `device`/`user`, so a foreign value fails at insert → surfaced
as 500 by `apperr.Internal`). The offline token binds the identifier with a truncated
`Fingerprint = SHA256(identifier + ":" + product_id)[:8]` (`internal/license/token.go`),
i.e. 64-bit binding, sufficient for uniqueness within one vendor's devices but not a
collision-hard fingerprint.

- Consequence: anyone holding the license key can activate any identifier chosen freely;
  identifiers are labels, not verified device identities. This is the industry-standard
  model (Keygen/Cryptlex behave similarly) but must be stated in the protocol contract for
  our fork's "device activation management" objective.
- Note: `identifier_type` outside the DB enum currently surfaces as 500 rather than 400 —
  a validation nit (Low), same code path as `handler/license.go` `Activate`.

### S6 — Unbounded request bodies on most endpoints
*Severity: Low*

Body-size caps exist only at three places: portal guards 64 KiB (`cmd/server/main.go`
`portalGuardMaxBody`), idempotency layer 256 KiB (`internal/middleware/idempotency.go`
— only when an `Idempotency-Key` header is present), and Stripe (card data never here).
A plain `POST /api/v1/license/activate` without the header, and all admin write routes,
buffer whatever the peer sends during JSON decoding. Per-IP rate limits reduce, but do not
eliminate, memory-pressure abuse.

### S7 — `/metrics` and `/health` are served without authentication
*Severity: Low / Info*

`cmd/server/main.go:638` `r.GET("/metrics", …)` and the `/health` handler (returns version)
are outside every auth group. Prometheus output exposes route names, status counts,
per-product activation counters and Go runtime/process metrics. `docs/api-contract.md` does
not mention them. Acceptable on a private network; a public install leaks operational
fingerprinting material (and version aids exploit targeting).

### S8 — OTP codes are logged in plaintext when SMTP is unconfigured
*Severity: Low (deliberate fallback, warned)| Evidence: `internal/handler/otp.go:80-100`

`logCodeFallback` writes the live login code to the server log at WARN when SMTP is absent
(unset or unconfigured). A config-read *error* correctly fails closed (no code logged). The
risk is that centralized logs become a login oracle while SMTP is unconfigured — an explicit
trade-off, currently only surfaced as a runtime warning, and `.env.example` advertises the
behavior ("OTP codes print to logs if not set").

### S9 — Audit trail is best-effort and not tamper-evident
*Severity: Low*

`Store.Audit` (`internal/store/store.go:1205`) performs the INSERT in a fire-and-forget
goroutine (5 s timeout, failure only logged). Audit rows can therefore be silently dropped
under load, shutdown, or DB pressure, and `audit_logs` (plain table, `20260320_init`) is
modifiable by any DB-privileged actor with no hash-chain/signature. Coverage is partial:
35 `Audit(...)` calls in `internal/handler/admin.go` vs. 78 handler methods; the sensitive
ones reviewed (key reveal/email, revoke/suspend/reinstate/refund, plan change, settings
secrets clear, API key lifecycle, expiry/usage changes) do carry audit records.

### S10 — Velopack (Windows) feed carries no artifact signature
*Severity: Low (integrity gap relative to the stated objective)*

`internal/service/release_feed.go:210`: *"Velopack does not check signatures; the hash and
HTTPS carry the trust."* Sparkle (`edSignature`) and Tauri (minisign) consumers verify
Ed25519 signatures; the Velopack path relies on TLS plus a server-published SHA-256 in the
same feed that would be substituted by an attacker who controls the response. With
`feed_license_required` gating there is still a license check, but not an end-to-end
integrity guarantee independent of TLS for that channel.

### S11 — No Content-Security-Policy header
*Severity: Info*

Headers set in `cmd/server/main.go`: `X-Frame-Options: DENY`, `X-Content-Type-Options:
nosniff`, `X-XSS-Protection`, `Referrer-Policy`, HSTS (https only). No CSP/`frame-ancestors`
beyond XFO, no `Permissions-Policy`. The SPA (`web/dist`) loads the Scalar docs widget from
`cdn.jsdelivr.net` on `/docs` only (server-rendered page in `cmd/server/main.go`), which
would complicate a strict CSP for that page specifically.

### S12 — Origin/CORS handling details
*Severity: Info*

In production, an `Origin` != `BASE_URL` is refused only for `OPTIONS` preflights; simple
requests fall through without CORS headers (so browsers cannot read the response), and CSRF
on cookie-authenticated mutations relies on `SameSite=Lax` session cookies
(`internal/handler/oauth.go` `setSecureCookie`). In development, any origin is reflected
with `Allow-Credentials: true` — development-only (`ENVIRONMENT` gate) but a foot-gun if a
staging box runs with `ENVIRONMENT=development`.

### Explicitly checked, NOT found (avoiding false positives)

- **Private signing keys cannot leak through JSON**: `PrivateKeyEncrypted []byte` has
  `json:"-"` (`internal/model/model.go:918`); no handler returns it; decryption happens only
  inside `SignArtifactWith` and is zeroized (`internal/service/release_signing.go`).
- **License key hidden from payloads by default**: `model.License.LicenseKey`,
  `KeyHash`, `LicenseKeyEncrypted` all `json:"-"` (`internal/model/model.go:313-320`).
- **No credential logging**: query params, portal path segment, panic dumps and the gin log
  formatter redact; covered by `cmd/server/log_redaction_test.go`.
- **Enumeration oracle**: `/license/verify`, `/deactivate`, SDK loaders collapse to a single
  `404 LICENSE_NOT_FOUND` (`internal/service/license.go:285-305,383,413-423`). Portal
  guards intentionally return identical 404 for "not yours" (`cmd/server/main.go` guard
  comments).
- **API key scope fail-closed**: empty scopes = no route reachable; scope vocabulary is a
  closed enum validated at the boundary (`internal/middleware/auth.go`, `admin.go:1820`).
- **SQL injection**: all data access goes through bun parameter binding or bun `NewRaw`
  with `?` placeholders; no string-concatenated SQL found in the files read. Sort column is
  allowlisted (`docs/api-contract.md`, `admin.go` sort handling tested by `sort_test.go`).

### Unknown / not verified (insufficient evidence — not claims)

- Whether any admin handler reachable in production reads secret settings into a response:
  `GetSecretSetting` exists with correct decryption semantics
  (`internal/store/settings.go`) and `ClearSecretSetting` is audited; a route-by-route
  handler sweep of all 78 admin methods was **not** performed. Recommend a follow-up pass.
- Penetration behavior under load (no load test was run in this read-only phase).
- Supply-chain vulnerability status of pinned dependencies (no CVE scan tool was run;
  see §5).

---

## 4. API and database observations

**API**

- One uniform envelope (`{success,data}|{success,error}`) for everything under `/api/`;
  error codes map to exactly one status (enforced by
  `pkg/response/contract_test.go:131` `TestErrorCodeMapsToExactlyOneStatus`, which fails
  when the scan finds <200 call sites — an anti-foot-gun worth keeping).
- `docs/openapi.yaml` covers **only** public SDK + feed routes: 20 paths (`/config`,
  `/license/*`, `/portal/seats*`, `/invites/accept`, `/releases/...`). The ~100 admin,
  portal-session, auth and billing routes exist only in `cmd/server/main.go` +
  `docs/api-contract.md` prose. SDK authors get schemas; admin-API integrators in other
  languages do not.
- Raw-body exceptions are documented precisely (`docs/api-contract.md`): feeds, `public.pem`,
  Stripe webhook, `/pay/:checkout_id`.
- Pagination default 50 / cap 200, clamped not rejected; only `/admin/licenses` honors
  `?sort=`; documented and enforced by `pagination.go` + `sort_test.go`.
- `Idempotency-Key` supported on exactly three writes (`activate`, `usage`,
  `floating/checkout`), documented with the full error matrix.

**Database**

- bun + PostgreSQL, auto-migrated at startup (`db.RunMigrations("db/migrations")` in
  `cmd/server/main.go`); 34 up/down migration pairs from `20260320_init` to
  `20261005_artifact_tauri_signature`.
- Correctness-critical invariants enforced in SQL, not application code:
  `activations UNIQUE(license_id, identifier)`; partial unique index
  `licenses(key_hash) WHERE key_hash != ''`; `release_signing_keys` partial unique
  "one active per product" (`20260511`) with tightened byte-length CHECKs (`20260513`);
  usage `usage_counters` unique per `(license_id, feature, period, period_key)`.
- Licensing lifecycle columns: `status` CHECK (`active|trialing|past_due|canceled|expired|
  suspended|revoked`), `valid_from/valid_until/suspended_at/canceled_at`,
  `20261004_license_suspended_by`, `20260908_maintenance_period` (`updates_until`),
  `20260907_license_payment_intent`.
- Secrets at rest: `settings.enc:v1:` AES-GCM values for `smtp_password` and
  `cloudflare_api_token`, AAD `setting:<key>`, and **writes refuse** when no master key
  exists (`ErrSecretEncryptionUnavailable`, `internal/store/settings.go`).
- Horizontal-scaling caveats visible in schema+code: idempotency/refresh/OTP cleanup jobs are
  started per process (safe), but brute-force state (S3) is not in the DB.

---

## 5. Licensing and dependency concerns

**This project's own licensing (fork-critical):**

- `LICENSE`: AGPL-3.0 **plus** an appended Section 7(b) Additional Term requiring the
  "Powered by Keygate" attribution (link to https://keygate.app) in all interactive user
  interfaces.
- `NOTICE`: the same attribution requirement is expanded to every derivative, white-label
  and SaaS deployment, and additionally asserts that the name "Keygate", its logo, and
  keygate.app are **trademarks not covered by the license** — derivatives "must use a
  different name and logo".
- `AGENTS.md` (project instructions) marks the attribution as a legally binding license
  requirement and forbids removing/altering it (`attribution_text`/`attribution_url` in
  `GET /api/v1/config`, `X-Powered-By` header via `internal/branding/branding.go`, admin
  sidebar and portal footer in `web/src/components/layout.tsx`, email footers in
  `branding.EmailFooter`).
- Practical read for our fork: **rebranding ≠ attribution removal.** A fork may adopt a new
  product name/logo (as NOTICE requires) while keeping the "Powered by Keygate" notice in
  UI, header, config API and emails; removing it requires a commercial license
  (hello@keygate.app). Verify with counsel before changing any of these surfaces.
- AGPL obligations also apply to our deployment model: anyone interacting with the modified
  server over a network must be offered its source.

**Dependencies (direct, from `go.mod`):**

| Dependency | License (upstream) | Role |
|---|---|---|
| `gin-gonic/gin` v1.12.0 | MIT | HTTP framework |
| `uptrace/bun` + `pgdialect`/`pgdriver` | MIT | Postgres ORM/driver |
| `golang-jwt/jwt/v5` | MIT | session JWT |
| `golang.org/x/crypto` (hkdf), `x/net`, `x/mod` | BSD-3-Clause | HKDF, semver helpers |
| `aws/aws-sdk-go-v2` (+ s3, credentials, smithy) | Apache-2.0 | S3-compatible storage |
| `stripe/stripe-go/v82` | MIT | payments |
| `redis/go-redis/v9` | BSD-2-Clause | optional rate-limit backend |
| `prometheus/client_golang` | Apache-2.0 | metrics |
| `joho/godotenv` | MIT | `.env` loading |
| `google/uuid` | BSD-3-Clause | ids |
| indirect: `sonic`, `quic-go`, `mongo-driver` (indirect only), `goccy/go-yaml`, etc. | MIT/BSD/Apache-2.0 | transitive |

All direct Go dependencies are permissive (MIT/BSD/Apache-2.0) — **no copyleft contamination
of a permissive fork's own choice**; note the project itself is AGPL for its own code.
License list above should be re-confirmed mechanically in CI with
[`google/go-licenses`](https://github.com/google/go-licenses) or
`licensebill` — **not run in this phase** (no such tool in the repo).

**Frontend (`web/package.json`):** React 19, React Router 7, Radix UI, TanStack Query,
Recharts, Tailwind 4, Vite 8, Biome — MIT/ISC/Apache-2.0 all. One external runtime
dependency in HTML: the Scalar docs widget from `cdn.jsdelivr.net` on `/docs`
(`cmd/server/main.go` `docsPage`) — a third-party script origin on a same-site page; note
for CSP/self-hosting work.

**Docker/CI:** `docker-compose.yml` (postgres `keygate:keygate` default creds — fine for
quickstart, must be overridden), `.github/workflows/ci.yml` (vet, unit, `-run "Atomic"`
integration slice, frontend typecheck/lint/build; `permissions: contents: read`),
`release.yml` (GHCR multi-arch push on tags, draft GitHub release). No third-party actions
outside `actions/*`, `oven-sh/*`, `docker/*`, `softprops/*` — reasonable, but versions float
on major tags (`@v7` etc.), which is a supply-chain review item, not a finding.

---

## 6. Testing strategy (security-focused, for the next phase)

Carried forward with full detail into `docs/implementation-roadmap.md` §5; the invariants to
protect first, each already backed by at least one test today:

1. **Atomicity**: activation caps, floating caps, usage quotas, idempotency claims —
   keep them gated behind a real Postgres in CI (`TEST_DATABASE_URL`), because they are
   skipped without it (`internal/store/integration_test.go:16-30`).
2. **Contract**: error-code → status bijection (`pkg/response/contract_test.go`) and log
   redaction (`cmd/server/log_redaction_test.go`) must never be deleted when touching
   handlers.
3. **Enumeration**: extend the existing pattern with handler-level tests asserting equal
   bodies/statuses for "unknown key" vs "known but unusable" on every public SDK route.
4. Known CI gap to fix later: `ci.yml` runs the integration slice as `-run "Atomic"`
   (`.github/workflows/ci.yml:54`), which matches 9 of the 92 store tests — the
   **activation race tests do not match that filter** and are therefore skipped in CI
   (verified by us out-of-band by running the full suite manually: all pass).
5. Recommended additions (not implemented in this phase): concurrency fuzz on
   `ActivateWithinLimit` at the HTTP layer, token-rotation/kid verification vectors once
   key rotation exists, an SBOM + CVE scan step, and CSP regression tests.

---

*Files examined for this document: see the consolidated list in
`docs/feature-gap-analysis.md` §8 ("Files examined"). Feature classification and gaps are in
`docs/feature-gap-analysis.md`; target design in `docs/architecture-proposal.md`; ordered
plan in `docs/implementation-roadmap.md`.*