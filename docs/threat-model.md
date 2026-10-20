# Threat Model — self-hosted licensing platform

Method: STRIDE-flavored asset/adversary analysis scoped to the Keygate fork, grounded in
the audit evidence (`docs/security-audit.md`, findings S1–S12) and the protocol in
`docs/api-design.md`. Design phase — nothing here has shipped.

**Explicit non-claim:** client software cannot be made tamper-proof. Determined local
privilege defeats any client-side check; the design language in this document is
*raise the cost, bound the damage, keep the server authoritative*, never *prevent*. All
protections below are advisory-enforced server-side; where a client defends itself, the
server treats the client's claims as assertions, not facts.

---

## 1. Assets (what we protect)

| Asset | Where it lives | Confidentiality | Integrity | Availability |
|---|---|---|---|---|
| `LICENSE_SIGNING_KEY` (Ed25519 seed) | server env / secret manager | **critical** | **critical** | high |
| `RELEASE_KEY_ENCRYPTION_KEY` (AES-256 master) | server env | **critical** | **critical** | **critical** (loss ⇒ signing keys unrecoverable) |
| `JWT_SECRET` (HS256 sessions) | server env | **critical** | high | high |
| Product Ed25519 private keys | DB, AES-GCM at rest (AAD=`product_id`) | **critical** | **critical** | high |
| License keys (customer credentials) | DB (hash + optional ciphertext + legacy plaintext column ⇒ S2) | high | high | high |
| Customer data / PII (emails, devices, IPs) | DB | high | high | medium |
| Admin + `kg_live_` API keys | DB (SHA-256 of the secret) | high | high | medium |
| Update artifacts + signatures | S3-compatible storage + DB metadata | medium (public by design) | **critical** | medium |
| Audit log | DB `audit_logs` | medium | high (S9: not tamper-evident) | medium |
| Rate-limit/lockout state | memory (+Redis if configured) | low | medium (S3: per-process) | medium |
| Service availability | server | — | — | high (licensing failing = customer apps stop activating) |

Adversaries considered: **A1** anonymous internet client · **A2** licensee sharing keys ·
**A3** licensee vs. their own installed binary (legitimate owner, hostile intent) ·
**A4** operator/DB insider · **A5** network attacker (MITM/redirect) · **A6** upstream
compromise (update channel, dependencies, CI) · **A7** co-located process in the deploy
network (docker sidecar posture).

Trust boundaries: (TB1) client ↔ Internet ↔ server API; (TB2) anyone ↔ feeds; (TB3)
browser ↔ admin/portal (cookies); (TB4) server ↔ Postgres/S3/Redis/SMTP/Stripe;
(TB5) trusted-proxy edge; (TB6) developer machine ↔ build/CI ↔ registry.

---

## 2. Threat register

Legend for **Mitigation owner**: 🟢 server can fully mitigate · 🟡 server *helps*, client
must finish · 🔵 client-only · ⚪ accepted/residual (documented, not "fixed").

### T1 — License-key sharing (an activated copy passed to third parties)
- Mechanics: the key is a bearer credential (`license_key` = auth on `/license/*`).
- 🟢 Server: activation caps per plan (`max_activations`), enforced under `FOR UPDATE`
  (verified by race tests); comparable for seats/floating sessions.
- 🟢 Server: each activation is identifier-bound; tokens carry `did` + truncated `fpr`,
  so a token lifted from machine A fails a client check on machine B *if* the client
  re-derives its identifier.
- 🟡 Client: pin its identifier, refuse to run on a mismatch; optionally re-derive binding
  from hardware (proposed `device_binding`, R-17).
- 🟡 Server: portal shows `last_verified`/IP to the owner (self-service policing).
- ⚪ Residual: sharing *between* honest-but-colluding humans across N devices ≤ cap is
  economically indistinguishable from legitimate use without hardware attestation
  (proposed R-17 raises cost; never zero).

### T2 — Repeated activation (repeat calls, retries, double-installs)
- 🟢 Verified: repeat = `already_activated`, *no slot consumed*; handled inside the same
  transaction as the limit (`ActivateWithinLimit`, `ErrAlreadyActivated`); `Idempotency-Key`
  collapses timed-out retries; both race tests green in this audit.
- ⚪ None — closed.

### T3 — Concurrent activation (parallel requests exceeding the cap)
- 🟢 Verified: single license-row `FOR UPDATE` serializes contenders; test asserts
  *exactly* `max` successes among 12 contenders; same pattern for floating
  (`CheckOutFloatingWithLimit`) and quotas (`IncrementUsageCounterWithLimit`).
