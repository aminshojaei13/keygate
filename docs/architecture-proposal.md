# Architecture Proposal — language-agnostic licensing platform (fork target)

Scope: evolve the current Keygate codebase (Go 1.27 + gin + bun/PostgreSQL + React SPA;
verified baseline in `docs/feature-gap-analysis.md`, risks in `docs/security-audit.md`) into
the target platform: self-hosted, **language-agnostic** licensing with device activation
management, offline license validation, and application integrity verification.

**Prime directive of this proposal: keep the server.** Nothing in the current API forces a
client language and no rewrite is recommended. Every change below is *additive* to the
existing HTTP/JSON surface, with explicit compatibility handling (§7).

---

## 1. Executive summary

The fork's architecture stays: single Go binary, PostgreSQL, optional S3-compatible
storage, optional Redis for shared state, React SPA served from `web/dist`. Around it we
formalize four contracts that are currently *implicit*:

1. **License protocol contract** — a complete, versioned OpenAPI 3.1 + JSON Schema
   description of *every* route (currently 20 of ~140 documented), including the error
   envelope already enforced by `pkg/response/contract_test.go`.
2. **Offline-token contract** — precisely specified byte-level verification (with
   `kid`-based key rotation and documented revocation latency), and ideally a JOSE
   (EdDSA/Ed25519) representation so any language uses a stock library.
3. **Device-identity contract** — canonical identifier format + validation + stale-slot
   policy server-side, replacing today's free-form ≤256-char string.
4. **Integrity contract** — artifact signing (already strong: Ed25519 + AES-GCM-held keys,
   Sparkle/Tauri) extended with signed feed timestamps/anti-rollback and an optional
   server challenge–response channel to make *runtime* integrity/clone detection possible.

Cross-cutting hardening: mandatory at-rest encryption + Phase C retirement of the
plaintext key column (S2), shared brute-force state (S3), body-size limits (S6),
authenticated metrics (S7), tamper-evident audit (S9), admin MFA, and CI that actually runs
the concurrency/performance suites (§8).

---

## 2. Current architecture (as verified)

```
 Browser (React SPA)          Licensed app / updater (any language)        Merchant backend (s2s)
        │  session cookie                    │ HTTPS+JSON                             │ kg_live_ Bearer
        ▼                                    ▼                                        ▼
┌──────────────────────────────── gin router (cmd/server/main.go) ───────────────────────────────┐
│ /api/v1/portal/*            /api/v1/license/* (+feeds, setup, config)         /api/v1/admin/*  │
│  SessionAuth                 license_key = credential + ID/RB/LF caps          SessionOrAPIKey  │
│                              LicenseBruteForceGuard · RateLimitByIPScoped      + RequireScope   │
│  guards (64 KiB body cap)    Idempotency (3 writes)                                             │
└──────────────┬────────────────────────────┬──────────────────────────────┬─────────────────────┘
               ▼                            ▼                              ▼
      service layer            license.Sign (Ed25519) / release_signing   payment (Stripe, webhooks)
               ▼                            ▼                              ▼
      store layer (bun + pg, FOR UPDATE atomicity, idempotency, processed_events HMAC webhooks)
               ▼
      PostgreSQL (+ migrations auto-run)   ·   S3-compatible storage (artifacts, presigned URLs)
      secrets at rest: AES-256-GCM via HKDF subkeys ("license-key", "release-signing-private-key",
      "setting:<key>")
```

Proven and retained: atomic activation/floating/quota/idempotency (tests pass under real
Postgres — verified in this audit), uniform error envelope with contract test, log
redaction, enumeration-resistant public endpoints, fail-closed API scopes, admin-only
signing-key management.

---

## 3. Target capability architecture

### 3.1 License protocol layer (documentation-first)

- Publish **OpenAPI 3.1** covering all 140 route registrations with reusable components:
  `Envelope`, `Error`, `Page` (limit/offset/total semantics per `docs/api-contract.md`),
  `LicenseKey`, `Identifier`, `Token`, `Idempotency-Key` header, `Retry-After`.
- Check the spec into `docs/openapi.yaml` and add a **drift check** in CI: parse
  `cmd/server/main.go` route registrations and fail if a route is undocumented.
