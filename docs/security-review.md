# Security Review — Keygate (security-enhancements)

**Reviewer role:** independent application security reviewer.
**Subject:** commit `1ec0c4e` on branch `security-enhancements` (working tree was
clean at the start of the review), i.e. the five commits that add license
activation / device records, signed integrity manifests, offline licenses and
key rotation, the Python SDK, and the admin device-management UI.

**Date:** 2026-10-18.

**Method:** reading the implementation, database migrations, routes, middleware,
Docker configuration and tests; then executing the suites that could be made to
run locally. Nothing was run against a production or third-party system. No
destructive tests were performed.

**Environment actually used for execution:** macOS arm64, Go `1.27.2`
(downloaded to `/tmp`, not installed system-wide), PostgreSQL `18.6` (Homebrew,
private cluster under `/tmp/kgpg`, port `55432`), Python `3.9.6`, Node
`24.18.0`. Docker was **not** available (`cannot connect to the Docker daemon`)
and Bun was **not** installed, so image builds and frontend typecheck/lint did
not run.

**Statement this review does not make:** the project is *not* declared secure or
production-ready. Findings below are limited to what was inspected and executed;
absence of a finding is not evidence of absence.

---

## 1. Executive summary

| # | Severity | Finding | Status |
|---|---|---|---|
| F-1 | **High** | The branch as committed did not compile; `go build`, `go vet` and every test were unrunnable | Fixed |
| F-2 | **High** | CI never executes the new activation/device integration tests, and skips 85 of 97 store tests silently | Reported (config change recommended) |
| F-3 | **High** | Identifier whitespace was trimmed for validation but stored raw, letting one machine hold several activation slots | Fixed + regression test |
| F-4 | **Medium** | Integrity verifier's path jail is bypassable with a symlinked directory; Go passed where the Python SDK correctly rejected | Fixed + regression test |
| F-5 | **Medium** | Python SDK accepted unknown signing-key IDs and unsupported manifest versions | Fixed + regression tests |
| F-6 | **Medium** | No domain separation between signed license tokens and signed manifests: a manifest verifies as a license token | Reported (protocol change needed) |
| F-7 | **Medium** | `device-identity.md` documents server-side drift handling, proof-of-possession and "clone immunity" that do not exist in the server | Reported (doc change needed) |
| F-8 | **Medium** | License keys remain in plaintext in the database; `db-backup` output was not git-ignored | Partly fixed (ignore rules); column drop still outstanding |
| F-9 | **Medium** | Dev-login gate keys off configuration (env + `BASE_URL`), not the request; any email incl. existing owner can authenticate on an exposed dev instance | Reported (design decision needed) |
| F-10 | **Medium** | Five reachable `golang.org/x/net` vulnerabilities (`GO-2026-6603/6610/6611/6612/6617`) | Fixed; `govulncheck` clean |
| F-11 | Low | Unauthenticated `/metrics` and `/health` disclose operational data / raw DB errors | Reported |
| F-12 | Low | Cookie-authenticated state changes rely on `SameSite=Lax` alone — no CSRF token or `Origin` assertion on `/portal` and `/admin` mutations | Reported |
| F-13 | Low | Docker image runs as root; compose DB uses a fixed password with TLS disabled (internal network only) | Reported |
| F-14 | Low | Integrity docs specify signing over decoded payload bytes; every implementation signs the base64 string | Reported (doc change needed) |
| F-15 | Low | Python SDK hand-rolls Ed25519 instead of using `cryptography` even when installed | Reported |
| F-16 | Low | Session JWT parsing does not pin `HS256`; `SessionAuth` falls back to the JWT admin claim when no checker is supplied | Reported |
| F-17 | Low | Offline verifiers fail open when `exp` / `did` / `fpr` are absent | Reported |
| F-18 | Low | Device transfer via `previous_identifier` is authorized by the license key alone (audited, not proven) | Reported |
| F-19 | Info | New lifecycle test was red because it activated four identifiers on a three-slot licence | Fixed (test) |
| F-20 | Info | Zero-value `max_activations` means "no slots" in activation but "unlimited" for seats | Reported |
| F-21 | Info | Email-queue test assumes the mail it just enqueued is processed, but the queue is shared and non-reset; it passes only while the backlog is under the 20-mail batch | Reported (test-isolation fix needed) |

No Critical finding was confirmed. The three High items are: the security controls
not being exercised at all on this branch (F-1, F-2) and one confirmed logic defect
in slot accounting (F-3). None is remote code execution or a data breach.

---

## 2. Findings

### F-1 (High, fixed) — The branch did not compile

**Evidence.** On a clean checkout:

```
$ go build ./...
pkg/integrity/scanner.go:9:2: "strings" imported and not used
internal/handler/license.go:6:2: "strings" imported and not used
cmd/keygate-manifest/main.go:5:2: "encoding/base64" imported and not used
cmd/keygate-manifest/main.go:11:2: "path/filepath" imported and not used
$ go vet ./...
internal/license/offline_test.go:5:2: "crypto/rand" imported and not used
pkg/deviceid/deviceid_test.go:8:2: "fmt" imported and not used
internal/handler/license_activation_test.go:593:3: undefined: strings
```

`ci.yml` runs `go vet ./...` and `go test ./...`, so this branch could not have
passed its own pipeline. Every statement of the form "the tests pass" about this
branch was therefore unverified at the time of review — including the security
tests that the feature depends on.

**Fix applied.** Removed the four unused imports, added the missing `strings`
import to the test. Build, vet and the full suite now pass (section 5).

**Why it matters.** A licensing control that is never compiled is a control
nobody has run. This is a release-integrity finding rather than a runtime
vulnerability.

---

### F-2 (High, reported) — CI does not run the tests that matter for this feature

