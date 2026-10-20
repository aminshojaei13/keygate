# Cryptographic Design

Scope: every cryptographic construction in the platform — what exists today (evidence from
code), what we standardize on, and the additions proposed. Design phase; no code changed.
Rule zero: **no custom cryptographic algorithms** — primitives are stdlib or IETF/FIPS
published constructions, used in their standard encodings so any language can interoperate
with a stock library.

---

## 1. Executive summary

- The existing crypto is already standard: Ed25519 (RFC 8032), AES-256-GCM (NIST SP
  800-38D), HKDF-SHA256 (RFC 5869), SHA-256 (FIPS 180-4), HMAC-SHA256 (RFC 2104),
  HS256/`JWT` (RFC 7519/7515) for *sessions only*, CSPRNG via `crypto/rand`.
- The interoperability gap is **encoding, not algorithm**: the offline token is a bespoke
  two-part format (fine cryptographically, undocumented verification byte-exactly until
  now) with **no key identifier**, so rotation is a cliff.
- Decision: keep `legacy-ed25519` as normative and fully specified §3; add `kid` key
  infrastructure and *optionally* dual-issue a standard **JWS compact serialization with
  `alg: EdDSA` over Ed25519 (RFC 8037/JOSE)** §4. No format change is required for
  artifact signatures (raw Ed25519 over file bytes — already the right answer), webhooks
  (HMAC-SHA256 over exact body bytes), or digests (SHA-256 hex).
- Nothing client-side shares a secret: the deliberate rejection of HMAC tokens stands
  (evidence in `internal/license/token.go`).

---

## 2. Algorithm inventory (as implemented, with evidence)

| # | Use | Construction | Encoding | Evidence |
|---|---|---|---|---|
| C1 | Offline license token signature | **Ed25519** (RFC 8032) over the *payload string bytes* | `base64url(payload).base64url(sig)`, unpadded (RFC 4648 §5) | `internal/license/token.go Sign/Verify` |
| C1b | Token fingerprint (device binding) | **SHA-256**, truncated to 8 bytes → 16 hex chars | `hex(SHA256(identifier + ":" + product_id)[0:8])` | `token.go Fingerprint` |
| C2 | Artifact signature (Sparkle/Tauri) | **Ed25519** over the **raw file bytes** | Sparkle: std-base64 raw 64-byte sig (`edSignature`); Tauri: minisign-style base64 bundle (`Ed‖keyid‖sig` + global sig over `sig‖trusted-comment`) with `timestamp:\tfile:` comment | `release_signing.go SignArtifactWith`, `tauri_minisign.go TauriSignature` |
| C3 | File digests | **SHA-256** server-computed; client hashes ignored | lowercase hex | `ErrArtifactContentChanged` recheck; `release.go` |
| C4 | License key storage lookup | **SHA-256** of normalized key (UPPER+trim) | hex (`key_hash`, partial unique index) | `keygen.go HashKey` |
| C5 | License key at rest | **AES-256-GCM**, 12-byte random nonce, **AAD = license id** | `nonce‖ct‖tag` in `BYTEA` | `crypto/aesgcm.go`, `store.prepareLicenseForInsert` |
| C6 | Signing-key + secret storage | **AES-256-GCM**, **AAD = product_id** (signing keys) or `setting:<key>` (seeds/settings) | exact 60-byte CHECK for the 32-byte seed (nonce+seed+tag: tight constraint migration `20260513`) | `release_signing.go`, `store/settings.go` |
| C7 | Key derivation | **HKDF-SHA256** (RFC 5869), info = `"keygate-v1-" + purpose`, no salt | 32-byte subkeys; purposes `license-key`, `release-signing-private-key` ⇒ purpose isolation | `crypto/hkdf.go DeriveSubkey` |
| C8 | API keys | 24 CSPRNG bytes → hex (`kg_live_` + 48 hex); stored as **SHA-256** | hex | `store.GenerateRawAPIKey`, `store.HashAPIKey` |
| C9 | Session auth | **HS256 JWT** (RFC 7519/7515) over `JWT_SECRET`, 24 h + rotating refresh (raw token stored as SHA-256) | compact JWS | `middleware/auth.go IssueJWT`, `oauth.go hashToken` |
| C10 | Webhook auth | **HMAC-SHA256** over the exact marshaled body bytes | header `X-Keygate-Signature: sha256=<hex>`, plus `X-Keygate-Event` | `webhook.go signPayload` (L653) / header set (L500-510) |
| C11 | Stripe webhook auth | Stripe's HMAC scheme via SDK verifier, + livemode guard, + prev-secret drain | `Stripe-Signature` | `payment/stripe.go:293-306` |
| C12 | OTP codes | 6 digits from `crypto/rand`; stored **SHA-256** (unsalted — bounded by attempt caps), compare via `hmac.Equal` | hex | `handler/otp.go generateOTPCode/hashOTPCode/hmac.Equal` |
| C13 | Invite/refresh tokens | 32-byte (invite 32-byte hex) CSPRNG; DB stores **SHA-256**, cleared on use | hex | `seats` `invite_token_hash`, `oauth.go hashToken` |
| C14 | License key generation | `PREFIX-` + 4×8 chars from 32-symbol alphabet, **rejection sampling** (no modulo bias) ⇒ 160 bits | plaintext format `KG-…-…-…-…` | `keygen.go randomSegment` |
| C15 | Logout/lockout state | n/a (policy, not crypto) — see threat model T3/T14 | — | — |