- 🟡 Multi-instance note: limits are DB-atomic (replica-safe); *lockouts* are not (S3) —
  R-12 shares them.
- ⚪ Residual: none for caps; N× lockout budget across replicas until R-12.

### T4 — Client-side identifier spoofing (invented hardware IDs)
- 🤨 Fact: identifiers are client-asserted, ≤256 chars, unvalidated (audit S5). The
  server *cannot* distinguish a forged identifier from a real one by itself.
- 🟢 Server: bound the blast radius — caps apply regardless of identifier honesty; audit
  rows record what was asserted + IP; canonical profile (§3.1 api-design) rejects
  malformed ids (quality, not truth).
- 🟡 Client: derive identifiers from hardware/OS keystore so *other* clients can't forge
  *the same* identifier (collisions cause "already activated" for the impostor — a DoS on
  the thief, benign for the owner).
- 🔵 With R-17 binding: activation stores a one-way binding; later activations must
  present a verifiable assertion, making cross-party forgery fail instead of merely collide.
- ⚪ Residual: identifier truth is a client claim, permanently.

### T5 — Token modification (edit claims: extend `vun`/`upd`, change `did`/`feature`)
- 🟢 Mitigated cryptographically: Ed25519 over the *exact payload bytes* — any claim edit
  invalidates the signature. `vun`/`upd`/`grc` live **inside** the signed payload
  precisely because the envelope used to be unsigned (fixed; `token.go` comments).
- 🔵 Client must verify signature *before* trusting any claim, and must not accept
  unsigned fallbacks ("if signature fails, allow" bugs are the classic failure).
- 🟡 Server: `/license/pubkey` + proposed `kid` list lets a client pin keys; conformance
  vectors (negative cases: mutated payload, truncated sig) enforce verifier correctness.
- ⚪ Residual: none if verification is done; clients that skip it are out of contract —
  stated in the conformance kit, not "fixed".