- Keep the tested invariants as *features*: single status per error code (already),
  `snake_case`, RFC3339 UTC, `[]` never null (already documented — now machine-checkable
  via schema tests).

### 3.2 Offline token — contract, then construction

Current format (`internal/license/token.go`): `base64url(payload) "." base64url(ed25519_sig)`
where the signature covers the *payload string bytes*. Claims: `lid,pid,pln,sts,did,ftr,
iat,exp,vun,upd,grc,nce,fpr`. Strengths: no shared secret on the client, nonce, device
binding, `vun`/`upd` inside the signed region (already correct). Gaps: no `kid`, bespoke
split, revocation latency undocumented (S1).

**Proposal (two stages, both backward compatible):**

1. **Spec stage (docs + vectors, no code change):** publish the exact verification
   procedure (UTF-8/byte semantics, base64url without padding, signature input = the
   first segment as bytes, claim table, `fpr` computation `SHA256(did + ":" + pid)[:8]`),
   plus a normative test-vector pack (valid token, expired, `vun` passed, wrong `did`,
   tampered payload, truncated signature) that every SDK must pass.
2. **JOSE stage (optional, additive):** issue tokens as RFC 7515/8037-style JWS with
   `alg: EdDSA`, `kid`, and the same claims (`exp`/`vun`/`upd`/`grc`/`fpr`/`nce`), served
   alongside the legacy token (`token_v2` field or `Accept`/`?format=jwt`) during a
   deprecation window. Rationale: Ed25519 JWS verifiers exist in every mandated language
   (PYCA `cryptography`, `jose`/`jws`, `nimbus-jose-jwt`, `jose4j`, .NET
   `Microsoft.IdentityModel.Tokens`, Go `lestrrat-go/jwx`, Rust `jsonwebtoken` with
   `eddsa`, C `libjose`/libsodium glue), eliminating our bespoke parser from every SDK.

**Key rotation (new, required for both stages):** store *multiple* active license-signing
keys in a new table `license_signing_keys (id=kid, public_key, private_key_encrypted
(AES-GCM, AAD=placeholder), active, created_at, retired_at)` — mirroring the proven
`release_signing_keys` design, including the partial unique index pattern. `GET
/api/v1/license/pubkey` grows `keys: [{kid, public_key, algorithm}]` while keeping the
legacy scalar fields for one release cycle. Verification logic: accept any *active-or-retired-within-window* kid; issuance uses the newest.

**Revocation latency (contract, then mechanism):**
- Document the bound immediately: offline validity ≤ `min(plan.token_ttl_days [default 7],
  valid_until + grace_days)` and it survives revoke/suspend until `exp` (S1).
- Add an optional per-plan `token_ttl_days`-driven *recommended client policy*: verify at
  start + every 24 h; treat `exp` as hard stop.
- Mechanism (phase-in): a signed **revocation list** endpoint
  `GET /api/v1/license/revocations` (or per-product), containing revoked license IDs +
  `rev` counter, signed with the same license-signing key, cached by clients offline within
  their check-in window. Cheap to implement (table + handler + signature) and kills the
  "revoked license keeps running for up to 7 days offline" story beyond the token TTL.

### 3.3 Device identity & activation management

- **Canonical identifier profile:** accept UUIDv4/UUIDv7 or
  `SHA-256::<64 hex chars>` (hardware-derived) as the normative format; enforce
  `^[0-9a-fA-F-]{32,36}$` or `^sha256:[0-9a-f]{64}$` for *new* activations, keyed by
  `identifier_type`. Free-form legacy identifiers are grandfathered (§7 risk 4).
- Server-side validation at the handler (today only length ≤256 — S5), which also fixes the
  `identifier_type` → 500 nit (DB CHECK rejects unknown types after the service accepted
  them).
- **Policy module (plan-level):** `stale_reclaim_days` — activations whose
  `last_verified` is older than N days are freed automatically by the existing expiry loop
  (`internal/service/expiry.go` already runs `DeleteExpiredActivations`). This answers the
  "lost laptop" problem structurally, on top of today's portal self-service recovery.
- Metadata: optional `device_meta` JSONB (hostname/OS/arch hints, client version), asserted
  by the client, displayed in portal/admin — clearly labeled as untrusted claims.