Stdlib sources only: `crypto/ed25519`, `crypto/aes`+`cipher.NewGCM`, `crypto/hmac`,
`crypto/sha256`, `crypto/rand`, `golang.org/x/crypto/hkdf`. No third-party crypto crates.

**Known (documented) algorithm-level caveats → tracked, not hidden:**
- C12 unsalted SHA-256 over 6 digits: acceptable *only* because of 5-attempt cap + 3-mails
  per 10 min per email + IP rate limits (evidence §otp.go); flag for future keyed-HMAC with
  server secret (roadmap R20/maintenance) — change is internal, no wire impact.
- C1 truncated 64-bit fingerprint: a binding *hint*, not a collision-resistant digest
  (audit S5). Under the canonical identifier profile (§3 api-design) collisions become
  practical only for an attacker who already knows the key *and* targets the same product —
  residual risk accepted; 16-byte `fpr` blessed for format v2 payloads.
- C9 HS256: fine for server-side sessions; **forbidden** for client-distributable tokens
  (shared secret in a binary = forgeable — the exact reason `token.go` rejects HMAC).

---

## 3. Offline token format — `legacy-ed25519` (normative verification procedure)

This is the current wire format; the spec below is what SDKs must implement. It is fully
specified here for the first time (the OpenAPI description covered it only partially).

**Serialization**

1. Payload = JSON object (UTF-8), fields exactly:
   `lid` (string), `pid` (string), `pln` (string), `sts` (string), `did` (string),
   `ftr` (object, optional), `iat` (int, unix s), `exp` (int, unix s), `vun` (int,
   optional), `upd` (int, optional), `grc` (int), `nce` (string, 16-byte base64url),
   `fpr` (string, optional, 16 hex).
2. `seg0 = base64url(json_bytes)` **without padding**.
3. `sig = Ed25519.Sign(priv, ASCII(seg0))` ← the signed message is the **base64url string
   itself**, not the raw JSON.
4. Token = `seg0 + "." + base64url(sig)` (exactly one `.`, unpadded).

**Verification (client, any language)**

```
i := last_index(token, ".")            # LAST dot, though only one is ever minted
if i < 0 -> reject
b := token[:i]; sig := base64url_decode(token[i+1:])
if not ed25519_verify(pubkey, ASCII(b), sig) -> reject   # ascii == utf8 bytes of b
payload := base64url_decode(b)  ->  JSON parse
if payload.exp <= now_unix -> reject "expired"
if payload.did != my_identifier -> reject "not mine"
if payload.fpr != hex(sha256(did + ":" + pid)[0:8]) -> reject
if payload.vun and now > vun + grc_days -> reject        # term + grace
if payload.upd and release_published_after(upd) -> reject # maintenance window
```

Rules: verify signature **before** parsing claims (never the reverse); no canonical JSON
step exists because the signed bytes are the transported base64 string — *re-serializing
JSON never affects verification*; unknown JSON fields must be ignored (forward compat);
base64url decode must accept unpadded input (padded tolerated, then re-encoded equality
not required because the signature covers the exact string as received).

---

## 4. Format evaluation and selection (offline token)

Candidates evaluated against our constraints (stock-library availability in C#, Java, JS,
Python, Go, Rust, C/C++; no custom crypto; no shared secrets; key rotation; existing
deployed clients):