### T6 — Token replay (captured token reused elsewhere/later)
- Cases: (a) *same device, later* — intended (`exp` window); (b) *other device* — blocked
  by client-side `did`/`fpr` check (🟡 client duty; server alone can't see offline use);
  (c) *after revocation* — bounded by `exp ≤ token_ttl_days` (S1) and closed further by
  the proposed revocations list (R-09: clients pin `rev` monotonicity, defeating
  rollback of an old list); (d) *captured at log/proxy* — log redaction tests + redacted
  panic recovery + path/query redaction remove the classic leak paths (audit: verified);
  feed URLs accept the **token** rather than the key deliberately (and refuse the key in
  the URL, 400).
- 🟡 Server: short default TTL (7 d, plan-tunable) = replay half-life; brute-force/rate
  limits on issuance endpoints.
- ⚪ Residual: within `exp` a stolen token works offline by design — that is the price of
  offline validation, quantified as the revocation latency contract in `api-design.md` §7.2.

### T7 — Signing-key compromise
Three keys, three playbooks (status = current reality → target in `cryptographic-design.md`):

| Key | Today | Compromise impact | Server-side response | Target |
|---|---|---|---|---|
| `LICENSE_SIGNING_KEY` | single seed, one pubkey endpoint, **no kid/rotation** | attacker mints offline tokens ⇒ full licensing bypass until clients re-fetch keys | rotate env var; restart; old tokens become invalid instantly for clients **only if** clients re-fetch (they cache 1 h) | `license_signing_keys` table (R-08): overlap rotation + revocations list; rotation *invalidates nothing abruptly*: retired key stops being listed after epoch, forged tokens under old kid die with it |
| Product artifact keys | AES-GCM at rest (AAD binds row to product), admin-only endpoints, never in JSON (`json:"-"`), never on clients | attacker ships malicious updates to that product's users | rotate (`/signing-key/rotate`, atomic single-active index), re-sign policy at publish already refuses content mismatch (`ARTIFACT_MODIFIED`/`ErrArtifactContentChanged`) | keep; add published `kid` set (§9 api-design) so update verification survives rotation |
| Master `RELEASE_KEY_ENCRYPTION_KEY` | env; loss = unrecoverable signing keys (config comment) + license-key decryption; whole-DB decrypt if leaked | total | no automated re-encrypt (documented gap) | managed re-encryption runbook (R-13); refuse plaintext mode; offline backup mandatory |
- 🟡 Distribution hygiene: keys never appear in client binaries or `/metrics` or logs
  (audited); private halves never serialize in responses (field-level `json:"-"` verified).
- ⚪ Residual: a compromised *server* is game over for online truth — self-hosted premise
  accepts this; the design limits lateral blast radius (purposes isolated by HKDF).

### T8 — Local file modification (client tampering: token cache, config, patched binary)
- Threat: attacker edits the persisted token/claims, trial clock, or the binary's license
  check; or patches out the check entirely.
- 🔵 Client-only: store tokens with integrity protection where the OS allows (DPAPI/Keychain/
  file perms), re-verify on use, treat stored state as untrusted cache.
- 🟡 Server: keep the *non-bypassable* effects server-side — activations, seats, quotas and
  downloads are enforced at request time; a patched client still cannot get fresh tokens,
  seats, or gated downloads.
- ⚪ Residual (explicit): binary patching defeats client checks on offline features. We do
  not claim anti-tamper; obfuscation is cost-raising only. The honest controls are server
  authority + signing channel integrity (T7/A6).

### T9 — Clock manipulation
- Forward setting (extend past `vun`/`exp` blindly) fails because claims are compared to
  *client* clock wrongly: clients must treat `exp` in their own clock and *also* compare
  against last-seen-server-time (SDK guidance: monotonic high-water mark persisted
  locally — this is what defeats **rollback**).
- 🟢 Server: token TTL is server-clock minted and clamped to `valid_until+grace`; online
  verify always uses server truth; `fpr`/`nonce` unaffected by time.
- 🟡 Client duty (conformance rule): persist `max(last_seen_server_time)`; if local clock
  < persisted high-water mark ⇒ suspect clock rollback ⇒ require re-verify (fail-closed
  offline). Concurrency fuzz: wall-clock jumps must not extend an already-issued `exp`.
- ⚪ Residual: a client that skips the high-water mark cannot detect clock rollback
  offline (inherent); document per-SDK.

### T10 — Network interception (MITM, downgrade, replayed requests)
- 🟢 Transport: HTTPS required in production (boot warning; cookie `Secure` decided from
  request reality); HSTS with preload when `BASE_URL` is https (verified); no plaintext
  endpoints exist after TLS termination.
- 🟢 Token-in-URL refused on feeds (400 with instructions); credentials never logged even
  if someone tries (`credentialQueryParams` redaction + tests).
- 🟢 Origin policy: foreign-origin preflight refused in production; requests carry no
  bearer in headers that intermediaries log (S12 documented nuance: non-preflight fallthrough
  gets no CORS headers ⇒ browsers can't read; `SameSite=Lax` covers mutation CSRF).
- 🟡 Client: standard TLS validation; optional certificate pinning is an *SDK convenience*,
  not protocol requirement (self-hosted servers change certs — pinning would brick
  customers; guidance: pin the **license signing key**, not the TLS cert, for offline
  trust).
- ⚪ Residual: compromise of the reverse proxy/edge (A7) can serve hostile content —
  out of app scope; deploy guidance in R0 docs.

### T11 — Offline revocation limitations
- 🤨 Inherent: an offline client cannot learn of `revoke/suspend` beyond its last
  contact. Bound = `min(token_ttl_days [7], vun + grc)` (audit S1; now a documented
  contract in `api-design.md` §7.2).
- 🟢 Server: plan-level `token_ttl_days` is the dial (shorter = less offline freedom);
  online endpoints always reflect revocation instantly (`404` collapse); activations
  physically deleted on revoke; `updates_until` gates gated feeds/downloads server-side.
- 🟡 Proposed R-09 revocations list: shorten *practical* latency for any client that can
  reach the server at all periodically (list fetch is cheap/cacheable and independent of
  re-verify).
- ⚪ Residual (cannot be removed): with *no* connectivity ever, revocation latency is
  exactly the offline window. Told to operators as a number, not a promise.

### T12 — Compromised client execution environment (debugger/root, memory scraping)
- 🔵 Client-only concerns: secrets in memory are readable to the debugger; the license key
  the user typed/persisted is re-derivable.
- 🟢 Server: nothing secret is given to the client to begin with (no shared MAC secret —
  explicitly why HMAC tokens were rejected in `token.go`), so heap-scraping yields the
  user's own key + public keys only; API keys are never in binaries (s2s keys are for
  servers).
- 🟡 Optional client advisories: detect debugger/jailbreak *for casual copying*, refuse to
  log tokens, wipe buffers (cost-raising only).
- ⚪ Residual: full. Never claim tamper-proofing.

### Additional threats (audited context)

| ID | Threat | Owner | Status |
|---|---|---|---|
| T13 | Key/lifecycle **enumeration oracles** on public endpoints | 🟢 server | **closed by design** (uniform `404 LICENSE_NOT_FOUND` everywhere out-of-band; portal returns identical 404 for "not yours"; tested behavior) |
| T14 | Brute-force issuance/probing (rate) | 🟢 server | per-IP buckets + exponential lockout; **S3: lockout not shared across replicas** → R-12; OTP: 3/10min per email, 5 attempts, 60/min invites |
| T15 | Signup/mailbox abuse (mail-cannon OTP to strangers) | 🟢 server | `otp_send` 30/h/IP + `signup_mode=licensed_only` fail-closed gate |
| T16 | Session/token theft in browser (XSS/CSRF) | 🟡 server+client | HttpOnly+SameSite=Lax cookies, DB-checked roles per request, refresh rotation with reuse detection (wipes all sessions); CSP missing (S11) → R-19; portal body cap 64 KiB |
| T17 | Admin API-key leak | 🟢 server | SHA-256 at rest, prefix display only, scopes fail-closed, product binding (403), rotation kills old secret instantly, last_used+IP observability; **blast radius** = key scopes (why `admin` wildcard must be rare) |
| T18 | Webhook forgery / SSRF / replay to *our* server | 🟢 server | HMAC-SHA256 `X-Keygate-Signature` over exact body bytes; private-target refusal (opt-in `WEBHOOK_ALLOW_PRIVATE`); Stripe signature verified incl. livemode + version guard; replay via `processed_events` claim |
| T19 | Payload-forgery on release artifacts (storage tampering) | 🟢 server | SHA-256 pinned at finalize, re-hashed before signing, `ARTIFACT_MODIFIED` refuses; signatures frozen at publish |
| T20 | Feed **rollback** (serve an older valid feed/template) | 🟡 server→client | proposed signed `rev`/timestamp (R-15); Velopack channel unsigned-by-design gap **S10** documented per-platform |
| T21 | Legacy plaintext license keys in DB (S2) | 🟢 server | master-key optional today → R-13 mandatory + Phase C drop; until then: DB dump ⇒ keys — stated in ops docs |
| T22 | Audit tamper/drop (S9) | 🟢 server | async/best-effort today → R-14 sync/outbox + hash chain; insider-with-DB remains ⚪ until R-14 |
| T23 | Supply chain: dependency/CVE/action-tag drift, `cdn.jsdelivr.net` on `/docs` | 🟢 server | no scanner today → R-20 (SBOM/CVE/license), pin Actions by SHA, self-host docs asset (R-19) |
| T24 | Trust-edge spoof (`X-Forwarded-For`) resetting per-IP budgets (S4) | 🟢 server | R-04: `TRUSTED_PROXIES` explicit, default trust-nothing |

---

## 3. What the server enforces vs. what clients must do

**Server-authoritative (holds even against a hostile client):** license existence/status
lifecycle; activation/seat/floating/quota caps (DB-atomic); entitlement resolution; gated
feed/download cutoffs (`updates_until`, `license_status`); artifact digests + signatures;
audit of lifecycle mutations; rate/lockout policy (post-R-12).

**Client-required (protocol behavior, any language):** verify the Ed25519 signature before
reading any token claim; reject expired `exp`, mismatched `did`/`fpr`, `vun`/`upd`
violations; persist tokens + last key snapshot + server-time high-water mark; treat
`404` on verify as "cannot validate" (never "key deleted"); re-verify on schedule ≤ `exp`;
fetch revocations when capability present (R-09) and honor `rev` monotonicity.

**Optional SDK conveniences (not protocol):** pinning, hardware identifier derivation,
obfuscation/debugger detection, background refresh scheduling, telemetry, cached
entitlement maps.

---

## 4. Security requirements derived (normative checklist for implementers)

1. No shared secret may ever ship in a client artifact (rules out HMAC-style tokens).
2. Every client-usable assertion carries an `exp` bounded by `min(TTL, vun+grc)`.
3. Every state-changing public mutation has an audit row (transfer must not ship without
   R-14's synchronous semantics).
4. Every error path on public SDK routes must resolve to one of the codes in
   `api-design.md`; enumeration tests guard it.
5. Server must document, numerically, the offline revocation window (S1) wherever it
   documents token TTLs.
6. Key rotation must be exercisable without downtime for *new* validations (overlap),
   and its failure mode must be deterministic (old kid rejected after epoch, not before —
   and never "silently unverified").
7. Claims about tamper-resistance in marketing/docs must match this model: no
   "unbreakable" offline enforcement language.
8. Any new client-facing field must be additive and ignorable (compat strategy §5).