- Keep the atomic path exactly as-is: `ActivateWithinLimit` with `FOR UPDATE` on the
  license row (verified under concurrency); all *new* policies run inside that same
  transaction so limits can never be exceeded by policy evaluation.

### 3.4 Application integrity verification (stated objective — currently absent)

Three complementary mechanisms, in implementation order:

1. **Artifact integrity (exists — extend):** keep Ed25519 per-product keys, AES-GCM
   storage with `product_id` AAD, server-computed SHA-256 re-check at sign time. Add
   **signed feed timestamps/serial** (`serial` + `pubDate` already exist for Sparkle; add a
   monotonic `feed_rev` counter signed in each feed payload) so an offline updater can
   detect rollback to an older, once-valid feed. Document the Velopack trust gap (S10) and
   evaluate Velopack's newer signing support upstream before claiming per-platform parity.
2. **Runtime challenge–response (new endpoint, additive):** `POST /license/challenge`
   `{license_key, identifier, nonce}` → server returns a signed response over
   `(client_nonce || server_nonce || license_id || product_id || returned_exp)` using the
   license-signing key. A client that *cannot* fetch and verify this has no proof it is
   talking to the genuine server; conversely the server can *require* a client-supplied
   challenge freshness on sensitive operations. This is language-neutral (Ed25519 verify)
   and composes with offline mode (offline token + last challenge receipt).
3. **Clone/VM detection (optional, vendor policy):** an *attestation claim* the client may
   include at activation — e.g. `{"hw":"<hash of machine identity>","app":"<code
   signature identity>"}` plus a portfolio of binding options (Windows DPAPI/TPM, macOS
   Keychain access control, Linux `/etc/machine-id`). Keygate stores the binding
   (`activations.device_binding`) and one-shot-verifies it on later activations so a copied
   license file fails. **Never** ship a private signing key to the client — only public
   keys and non-reusable assertions (this invariant already holds today: private keys never
   leave the server; keep it a test).

### 3.5 Service/layering changes (deliberately small)

- Keep handler → service → store layering (already clean).
- Introduce `internal/licensing/` only as a *contract package*: token verify helpers,
  vectors, identifier canonicalization — so future SDK-generator tooling has one source of
  truth. No microservices, no queue broker; keep the "one server, one database" promise.
- Move brute-force state into the already-present Redis backend (same pattern as
  `resilientBackend`) with the in-memory fallback — fixes S3 without new infrastructure
  requirements (Redis stays optional; single-instance semantics unchanged).
- Centralize request body limits (a `MaxBytesReader` middleware, e.g. 64 KiB default,
  1 MiB admin uploads excluded) — fixes S6.
- `/metrics`: bind behind existing auth when `METRICS_TOKEN`/basic auth is configured,
  keep unauthenticated default for compatibility (S7).

### 3.6 Data model deltas (additive migrations only)

| Change | Purpose | Reuses pattern |
|---|---|---|
| `license_signing_keys` table (+`kid` column on nothing else) | token key rotation | `release_signing_keys` |
| `revoked_licenses` / `license_revocations` view or status+`revoked_at` index | signed revocation list | audit/status columns |
| `plans.stale_reclaim_days`, `plans.identifier_profile` (enum/text) | device policy | existing plan numeric bounds validation |
| `activations.device_meta JSONB`, `activations.binding TEXT` | identity claims | `audit_logs.changes` JSONB style |
| Drop `licenses.license_key` (**Phase C**) behind a maintenance flag after verified backfill | close S2 | migration style of `20260514` with mirrored down |
| Additive `httpsig`-style integrity for audit rows: `audit_logs.prev_hash,hash` | tamper-evident audit (S9) | append-only chain on insert |

Schema remains PostgreSQL-only; no cross-DB support is proposed (out of scope, would tax
the FOR UPDATE-driven correctness model).

---

## 4. API and language-interop design (why no client is privileged)

Contract rules (already true, to be published as JSON Schema + OpenAPI):

- Transport: HTTPS only; plain HTTP refused in production for sessions (warned today).
- Bodies: JSON, `snake_case`; RFC3339 UTC `*_at`; `YYYY-MM-DD` buckets; `[]` not null.
- Auth: SDK routes use the `license_key` (or signed token) itself; s2s uses
  `Authorization: Bearer kg_live_…`; portal/admin use session/JWT or API key + scope.