| Option | Pros | Cons | Verdict |
|---|---|---|---|
| **A. Custom two-part** (current) | tiny; no JSON canonicalization; no library dependency; deployed | no `kid` ⇒ rotation is a cliff; every language needs a 20-line parser; not a standard | ✅ **kept as `legacy-ed25519`**, now byte-exactly specified (§3) |
| **B. JWS compact, `alg: EdDSA`, Ed25519 (RFC 7515/8037 JOSE)** | the standard: `kid` in protected header, key lists (JWK), stock verifiers everywhere (`@noble/jose`, `jose` (JS), `cryptography`+`PyJWT`/`jwcrypto` (Py), `Microsoft.IdentityModel.Tokens` / BouncyCastle (.NET/Java), `lestrrat-go/jwx` (Go), `jsonwebtoken`/`jose` (Rust), `libjose`+libsodium (C)); self-describing 3 parts ⇒ unambiguous format detection | slightly larger (~130 B header); libraries can be fussy about `crit`/`alg` allowlists | ✅ **chosen as optional `jws-eddsa` v2**, dual-issued, additive field |
| **C. PASETO v2.public** | misuse-resistant by design | no stock support in C/C++/older Java/.NET; ecosystem smaller | ❌ (violates "stock library in every mandated language") |
| **D. COSE/CWT (RFC 9052)** | compact binary; great for constrained C/C++ | JSON-heavy web/mobile SDKs pay CBOR tax; tooling uneven | ⏸ defer (a legitimate future format flag, never mandatory) |
| **E. Plain JWT `HS256`** | trivial verify | shared secret ships in binary ⇒ forgeable ⇒ **rejected by design** (`token.go` rationale) | ❌ forbidden platform-wide |
| **F. X.509/PKCS#7 detached for tokens** | enterprise familiarity | heavy for this payload size; ecosystem mismatch with JSON APIs | ❌ |

Artifact signatures are **already selected**: raw detached Ed25519 over file bytes (= what
Sparkle's `edSignature` is; Tauri's minisign directory is the same signature plus a global
comment signature) — no format change proposed, only a *verification-port test* exists and
stays (`tauri_minisign_test.go` pattern).

Webhooks: **HMAC-SHA256 over the exact bytes** — a standard construction (Stripe/GitHub
style); spec addition is documentation only (§6).

Anti-rollback timestamps: optional **RFC 3161 TSP** or TUF-style `rev`/`timestamp` signed
statements are additive fields, not new algorithms (roadmap R-15); default proposal uses
the cheaper monotonic `rev` counter inside existing Ed25519.

---

## 5. Canonicalization & signing-input rules (normative)

| Artifact | Signed input | Canonicalization | Digest |
|---|---|---|---|
| Offline token (legacy) | ASCII bytes of `seg0` (the base64url payload string) | none — the transported string *is* the message | n/a |
| Offline token (JWS) | `ASCII(BASE64URL(protected) + "." + BASE64URL(payload))` per RFC 7515 §5.1 | none (JOSE signing input is pre-encoded) | n/a |
| Revocation list (proposed) | same two-part envelope; payload = JSON object `{rev, generated_at, entries:[…]}` serialized with **sorted keys** (golang: fixed struct field order documented in spec — the JSON the server signs is byte-identical to what it ships; clients do NOT re-serialize: they verify the *received* `payload` segment string, mirroring §3) | none for verifier (byte-identity of transported segment) | n/a |
| Artifacts | **raw file bytes** as stored/retreived | none (no serialization) | SHA-256 of same bytes, hex |
| Webhook body | exact JSON bytes of the POST body | none; clients MUST verify bytes-as-received before JSON parse | HMAC-SHA256 |
| `fpr` device binding | `identifier + ":" + product_id` as UTF-8 | none (string concat is specified) | SHA-256, first 8 bytes |
| Key normalization (hashing) | `UPPER(TRIM(key))` UTF-8 before SHA-256 | specified trim+case-fold | SHA-256 |

**RFC 8785 (JCS) is deliberately NOT required**: every construction signs either (a) a
pre-encoded transport segment, (b) raw bytes, or (c) an HMAC over raw bytes. No verifier
ever re-serializes JSON to produce a signature. This removes the classic cross-language
canonicalization bug class from the protocol entirely — a stated design win, to be
protected: **never sign "the JSON object" in the new designs above; sign the transported
bytes.**

Base64 dialects (avoid the other classic bug): tokens/revocations = **base64url
unpadded** (accept padded leniently); artifact signatures = **standard base64 (padded)**
because Sparkle/Tauri ecosystems expect it; keys/digests/ids = **lowercase hex** (pubkey
endpoint historically hex — keep `format:"hex"`).

---

## 6. Standards profile (what a client must support)

Required for offline validation: HTTPS + JSON parsing + **Ed25519 verify (RFC 8032)** +
base64url decode + SHA-256. Optional: HMAC-SHA256 (webhook *receivers* only), JWS verify
(only if server migrates to `jws-eddsa` issuance).