**Evidence.** `.github/workflows/ci.yml` provides a Postgres service, then runs:

```yaml
- name: Unit tests
  run: go test ./... -count=1            # no TEST_DATABASE_URL → integration tests skip
- name: Integration tests
  env: { TEST_DATABASE_URL: ... }
  run: go test -count=1 -run "Atomic" ./internal/store/
```

Measured locally without `TEST_DATABASE_URL`:

- `internal/store`: **85 of 97** tests skip (`skipping integration test: TEST_DATABASE_URL not set`).
- `internal/handler`: **43 of 66** tests skip.

The `-run "Atomic"` filter then excludes, among others,
`TestConcurrentActivationFromOneMachine`, `TestConcurrentActivationRespectsTheLimit`,
`TestActivationLifecycle_*`, `TestConcurrentCheckOut*`, `TestLastOwner*`, and the
whole `internal/handler` activation/portal suite. The Python suites under `test/`
and `sdk/python/tests/` and the frontend `node --test` suite are not run at all.

**Why it matters.** The activation-limit and concurrency invariants are the core
of the new licensing feature. They are only observable against a database, and CI
never runs them. F-3 and F-19 survived merge for exactly this reason.

**Recommended fix.**

```yaml
- name: All tests, with database
  env: { TEST_DATABASE_URL: postgres://keygate:keygate@localhost:5432/keygate?sslmode=disable }
  run: go test ./... -count=1
- name: Concurrency under the race detector
  env: { TEST_DATABASE_URL: ... }
  run: go test -race -count=1 -run "Concurrent|Atomic|Race" ./internal/store ./internal/handler ./internal/middleware
- name: Python suites
  run: |
    python3 test/integrity_test.py
    python3 test/offline_token_test.py
    python3 test/device_identity_test.py
    python3 -m unittest discover -s sdk/python/tests
- name: Vulnerabilities
  run: |
    go install golang.org/x/vuln/cmd/govulncheck@latest
    govulncheck ./...
```

---

### F-3 (High, fixed) — Identifier padding escaped validation and burned extra slots

**Evidence.**

`pkg/apperr/validate.go` (before):

```go
func ValidateIdentifier(identifier string) *AppError {
    identifier = strings.TrimSpace(identifier)   // validates a different string
    ...
    if err := deviceid.ValidateIdentifierFormat(identifier); err != nil { ... }
```

`pkg/deviceid/deviceid.go` (before) also trimmed. The handler then passes the
**raw** `req.Identifier` to the service, which stores it and looks activations up
with an exact match (`WHERE license_id = ? AND identifier = ?`). So
`"550e8400-…"`, `"550e8400-…\n"` and `"550e8400-… "` were three distinct active
rows for one physical machine.

The repository's own test already asserted this must fail:

```
--- FAIL: TestDeviceID_InvalidIdentifierFormats/contains_newline
    deviceid_test.go:254: expected validation error for "device-id\n", got nil
```

(This failure was invisible until F-1 was fixed.)