- Errors: `{success:false, error:{code,message,details?}}`, one status per code,
  `Retry-After` where a wait is implied (implemented).
- Retry: decide from HTTP status; `Idempotency-Key` on three writes (implemented + tested).
- Crypto a client must implement: **Ed25519 verify only** (RFC 8032), base64url, SHA-256,
  and optionally HMAC-SHA256 when it *receives* webhooks. AES/HKDF never cross the wire.
  Every mandated language has this in the standard library or one small dependency.

Per-language notes for the vendor SDK matrix (conformance kit, not SDK code in-repo):

| Language | Ed25519 + token | HTTP/JSON | Watch-outs |
|---|---|---|---|
| C#/.NET | `System.Security.Cryptography.Ed25519` (.NET 8+) or NSec/BouncyCastle | HttpClient | span-safe base64url; AOT-safe trimming |
| Java | JDK 15+ `Ed25519.Signature` or BouncyCastle | `java.net.http` | no ALPN surprises on old JREs; document min JDK |
| JS/TS | `node:crypto` `verify(null,…)`; WebCrypto Ed255? — use tweetnacl-free `@noble/curves` in browser | `fetch` | browser clocks drift → skew allowance on `exp` |
| Python | `cryptography` (`Ed25519PublicKey`) | `httpx`/`requests` | constant-time compare for webhook HMAC |
| Go | stdlib | stdlib | reference implementation (server = oracle) |
| Rust | `ed25519-dalek`, `jsonwebtoken` | `reqwest` | feature-flag TLS backends |
| C/C++ | libsodium `crypto_sign_verify_detached` | libcurl | document exact bytes to verify (the payload *string*), padded vs unpadded base64url |

Anti-recommendations (explicitly out): requiring any client-side framework, gRPC/protobuf
for SDK routes, database access from clients, or embedding server secrets (cancel-keys,
API keys) in binaries — the repo already dropped embedded API keys deliberately
(`cmd/server/main.go` comment block on the license group) and that decision stands.

---

## 5. Security architecture (how each finding is closed)

| Finding | Architectural fix |
|---|---|
| S1 revocation latency | document bound now; plan-level TTL policy; signed revocation list; client policy (3.2) |
| S2 plaintext key column | verify backfill completeness → Phase C drop behind flag; make `RELEASE_KEY_ENCRYPTION_KEY` mandatory in production start-up (upgrade current warning to fatal), keeping decrypt-fallback until Phase C |
| S3 local brute-force | share state via Redis backend (and/or DB table), same fallback pattern as rate limiter |
| S4 trusted proxies | document `TRUSTED_PROXIES` as explicit config (default: empty = trust nothing) instead of hardcoded RFC1918 ranges |
| S5 identifier trust | canonical profile + validation (3.3); keep fingerprint but compute over canonical id; consider 16-byte fingerprint when migrating |
| S6 body limits | global `MaxBytesReader` middleware |
| S7 metrics | optional token/basic-auth on `/metrics` (`METRICS_TOKEN`), default open for compat |
| S8 OTP in logs | default behavior change: log only last-4 of the code when SMTP unconfigured (opt-in full log via `OTP_LOG_CODE=true`) |
| S9 audit | synchronous insert inside the request tx for sensitive ops (or outbox), hash-chain columns, retention/export job |
| S10 Velopack | upstream signing evaluation; document per-platform trust matrix in the SDK kit |
| S11 CSP | `Content-Security-Policy: default-src 'self'` + style/hash exceptions; self-host the Scalar docs asset (removes `cdn.jsdelivr.net`) |
| S12 CORS/CSRF | keep SameSite=Lax; reject mismatched origins for *all* methods in production, not just preflight; consider `Origin` check on state-changing routes |
| new | Admin MFA (TOTP first), login-alert webhooks, `rotate JWT_SECRET`/`LICENSE_SIGNING_KEY` runbook endpoints with overlap window |
| new | SBOM + CVE + license scan jobs in CI (`.github/workflows`), pinned action SHAs |

---

## 6. Testing and quality strategy (architecture-level)