Wire standards honored: RFC 8446 (TLS 1.2+/1.3 via platform stack), RFC 7231 semantics +
RFC 6585 statuses used (409/413/429), RFC 9110 field usage (`Retry-After`), RFC 3339
timestamps, RFC 4648 §5, RFC 6979 not needed (Ed25519 is deterministic — note: signatures
are deterministic by design, so signature ≠ randomness oracle concerns don't apply).

Header/protocol additions proposed (all optional): `Deprecation`, `Link: successor`,
`X-Keygate-Event` (exists), `Idempotency-Key` (exists), `Idempotent-Replayed` (exists).

---

## 7. Key management design

### 7.1 Key inventory & storage (target state)

| Key | Generation | Storage (target) | Distribution | Lifetime |
|---|---|---| --- |---|
| `LICENSE_SIGNING_KEY` (token/revocation signer) | `openssl rand -hex 32` seed → `ed25519.NewKeyFromSeed` | server env/secret manager; **target**: rows in `license_signing_keys`, ciphertext under master key | public half only via `/license/pubkey` (+`kid`) | target: rotatable with overlap epochs (R-08); today: rotate = invalidate all existing tokens on next fetch |
| Product artifact keys | `ed25519.GenerateKey(rand.Reader)` server-side | DB, AES-256-GCM, **AAD=product_id**; seeds zeroed after use (`zeroBytes`) | PEM / raw b64 / Tauri envelope; admin downloads today, proposed public discovery | active/retired rotation existing (`/signing-key/rotate`, single-active partial index) |
| `RELEASE_KEY_ENCRYPTION_KEY` (master) | 32 bytes hex | env only; **never** derived, logged, or sent | never leaves server | loss ⇒ permanent signing-key loss (documented); change ⇒ re-encrypt required (runbook gap → R-13) |
| `JWT_SECRET` | ≥32 chars (boot-fatal if shorter) | env | never | rotate ⇒ all sessions drop (acceptable, refresh flow re-auths) |
| API keys `kg_live_` | 24 random bytes | SHA-256 in DB | shown once at mint | rotate = instant revoke (no grace — by design) |
| Webhook secrets | 32 random bytes hex per webhook | DB plaintext (it's a *shared* secret with the receiver) | shown at creation | receiver-side rotation pattern exists via Stripe prev-secret drain precedent |
| SMTP/API secrets in settings | operator input | AES-GCM `enc:v1:` prefix, **write refused without master key** | never returned to UI | rotation via settings endpoints (audit `secret_cleared`) |

Entropy policy: all randomness from `crypto/rand`; license keys 160 bits; API/refresh/invite
tokens ≥192 bits; no `math/rand` anywhere (verified by construction review of these files).

### 7.2 Recommended rotation choreography (fits §protocol-versioning epochs)

1. Mint & publish new `kid` (kept `active` alongside old → `retired`).
2. Start issuing tokens carrying `kid` (JWS) or fetch-key lists covering both keys (legacy
   clients re-fetch pubkey every ≤1 h ⇒ they naturally pick up the union snapshot).
3. Wait `max(plan.token_ttl_days) + slack` so any client offline at step 1 re-fetches.
4. Retire old key (still listed as `retired` until its last possible `exp` passes).
5. Reject old `kid` only after step 4 completes — deterministic, never "silently
   unverifiable" (threat-model req #6).

Compromise response matrix: token key → immediate generation-2 rotation *without*
waiting (forged tokens under old kid die when clients re-fetch — bounded by the 1 h cache;
document the ≤1 h window); artifact key → rotate + optional re-sign + publish with
`minimum_supported_version` floor if malicious历史上 published; master key → depends on
exposure: re-encrypt all rows (`license_key_encrypted`, `private_key_encrypted`,
`settings`) via offline job, keep old key as AAD-stable input only (AADs are ids, not keys
— unaffected); JWT secret → rotate + force re-login (already DB role-checked per request).

### 7.3 Client key-pinning guidance (SDK convenience, not protocol)

Pin the **license signing public key(s)** (offline trust anchor) — survive TLS cert renewal;
do not pin TLS certs (self-hosted servers change CAs); persist last `keys[]` snapshot with
a fallback to the bootstrap key embedded at build time; refuse to start if *both* the fetch
and the snapshot are empty — but only *warn*-degrade on verification of an unexpired
previously-verified cache (availability vs. safety balance documented per-SDK).

---

## 8. Out of scope / explicitly not done (anti-requirements)

- No homegrown cipher, KDF, Padding-OK-MAC constructions, XOR "obfuscation", or custom
  nonce schemes (GCM nonces: random 12-byte per encryption; never counter-shared).
- No client-side encryption requirement (data at rest on client is *cache*, integrity
  protected only where we also ship keys).
- No claim of token revocability within issuance window beyond the documented `exp` bound +
  proposed revocations list (S1, threat T11).
- No encryption of license keys *from their owner* — the key is the owner's credential;
  at-rest encryption protects the DB against outsiders, not against DBAs (S2 row remains
  honest until Phase C).
- No algorithm agility beyond a closed allowlist (`EdDSA`, `HS256` sessions) — "algorithm
  agility" negotiation strings in tokens are rejected (downgrade risk); format choice
  happens by public-key-fetch, never by attacker-controlled token header value, and `alg`
  must be allowlisted by every client verifier (JOSE's classic confusion defense).