**Impact.** A single machine can occupy several activation slots on a shared
license by re-sending its identifier with different padding — up to the plan cap.
Each spelling also defeats `already_activated` idempotency, splits the audit
trail across rows, and can lock other devices of the same license out of the
remaining slots. The API documentation claims the opposite
(`docs/device-identity.md` §8: "Strict rejection of control characters, whitespace
… with HTTP 400").

**Fix applied.** Validate the *canonical* string instead of silently trimming it:

- `deviceid.ValidateIdentifierFormat` rejects any identifier whose trimmed form
  differs from the input.
- `apperr.ValidateIdentifier` only treats whitespace-only input as "missing"; it
  no longer normalizes before validating.

**Regression tests added.**

- `pkg/deviceid/deviceid_test.go: TestDeviceID_RejectsSurroundingWhitespace`
- `pkg/apperr/validate_test.go: TestValidateIdentifierRejectsNonCanonicalSpelling`
- `internal/handler/license_activation_test.go: TestLicenseActivation_PaddedIdentifierIsRejectedWithoutTakingSlot`
  (asserts 400 for `\n`, leading space and trailing space, **zero** rows created
  via `CountActivations`, then 200 for the canonical identifier)

---

### F-4 (Medium, fixed) — Integrity path jail bypassed by a symlinked directory

**Evidence / reproduction.** `VerifyInstalledFiles` only called `EvalSymlinks`
when `Lstat` reported a symlink at the *final* component. `Lstat("root/sub/a.txt")`
follows `sub`, so a symlinked directory in the middle of the path was invisible.

```bash
mkdir -p /tmp/igtest2/root/sub && echo protected > /tmp/igtest2/root/sub/a.txt
keygate-manifest create --dir /tmp/igtest2/root ... --out /tmp/igtest2/manifest.json
mv /tmp/igtest2/root/sub /tmp/igtest2/outside
ln -s /tmp/igtest2/outside /tmp/igtest2/root/sub
keygate-manifest verify --manifest /tmp/igtest2/manifest.json --pubkey … --dir /tmp/igtest2/root
# → INTEGRITY VERIFICATION PASSED   (the protected file now lives outside the root)
```

The Python SDK on the same tree returned
`ManifestVerificationError [JAIL_ESCAPE] path escapes directory jail: 'sub/a.txt'`.
The Go tool and the reference SDK disagreed on a security boundary.

**Impact.** Verification can pass while the protected content is outside the
designated installation root, contradicting `docs/integrity-verification.md`
Step 4. Because the bytes must still match the signed digest, this is not an
integrity forgery; it is a confinement and consistency failure (relocation of
authentic content, reading outside the root, inconsistent client behaviour).

**Fix applied** (`pkg/integrity/verifier.go`):

- resolve the installation root once (`EvalSymlinks`) so hosts where the root is
  itself a symlink (`/tmp → /private/tmp`) do not fail closed incorrectly;
- resolve the **full** target path and require it to remain inside the resolved
  root before hashing;
- reject non-regular files (a FIFO named like a protected file would block
  hashing indefinitely).

**Regression test:** `pkg/integrity/integrity_test.go:
TestIntegrity_SymlinkedDirectoryCannotEscapeRoot` (baseline verifies, escaped
tree must not).

---

### F-5 (Medium, fixed) — Python SDK accepted unknown keys and unknown manifest versions

**Evidence (reproduced before the fix).** With a validly signed envelope:

1. Changing `signing_key_id` to `key_attacker_unknown` still verified: the
   verifier fell back to `default_kid` (the first key in the keyring) whenever the
   envelope's ID was unknown. Signature was valid, so acceptance followed.
2. Re-signing a payload with `"manifest_version": 2` was accepted; the parser
   never validated the version and read the payload on a best-effort basis.

Both violate the review criteria "unknown signing keys are rejected" and
"manifest versions … handled safely". The Go verifier rejects both.

**Fix applied** (`sdk/python/keygate/manifest.py`):

- a keyring is a set of trusted IDs: an unknown ID is now `UNKNOWN_SIGNING_KEY`;
- `single_pubkey=` is treated as an explicit pin and remains supported (the key
  is the anchor; a tampered envelope still fails the payload/envelope ID match);
- `manifest_version` is validated (accepts `1`, `"1"`, `"1.0"`) at both envelope
  and payload level;
- payload `signing_key_id` must match the envelope, as in Go.

**Regression tests:** four cases in
`sdk/python/tests/test_sdk.py::TestManifestEnvelopeHardening`.

**Verified not to break interop:** a manifest produced by the Go CLI still
verifies under the Python script, the pinned SDK verifier and the keyring SDK
verifier (section 5).

---

### F-6 (Medium, reported) — No domain separation between license tokens and manifests

**Evidence.** Both formats sign `base64url(payload)` with Ed25519 and no
type/audience prefix. A probe (temporary test, removed after execution):

```
ACCEPTED manifest envelope as licence token:
{"lid":"","pid":"","pln":"","sts":"","did":"","iat":0,"exp":0,"grc":0,"nce":""}
```

`license.Verify` returned success for `manifest.payload + "." + manifest.signature`
when the same key was supplied.

**Impact.** Only reachable if one Ed25519 key is used both as `LICENSE_SIGNING_KEY`
and as a manifest/release signing key, and the consumer trusts a bare successful
`Verify`. In-tree, the one consumer (`release_public.go` feed path) then looks up
`FeedCutoffForToken` by the empty `lid` and 404s, so no in-tree exploit was
demonstrated. It is still a protocol weakness that becomes exploitable the moment
two keys are shared or a new consumer checks only the signature.

**Recommended fix (breaking, not applied).** Prefix the signed bytes with a
domain tag (`keygate:license:v1:` / `keygate:integrity:v1:`), or add a mandatory
`typ` claim that verifiers check. Until then: document that these keys must never
be shared, and require consumers to validate `pid`, `exp`, `sts`, and features.

---

### F-7 (Medium, reported) — Device-identity documentation describes controls the server does not implement

**Evidence.**

- `docs/device-identity.md` §5.2 states server decisions: score ≥ 60 "slot retained
  in place", 30–59 "in-place rebind … audit warning `device_drift_rebound`",
  < 30 rejected.
- §6.2 documents proof-of-possession: `device_pubkey` sent to
  `POST /license/activate`, challenge/response, and "Clone Immunity".
- §8 claims the tests validate hardware drift, cloned-state detection and
  "Ed25519 signature verification over challenge nonces".

**Actual code.** A repository-wide search shows `EvaluateDrift`,
`VerifyProofOfPossession`, `ComputeCompositeDigest` and `HashComponent` are
referenced **only by tests**. There is no drift evaluation at activation, no
`device_pubkey`/challenge in `Activate`, and the string `device_drift_rebound`
does not exist. `Activate` has no field for a device public key, so the
documented parameter is silently ignored by JSON binding.

**Impact.** An integrator reading this document believes cloned disk images are
denied and that hardware drift is handled in place. In reality a changed
fingerprint is simply a new identifier: it takes a new slot, and at the cap it is
refused with `ACTIVATION_LIMIT` — exactly the "spurious 409" the document promises
to prevent. Cloned client state *is* in the threat model
(`docs/threat-model.md` T4, R-17), but `device-identity.md` presents the same
topics as implemented.

**Recommended fix (documentation; no code change made).** Either implement
activate-time drift/rebind and PoP, or mark §5.2/§6.2/§8 as *proposed* and state
plainly that identifiers are client claims with no proof of possession.

---

### F-8 (Medium, partly fixed) — License keys remain in plaintext; backups were committable

**Evidence.**

- `db/migrations/20260320_init.up.sql:85`: `license_key TEXT NOT NULL UNIQUE`
  still exists; no migration drops it.
- `prepareLicenseForInsert` writes the plaintext column and, when a master key is
  configured, additionally stores `license_key_encrypted`.
- `FindLicenseByKey` looks up by `key_hash` first and **falls back** to a plaintext
  comparison for un-migrated rows.
- `make db-backup` writes `backup-YYYYMMDD-HHMMSS.sql` into the working directory,
  and `.gitignore` did not exclude it.

**Impact.** With `RELEASE_KEY_ENCRYPTION_KEY` set, the encrypted copy is additive:
a database dump or backup still contains every customer's license key in clear
text, plus backups that can be committed or copied into a Docker build context by
accident.

**Fix applied (partial).** Added `backup-*.sql` to `.gitignore` and
`.dockerignore`.

**Outstanding.** Complete the documented Phase B/C (backfill, then drop
`license_key`), and remove the plaintext fallback so a key can never be compared
in the clear. At minimum, state the plaintext-at-rest property explicitly in the
security documentation instead of implying hashing/encryption covers the key.

---

### F-9 (Medium, reported) — Dev login is gated by configuration, not by the request

**Evidence.** `cmd/server/main.go:834` registers `POST /api/v1/auth/dev-login`
unconditionally; `AuthHandler.DevLogin` (`internal/handler/oauth.go:202`) allows
the call when `ENVIRONMENT=development` **and** `BASE_URL` contains `localhost` or
`127.0.0.1`. The caller's address is never inspected. The handler upserts the
supplied email and issues a session with that user's current role.

**Impact.** A development instance that is reachable from a network — a container
publishing `0.0.0.0:9000`, a laptop on a shared network, a dev VM — lets anyone
obtain a session for any email, including an existing owner/admin, without a
password or OTP. `.env.example` ships `ENVIRONMENT=development`; the compose file
pins `ENVIRONMENT: production`, which mitigates the documented path.

**Recommended fix.** Require a loopback `RemoteAddr` (and no
`X-Forwarded-For` from an untrusted peer) in addition to the config checks, or
remove dev-login and rely on `ADMIN_EMAILS` + OTP in development too. Not applied:
it is a product decision that changes local workflows.

---

### F-10 (Medium, fixed) — Reachable dependency vulnerabilities

`govulncheck ./...` on the branch:

```
GO-2026-6603  HTTP/2 server memory exhaustion (Trailer headers)
GO-2026-6610  HTTP/2 transport accepts malformed framing headers
GO-2026-6611  Excessive CPU from repeated initial window changes
GO-2026-6612  (golang.org/x/net, fixed in v0.60.0)
GO-2026-6617  (golang.org/x/net, fixed in v0.60.0)
Found in: golang.org/x/net@v0.59.0     Fixed in: v0.60.0
Your code is affected by 5 vulnerabilities from 1 module.
```

Traces reach the HTTP server and outbound `http.Client` paths. **Fix applied:**
`golang.org/x/net v0.59.0 → v0.60.0`; `govulncheck` now reports
"No vulnerabilities found" (one module-level advisory in a package that is not
called remains, as reported by the tool).

Dependency licences: all direct modules are permissive (MIT/BSD-2/BSD-3/Apache-2.0);
no GPL dependency was found. This was reviewed from `go.mod` only — no automated
licence scan was run.

---

### F-11 (Low) — Unauthenticated metrics and health endpoints

`cmd/server/main.go:638` serves `/metrics` with no authentication, exposing
per-route request counts and `product_id` labels (`LicenseActivations`,
`LicenseVerifications`, webhook/email counters). `/health:640` returns the raw
database error string (`"error: " + err.Error()`), which can include host names,
roles and database names.

**Recommendation.** Serve metrics on an internal listener or behind admin auth;
return a generic `"database": "error"` in `/health` and log the cause.

---

### F-12 (Low) — CSRF depends on `SameSite=Lax`

Session and refresh cookies are `HttpOnly`, `Secure` when the request used TLS,
and `SameSite=Lax` (`setSecureCookie` in `internal/handler/oauth.go`), which stops
cross-site `POST` requests from carrying the cookie. There is no CSRF token and
no server-side `Origin`/`Sec-Fetch-Site` assertion on `/portal/*` or `/admin/*`
mutations; the CORS middleware only controls whether a browser may *read* a
response, and in production it does not add `Access-Control-Allow-Origin` for a
foreign origin at all. This is adequate against ordinary CSRF today, but it is a
single attribute standing between the API and universal same-site request
forgery. If the session cookie ever needs `SameSite=None` (cross-site embedding),
a token or an `Origin` check must be added at the same time.

---

### F-13 (Low) — Container hardening and default credentials

- `Dockerfile` has no `USER`; the server runs as root inside the image.
- `docker-compose.yml` runs Postgres with `POSTGRES_PASSWORD: keygate` and
  `sslmode=disable`. The database is not published to the host (`postgres` has no
  `ports:`), so this is an internal-network exposure, not an internet one.
- No `read_only`, `cap_drop`, or resource limits are set.

**Recommendation.** Add a non-root `USER`, generate the DB password from the
environment, and consider `read_only: true` plus `tmpfs` for the app container.

---

### F-14 (Low) — Integrity specification contradicts every implementation

`docs/integrity-verification.md` Step 2 and the flow diagram say to verify the
signature over the **decoded payload bytes**, and Step 3 requires
`manifest_version == "1.0"`. The Go signer/verifier
(`pkg/integrity/signer.go`, `verifier.go`) signs and verifies the **ASCII bytes of
the base64 payload string** with `manifest_version` as the integer `1`; the Python
script and SDK match Go. An implementer following the document literally cannot
verify a real manifest.

**Recommendation.** Correct the document; add the cross-implementation vector
(Golang CLI → Python verifier) to CI so drift is caught mechanically. Interop was
verified manually during this review (section 5).

---

### F-15 (Low) — Hand-rolled Ed25519 in the Python SDK

`sdk/python/keygate/offline.py` and `manifest.py` use
`_ed25519_verify_pure`, a pure-Python RFC 8032 implementation, even when the
`cryptography` package is installed. It rejects `S >= L` (good) but does not
reject small-order public keys; this is not exploitable when keys are pinned, but
it is a large amount of security-critical arithmetic to own. **Recommendation:**
prefer `cryptography` when importable, keep the pure fallback only for
dependency-free environments, and add RFC 8032 test vectors (including
small-order points).

---

### F-16 (Low) — JWT hardening gaps

`internal/middleware/auth.go`:

```go
jwt.ParseWithClaims(raw, claims, func(*jwt.Token) (any, error) { return []byte(secret), nil })
```

No `jwt.WithValidMethods([]string{"HS256"})`. The current design is not forgeable
(`none` is rejected by the library, and the key is an HMAC secret), but pinning the
algorithm prevents a future algorithm-confusion regression. `SessionAuth` also
falls back to `claims.IsAdmin` when no `AdminChecker` is supplied; every current
call site passes `db.FindUserIsAdmin`, but the fallback is a single-call-site
mistake away from trusting a client-supplied admin claim. **Recommendation:** pin
the method and remove the claim fallback (fail closed).

---

### F-17 (Low) — Offline verifiers fail open on missing claims

Both `internal/license/token.go` and `sdk/python/keygate/offline.py` skip a check
when the corresponding claim is absent or zero:

- `exp == 0` → no expiry check (token never expires);
- `did` / `fpr` absent → device binding silently skipped even when the caller
  passed `expected_identifier`;
- `vun == 0` → treated as perpetual.

The server always sets these fields, so this is defense in depth. **Recommendation:**
require `exp`/`iat` to be present, and reject a token whose `did`/`fpr` are missing
when an expected identifier was supplied.

---

### F-18 (Low) — Device transfer is authorized by the license key alone

`service.Activate` migrates an activation in place when the caller supplies
`previous_identifier` (`internal/service/license.go:139`). The only authorization
is possession of the license key and knowledge of the old identifier, which the
portal exposes to the owner and to accepted seats. A holder of the key can
therefore rename another device's activation to their own identifier, evicting
that device (its `/license/verify` then returns 404). The operation is audited
(`action: "migrated"`, with IP), so it is *auditable* but not *proven*.

**Recommendation.** Bind the migration to the caller (e.g. require a signed token
issued for the previous identifier, or a portal session for that license) and
notify the displaced device.

---

### F-19 (Info, fixed) — A newly added test was red against a real database

`internal/store/activation_lifecycle_test.go` created a licence with
`max_activations = 3` and then activated four identifiers in four subtests
without deactivating any. The fourth subtest failed with
`activation limit reached` — the limit behaving correctly, and the test being
wrong. It only surfaced with a database (`TEST_DATABASE_URL`), which is why F-2
matters. **Fix applied:** size both the licence cap and the in-loop
`ActivateWithinLimit` argument to the table length; assertions unchanged.

---

### F-20 (Info) — Zero-value activation cap is inconsistent

`AddSeatWithinLimit` treats `maxSeats <= 0` as "no limit";
`ActivateWithinLimit` treats `maxActivations <= 0` as zero capacity
(`count >= max` is true immediately). Activation for a SaaS product is gated by
the capability check before reaching this code, so the reachable failure mode is a
misconfigured desktop plan refusing every activation. It fails closed. Worth
aligning/documenting rather than leaving two meanings for zero.

### F-21 (Info, reported) — The email-queue test depends on a shared, never-reset queue

`TestQueueMarksPermanentFailureTerminal` enqueues one mail, calls
`svc.processQueue(ctx, db)` once and then asserts that **that** row is terminal
(`status = 'failed'`, body wiped). Two facts in the product make that assumption
unsound on a reused database:

- `ClaimNextEmail` serves the single oldest due pending mail
  (`ORDER BY created_at ASC LIMIT 1`, `internal/store/store.go:2261`), and
  `processQueue` claims at most `emailQueueBatch = 20` mails per call
  (`internal/service/email.go:1038`).
- Tests share one database and never clear `email_queue`. Measured after a few
  suite runs: **85 pending rows** with an earliest `next_retry` already in the
  past.

Once 20 older mails are due, the batch is consumed by them and the test's own
mail is never sent, so the assertion fails. Observed behaviour matches: the suite
passed on a fresh database and on an early run, then failed on later runs — with
`-p 1` as well as with parallel packages, so this is not (only) package
parallelism. The temporary failure line is:

```
--- FAIL: TestQueueMarksPermanentFailureTerminal (0.03s)
    permanent failure should be terminal (failed), got "pending"
```

This is a test-isolation defect, not a product defect: oldest-first batch sending
and the claim lease are intended behaviour.

**Recommendation.** Give the test its own queue (truncate `email_queue` in setup,
or run each package against a fresh database/schema), or assert by waiting for its
row to leave `pending`. This must be fixed before F-2 makes a full-suite database
run a required CI gate, or the gate will be flaky and its failures ignored.

---

## 3. Verification matrix (requested areas)

Legend: **V-code** verified by reading the implementation; **V-test** verified by
executing tests locally; **V-repro** demonstrated with a reproduction.

### 3.1 License enforcement

| Claim | Result | Evidence |
|---|---|---|
| Activation limits enforced server-side | Yes | `Store.ActivateWithinLimit` counts under `SELECT … FOR UPDATE` on the licence row (`internal/store/store.go:2074`) |
| Concurrent requests cannot bypass limits | Yes | **V-test** `TestConcurrentActivationRespectsTheLimit` (12 contenders, max 3) and `TestConcurrentActivationFromOneMachine` pass against Postgres 18.6, also under `-race` |
| Transfers cannot create duplicate active devices | Yes | Transfer deactivates active rows in the same transaction (`internal/store/admin.go:751`); identifier migration keeps one row per `(license_id, identifier)` (unique constraint), verified by `TestActivationLifecycle_MigrationAndRollback` |
| Revoked/expired licenses rejected | Yes | `assertUsable` covers active/trialing/past_due, canceled, suspended, revoked, expired; activation returns the specific code, verify collapses to 404 (`internal/service/license.go`) |
| Administrative actions require authorization | Yes (with caveats) | `/admin` groups are session-or-API-key + `RequireScope`; admin status read from the DB per request (`db.FindUserIsAdmin`). Caveats: product-scope helper passes when the key has no `product_id` (system-wide key, by design), and `licenses:write` keys can reveal keys (F-8/F-18 note) |
| No sensitive data in logs/errors | Mostly | `response.Internal` never echoes the cause; log formatter redacts `license_key`/`license_token` query params and the `/api/v1/portal/licenses/<key>/` path segment; `cmd/server/log_redaction_test.go` passes. Exception: `/health` leaks DB error text (F-11) |

### 3.2 Device identity

| Claim | Result | Evidence |
|---|---|---|
| Identifiers treated as claims, not proof | Yes, documented | `docs/threat-model.md` T4 ("permanently a client claim"); `device_pubkey` not implemented (F-7) |
| Missing identifiers handled safely | Yes | `ValidateIdentifier` rejects empty; handler binds `required`; store queries parameterized |
| Fingerprint changes do not silently bypass policy | **No policy exists** | Drift scoring is dead code (F-7). A changed fingerprint is a new identifier and consumes a new slot; at the cap it is refused. Not a silent bypass, but not the documented rebind either |
| Cloned client state in threat model | Yes, but contradicted | `threat-model.md` T4/R-17 vs `device-identity.md` §6.2 "Clone Immunity" (F-7) |
| Device-transfer operations authorized and auditable | Auditable, weakly authorized | Audit row `migrated` with from/to/scheme/IP; authorization is the license key + knowledge of the old identifier (F-18) |

### 3.3 Cryptography

| Claim | Result | Evidence |
|---|---|---|
| Established formats | Yes | Ed25519 (RFC 8032), AES-256-GCM with HKDF-derived subkeys and AAD binding (`internal/crypto`), SHA-256 |
| Private signing keys absent from clients | Yes | License signing is server-side; release private keys AES-GCM encrypted at rest, AAD = product id; `json:"-"` on private material |
| Invalid signatures rejected | Yes | Go token/verifier and Python verifier tests (mutated payload, wrong key, truncated signature) |
| Unknown signing keys rejected | Go yes; Python **was no** | Fixed in F-5 |
| Key rotation correct | Partly | Release keys rotate atomically (single active key, advisory lock, old keys stay listed). License signing key rotation is an env-var swap: `/license/pubkey` returns exactly **one** key, while `docs/offline-licensing.md` claims it serves "the union of the active key and all recently retired keys". Clients holding an older key cannot verify newly issued tokens until they re-fetch |
| Offline revocation limitations documented | Yes | `docs/offline-licensing.md` §4.1, `threat-model.md` T11: an offline client stays valid until `exp` |

### 3.4 Integrity manifests

| Claim | Result | Evidence |
|---|---|---|
| Signature checked before file entries trusted | Yes | `VerifyEnvelope` verifies the signature, then unmarshals; callers use the returned manifest. Note: `SignedEnvelope.Manifest` exists as an **unverified** convenience field — no in-tree consumer reads it after verification, but it is a trap for new callers |
| Digests computed correctly | Yes | Streaming SHA-256 + size; `HashFile`; cross-checked against Python + `sha256sum` expectations in tests |
| Paths cannot escape the root | **Was no**, now fixed | F-4 |
| Missing/modified files detected | Yes | `TestIntegrity_MissingFileDetection`, `TestIntegrity_ModifiedFileDetection`; non-regular files now rejected as well |
| Canonical serialization unambiguous | Mostly | Struct field order is fixed and Go sorts map keys; duplicate `path` entries are not rejected and `sort.Slice` is not stable, so two entries for the same path have unspecified order in the canonical bytes. Verification does not re-canonicalize, so this is not a verification bypass, but reject duplicates |
| Manifest versions and key rotation handled safely | Go yes; Python **was no** | Go rejects unknown `manifest_version` and unknown key IDs; fixed in F-5 for Python |
| Docs distinguish integrity checking from runtime protection | Yes | `docs/integrity-manifest-format.md` §3/§276 explicitly states static hashes cannot prevent memory patching, `ptrace`, Frida, etc. — this part is accurate |

### 3.5 API and deployment

| Area | Result |
|---|---|
| Authentication | JWT HS256 in an HttpOnly `SameSite=Lax` cookie (Secure follows the actual scheme), refresh tokens hashed and rotated with reuse detection. API keys hashed, scoped, per-product optional (F-16 hardening note) |
| Authorization | `RequireScope` + `AdminOnly` + per-product API-key checks; portal guards verify licence ownership or accepted seat. Memory-exhaustion caps on portal bodies (64 KiB) |
| Input validation | Identifier/email/slug/URL/semver validators; sort columns resolved through an allowlist; no string-concatenated SQL found (`NewRaw` calls are parameterized) |
| Rate limiting | Per-IP, per-scope buckets on `/license`, `/auth`, `/otp/send`, feeds, webhooks; licence brute-force guard with exponential backoff. In-memory by default, Redis with breaker fallback; multi-replica lockouts are per-instance (documented residual) |
| Secret management | Secrets encrypted at rest with the master key, never returned by `GET /admin/settings` (`secrets_set` only); LICENSE key encryption optional and additive (F-8) |
| DB transactions | Activation, seats, floating, usage, transfer, refunds, renewals all serialize on row/advisory locks; `-race` concurrency tests pass |
| Docker | Multi-stage, CGO disabled, static frontend; runs as root (F-13); no secrets baked in; `.dockerignore` excludes `.env*`, `node_modules`, `dist` |
| Default credentials | No application default password; setup wizard is guarded by `ADMIN_EMAILS` (when set) and a `pg_try_advisory_xact_lock`, and refuses once complete. Postgres compose password is fixed but unpublished (F-13) |
| Network exposure | App publishes 9000; Postgres is internal only; `SetTrustedProxies` limits forwarded-header trust to private ranges |
| Backup/migration | `make db-backup`/`db-restore` exist (pg_dump/psql). Migrations run under an advisory lock, one transaction per file, with checksum verification of applied files. `*.down.sql` files exist but are not applied automatically. No restore drill was executed |
| Dependencies/licenses | 5 reachable advisories fixed (F-10); permissive licences only |

---

## 4. Reproduction steps for confirmed defects

All commands assume the repository root and a local Postgres:

```bash
# Toolchain used (no system install)
curl -sSL https://go.dev/dl/go1.27.2.darwin-arm64.tar.gz | tar -xz -C /tmp/goroot
export PATH=/tmp/goroot/go/bin:$PATH

# F-1: did not compile
go build ./...            # 5 build errors across 4 files, 3 test errors across 2 more
go vet ./...              # + undefined: strings in license_activation_test.go

# F-3: whitespace identifier
go test ./pkg/deviceid -run InvalidIdentifierFormats -v
#   FAIL ... contains_newline: expected validation error for "device-id\n", got nil

# F-4: symlinked directory escape (see section 2)
# F-5: Python SDK (before fix)
python3 - <<'PY'
# flip signing_key_id to an unknown id / re-sign manifest_version=2 → previously accepted
PY

# F-6: manifest accepted as a licence token
# temporary test calling license.Verify(env.Payload+"."+env.Signature, pub) → success

# F-19: lifecycle test red
TEST_DATABASE_URL=... go test ./internal/store -run ActivationLifecycle -v
```

Database setup used for the executed runs:

```bash
brew install postgresql@18
LC_ALL=en_US.UTF-8 initdb -D /tmp/kgpg -U postgres
LC_ALL=en_US.UTF-8 pg_ctl -D /tmp/kgpg -o "-p 55432 -k /tmp" -l /tmp/kgpg.log start
createdb -h /tmp -p 55432 -U postgres keygate_test
export TEST_DATABASE_URL="postgres://postgres@localhost:55432/keygate_test?sslmode=disable"
```

---

## 5. Tests actually executed

| # | Command | Environment | Result |
|---|---|---|---|
| 1 | `go build ./...` | Go 1.27.2, macOS arm64 | **FAILED** initially (F-1); **PASSED** after fixes |
| 2 | `go vet ./...` | same | **FAILED** initially (F-1); **PASSED** after fixes |
| 3 | `go test ./... -count=1` (no DB) | same | 17 packages `ok`; 1 package **FAILED** (`pkg/deviceid`, F-3); 85 store + 43 handler tests silently skipped |
| 4 | `go test ./internal/store -count=1` | + Postgres 18.6 | **FAILED** initially (F-19); **PASSED** after the test fix |
| 5 | `go test ./internal/handler -count=1` | + Postgres 18.6 | PASSED |
| 6 | `go test ./... -count=1 -v` (packages in parallel) | + Postgres 18.6, fresh database | **470 PASS, 2 SKIP, 0 FAIL**; 17 packages `ok`, 4 with no test files. Skips: `TestSigningServiceMaxSignSize` ("requires real store"), `TestSetSettings_Atomic` ("atomicity not observable this way") |
| 6b | `go test ./... -count=1`, then `go test -p 1 ./... -count=1`, on the reused database | + Postgres 18.6 | Both eventually **FAILED** `TestQueueMarksPermanentFailureTerminal` (`internal/service`, F-21) once the shared `email_queue` had accumulated ≥20 due rows. Everything else passed in the same runs (16 packages `ok`); `internal/service` in isolation passed again once the due backlog was smaller |
| 7 | `go test -race -count=1 -run "Concurrent\|Atomic\|Race" ./internal/store ./internal/handler ./internal/middleware` | + Postgres 18.6 | PASSED (no data races) |
| 8 | `go test ./internal/handler -run "PaddedIdentifier\|InvalidIdentifierFormats" -v` | + Postgres 18.6 | PASSED (F-3 regression tests) |
| 9 | `go test ./pkg/integrity -run "Symlink" -v` | Go | PASSED (F-4 regression test) |
| 10 | `go test ./pkg/deviceid ./pkg/apperr -run "Whitespace\|NonCanonical\|InvalidIdentifierFormats" -v` | Go | PASSED (F-3 regression tests) |
| 11 | `govulncheck ./...` | Go 1.27.2 | 5 reachable vulns → after `x/net` bump: **no vulnerabilities found** |
| 12 | `python3 test/integrity_test.py` | Python 3.9.6 | PASSED (8/8 sections) |
| 13 | `python3 test/offline_token_test.py` | Python 3.9.6 | PASSED (14/14 vectors) |
| 14 | `python3 test/device_identity_test.py` | Python 3.9.6 | PASSED (fixtures incl. drift/clone scoring) |
| 15 | `python3 -m unittest discover -s sdk/python/tests -v` | Python 3.9.6 | PASSED (12 → **16** after F-5 regression tests) |
| 16 | `node --test tests/*.test.mjs` (in `web/`) | Node 24.18.0 | PASSED (9 tests) |
| 17 | Go CLI → Python interop (manual) | Go CLI + Python script + SDK | PASSED: manifest created by `keygate-manifest create` verified by `keygate-manifest verify`, `scripts/integrity_manifest.py verify`, and `ManifestVerifier` (pinned and keyring modes) |
| 18 | Symlink escape reproduction (F-4) | Go CLI + Python SDK | **Reproduced**: Go PASSED, Python rejected `JAIL_ESCAPE`; PASSES after fix |
| 19 | Manifest-as-licence-token probe (F-6) | Go | **Reproduced**: `Verify` returned success with empty claims |
| 20 | Python unknown-key / bad-version probes (F-5) | Python | **Reproduced** before fix; rejected after fix |

**Not executed (stated as limitations, not passes):**

- Docker image build and container run (`docker` daemon unavailable on the host).
- `web/` `bun run typecheck`, `bun run lint`, `bun run build` (Bun not installed,
  `web/node_modules` absent).
- Backup/restore drill (`make db-backup` / `db-restore`).
- Any Stripe, SMTP or object-storage integration against a real endpoint.
- Load/performance testing of the rate limiter or activation path.
- Frontend end-to-end flows in a browser (the admin UI changes were reviewed by
  reading `web/src/pages/admin/licenses.tsx` and `web/src/lib/api.ts`; the
  Playwright-style flows were not exercised).
- Licence key rotation with a live client fleet (inherently multi-instance/time-based).

---

## 6. Changes made during this review

All changes are **uncommitted** in the working tree. They are the minimum needed
to make the branch testable and to close the confirmed defects, with regression
tests for each.

| File | Change |
|---|---|
| `cmd/keygate-manifest/main.go`, `internal/handler/license.go`, `pkg/integrity/scanner.go`, `internal/license/offline_test.go`, `pkg/deviceid/deviceid_test.go` | Removed unused imports (F-1) |
| `internal/handler/license_activation_test.go` | Added missing `strings` import (F-1); added padded-identifier regression test (F-3) |
| `pkg/apperr/validate.go`, `pkg/deviceid/deviceid.go` | Reject non-canonical identifier spelling instead of trimming (F-3) |
| `pkg/apperr/validate_test.go`, `pkg/deviceid/deviceid_test.go` | Regression tests for F-3 |
| `pkg/integrity/verifier.go` | Resolve full path + resolved-root containment + regular-file check (F-4) |
| `pkg/integrity/integrity_test.go` | Symlink-escape regression test (F-4) |
| `sdk/python/keygate/manifest.py` | Reject unknown key IDs in keyring mode, validate manifest version, payload/envelope key-ID match (F-5) |
| `sdk/python/tests/test_sdk.py` | Four regression tests (F-5) |
| `internal/store/activation_lifecycle_test.go` | Size the licence cap to the table (F-19) |
| `go.mod`, `go.sum` | `golang.org/x/net v0.59.0 → v0.60.0` (F-10) |
| `.gitignore`, `.dockerignore` | Exclude `backup-*.sql` (F-8) |

No verification was weakened to make a test pass: F-3 and F-4 tighten checks, F-5
tightens the SDK, F-19 corrects the test's own capacity arithmetic while keeping
every assertion, and the dependency bump is a patch-level upgrade.

---

## 7. Residual risks and unresolved items

1. **F-2** — until CI runs the full suite against Postgres, the activation and
   concurrency invariants can regress unnoticed. This is the highest-leverage
   process fix remaining.
2. **F-6** — no domain separation between signed token types. Fixing it is a
   protocol change that invalidates currently issued tokens; not applied.
3. **F-7** — documented drift/PoP controls do not exist. Either build them or
   correct the document; integrators are currently misled.
4. **F-8** — plaintext license keys remain in the database and in `pg_dump`
   output. Encryption is additive until the column is dropped.
5. **F-9** — dev-login is a configuration footgun, not a code bug; it needs a
   product decision.
6. **F-18** — device transfer trusts the license key plus knowledge of the old
   identifier. Any proof-of-possession design (R-17) would also close this.
7. **License signing-key rotation** — `/license/pubkey` returns a single key, so a
   rotation invalidates offline tokens already issued (and breaks clients that
   cached the old key) until they re-fetch. The offline-licensing document
   describes an overlap that is not implemented.
8. **Multi-replica brute-force/rate limiting** — lockouts live in process memory
   unless Redis is configured, so N replicas give N budgets. Documented, not fixed.
9. **Integrity manifests are static-file checks only** — by design and correctly
   documented. They do not stop a patched binary, `ptrace`, `LD_PRELOAD` or memory
   hooks; a client that skips verification is out of contract.
10. **Client-side trust** — offline tokens, device binding and manifest checks are
    only as strong as the client implementation. The Python SDK is a reference,
    not a hardened runtime; F-15 (hand-rolled Ed25519) is part of that risk.
11. **F-21** — `internal/service` inherits a shared, never-reset `email_queue`,
    so one email test fails once the backlog exceeds the 20-mail batch. Fix the
    test isolation before relying on a full-suite CI gate.
12. **Unknowns** — production deployment topology (TLS termination, proxy trust,
    replica count), real Stripe/SMTP behaviour, and load characteristics were not
    observable from this checkout.

---

## 8. Conclusion

The core server-side licencing invariants that were actually exercised — atomic
activation caps under concurrency (including under `-race`), slot-free repeat
activation, transfer releasing slots in one transaction, lifecycle rejection, and
scoped admin authorization — held. The newly added activation/device feature had
one confirmed slot-accounting defect (F-3) that the repository's own test already
flagged but that could not run because the branch did not compile (F-1) and CI
does not run the relevant tests (F-2). The integrity verifier had a real
confinement bypass (F-4) and the reference SDK accepted unknown keys and
unsupported versions (F-5).

After the fixes listed in section 6, the full Go suite (470 pass, 2 skip, 0 fail on
a fresh database; see F-21 for the one queue-dependent test once a test database
has been reused), the concurrency subset under `-race`, the Python suites
(8 + 14 + device fixtures + 16 SDK tests) and the frontend unit tests (9) pass
locally, and `govulncheck` reports no reachable vulnerabilities. The remaining Medium/Low findings above are real but were not
fixed here because they require product decisions, protocol changes, or
documentation rewrites. This review does not establish that the system is secure.