1. **Concurrency invariants are the crown jewels**: activation caps, floating caps, usage
   quotas, idempotency, publish race, last-owner race — must run against real Postgres in
   CI on every PR (fix `-run "Atomic"` filter: it currently covers 9/92 store tests while
   the activation race tests get skipped — see `docs/feature-gap-analysis.md` §8).
2. **Contract tests**: keep `TestErrorCodeMapsToExactlyOneStatus`; add schema validation of
   recorded fixtures (golden JSON) against OpenAPI components; add route↔spec drift check.
3. **Crypto vectors**: token verification vectors (valid/expired/tampered/`kid`-unknown),
   release signature vectors (already partially: `tauri_minisign_test.go` re-implements
   Tauri's verifier — that pattern generalizes as external-verifier ports).
4. **Enumeration regression tests**: identical status+body for "unknown key" vs
   "known but unusable" on every public SDK route.
5. **Security smoke suite**: header presence (HSTS/CSP), redaction (exists), body-limit,
   CSRF-origin rejection, metrics auth, OTP log masking.
6. **Multi-instance test**: two server processes against one Redis/Postgres; assert a
   lockout on A blocks B (regression for S3), idempotency crosses instances, rate limits
   aggregate.
7. **Upgrade/backup drills**: master-key rotation rehearsal (re-encrypt job), restore from
   `make db-backup`, migration up→down→up on a seeded copy.

---

## 7. Backward-compatibility risks (and how each is contained)

1. **Token format change** — any new representation must be *additive*; legacy
   `base64url(base64url(payload).base64url(sig))` verifiers in the field must keep working.
   Contain: dual-field issuance until a documented sunset date; vectors for both formats;
   server accepts old tokens until their `exp`.
2. **`kid` on `GET /license/pubkey`** — response shape gain; keep `algorithm`/`public_key`
   scalars; bump OpenAPI minor (`1.2.0` → `1.3.0`).
3. **Identifier canonicalization** — grandfather existing rows; validate only newly created
   activations; expose the active profile in portal/admin so operators understand slot
   counts.
4. **Making encryption mandatory (S2)** — installs without `RELEASE_KEY_ENCRYPTION_KEY`
   must be given a migration path: refuse start only after an explicit
   `ALLOW_PLAINTEXT_LICENSE_KEYS=false` transition flag declared in an upgrade note (never
   silently enable).
5. **Revocation list semantics** — new clients consume it; old clients unaffected (they
   never call it). No server behavior changes.
6. **Origin rejection for all methods (S12)** — could break legitimate third-party admin
   integrations calling the API from browser contexts with odd origins; scope it to
   cookie-authenticated (mutating) requests and document.
7. **Perf/scale assumptions if TTL shrinks** — lowering `token_ttl_days` multiplies
   `/license/verify` QPS; rate-limit defaults (`RATE_LIMIT_API=60/min × 2, capped ≥120/min`
   on the license group) must be re-derived from expected install counts before any
   recommendation changes.
8. **Branding/attribution** — rebrand allowed by `NOTICE` (different name/logo) but the
   `"Powered by Keygate"` attribution in UI, `X-Powered-By`, and `attribution_text/url`
   config fields must be preserved (license-term constraint, see
   `docs/security-audit.md` §5). All schema/API changes must leave those response fields
   intact.

---

## 8. Recommended implementation order (summary — detail in `docs/implementation-roadmap.md`)

1. **R0 Doc-true baseline** (days): publish findings, fix CI filter, document S1/S2/S10
   realities, `TRUSTED_PROXIES`, body limits — zero API change.
2. **R1 Contract & offline-trust core** (1–2 weeks): full OpenAPI + vectors, `kid` key
   table, pubkey response extension, revocation list, identifier profile.
3. **R2 Integrity** (2–3 weeks): feed rev/anti-rollback, challenge–response endpoint,
   optional device binding, Velopack trust documentation.
4. **R3 Hardening** (parallel track): shared brute-force, at-rest encryption→Phase C,
   audit hash-chain + sync writes, metrics auth, CSP/self-hosted docs asset, admin MFA,
   OTP log masking, trusted-proxy config.
5. **R4 Ecosystem** (ongoing): SDK conformance kit, SBOM/CVE/license CI, dashboards for
   revocation/rotation ops, performance budget for TTL policies.

Each stage lands with its tests (§6); no stage requires a rewrite, a framework change, or
a client-language dependency.