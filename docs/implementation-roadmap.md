# Implementation Roadmap — ordered plan for the fork

Companion documents: `docs/security-audit.md` (findings S1–S12 with evidence),
`docs/feature-gap-analysis.md` (baseline classification + gaps),
`docs/architecture-proposal.md` (target design). This phase was **read-only**; nothing
below has been implemented yet.

Prioritization formula: **priority = risk addressed ÷ implementation cost**. Costs assume
the existing layering (handler → service → store) and existing test patterns; S = small
(≤1 dev-day), M = medium (2–5 days), L = large (1–3 weeks).

---

## 1. Executive summary

The server's core licensing machinery is sound and concurrency-tested; the fork's value
comes from closing the *offline trust lifecycle* (revocation, key rotation, documented
latency), formalizing the *contract* other languages build against (OpenAPI + vectors),
adding the missing *integrity* story, and hardening the operational surface. Roadmap = 5
stages (R0–R4), 20 tickets, each with acceptance criteria and the test that proves it. No
schema-breaking change is scheduled before its compatibility containment exists (see
§7 of `docs/architecture-proposal.md`).

---

## 2. Prioritized backlog (risk × cost)

Legend: **P0** = do before any public fork release · **P1** = first release scope ·
**P2** = second release · **P3** = continuous.

| ID | Item | Risk addressed | Cost | Priority |
|---|---|---|---|---|
| R-01 | Fix CI integration filter: run full `./internal/store` package with `TEST_DATABASE_URL` instead of `-run "Atomic"` (currently 9/92 tests; activation race tests never run in CI) | regression blind spot (verified green only manually) | S | **P0** |
| R-02 | Document offline revocation semantics (worst-case latency = `min(token_ttl_days, valid_until+grace)`) in README/OpenAPI/portal copy | S1 (operator blindness) | S | **P0** |
| R-03 | Document S2 (plaintext `LICENSE KEY` column) in deployment docs; add an ops check that fails loudly when `RELEASE_KEY_ENCRYPTION_KEY` unset in production | S2 | S | **P0** |
| R-04 | Make `TRUSTED_PROXIES` an explicit env var; default = trust nothing (remove hardcoded RFC1918 list) | S4 | S | **P0** |
| R-05 | Global request-body size limit middleware (64 KiB SDK, larger admin uploads) | S6 | S | **P0** |
| R-06 | Full OpenAPI 3.1 for all 140 routes + reusable `Envelope/Error/Page/ErrorCodes` components + CI drift check (routes ↔ spec) | contract gaps for non-JS clients | M | **P1** |
| R-07 | Token verification spec + golden vectors (valid/expired/tampered/wrong-device/unknown-key) published for SDK authors | language interop | S | **P1** |
| R-08 | `license_signing_keys` table + `kid`, overlap-verify, `/license/pubkey` extended (`keys[]`) | key rotation impossibility today | M | **P1** |
| R-09 | Signed revocation list endpoint (`GET /license/revocations`, Ed25519-signed, per-product) | S1 (closes offline revoked-license window) | M | **P1** |
| R-10 | Identifier canonicalization + validation at handler (new activations), fix `identifier_type` → 500 nit, grandfather legacy rows | S5 | S | **P1** |
| R-11 | Stale-activation reaping (`plans.stale_reclaim_days` on `last_verified`, in existing expiry loop) | lost-device slot recovery | M | **P1** |
| R-12 | Shared brute-force lockout (Redis backend with in-memory fallback, same pattern as rate limiter) | S3 | M | **P1** |
| R-13 | At-rest encryption to mandatory-by-default (fatal in production) + **Phase C**: verified backfill then `DROP COLUMN licenses.license_key` behind a flag | S2 | M | **P1** |
| R-14 | Audit integrity: sync (or outbox) writes for sensitive ops, `prev_hash`/`hash` chain, periodic export; audit-coverage audit (close the 35/78 gap) | S9 | M | **P1** |
| R-15 | Feed `rev`/signed timestamp anti-rollback + per-platform trust matrix doc incl. Velopack gap (S10) | update rollback | M | **P2** |
| R-16 | Challenge–response endpoint `POST /license/challenge` (signed nonce exchange) | runtime integrity baseline | M | **P2** |
| R-17 | Optional device binding claims at activation (`device_binding`, hw/app identity hints stored server-side) | clone detection | L | **P2** |
| R-18 | Admin MFA (TOTP), login-event webhooks, secret-rotation runbook + overlap endpoints | account takeover | M | **P2** |
| R-19 | CSP header, self-host the `/docs` Scalar asset (remove `cdn.jsdelivr.net`), `/metrics` optional auth, OTP log masking default | S7, S11, S8 | S | **P2** |
| R-20 | Supply chain: SBOM + CVE + license scan jobs, pin GitHub Actions by SHA; SDK conformance kit packaging; perf budget for shortened TTLs | ops/interop maturity | M (ongoing) | **P3** |

---

## 3. Recommended implementation order

**Stage R0 — Truth & safety net (≈1 week, docs/CI only, zero API change)**
R-01 → R-02 → R-03 → R-04 → R-05.
*Why first:* everything later depends on tests actually running and on the documented
baseline matching reality (the completion criterion of this audit phase).
*Acceptance:* CI executes all 92 store tests against Postgres; docs state S1/S2 behaviors;
a >64 KiB SDK POST is rejected with the envelope's `413 BODY_TOO_LARGE`; default config no
longer trusts forwarded headers.

**Stage R1 — Contract & offline trust (≈2 weeks)**
R-06 → R-07 → R-08 → R-09 → R-10 → R-11 → (start R-12, R-13 in parallel).
*Why second:* it delivers the fork's headline capabilities (offline validation done right,
language-agnostic contract) with additive changes only, and unblocks SDK work.
*Acceptance:* an independent verifier (a throwaway Python or Node script using only the
published spec+vectors) validates tokens and the revocation list without reading our code;
route↔spec drift check passes in CI; rotating `kid` keeps issuing+verifying across the
overlap window (test: two keys active, old tokens still verify).

**Stage R2 — Hardening (≈2 weeks, can overlap late R1)**
R-12 → R-13 → R-14 → R-19.
*Acceptance:* two server processes sharing Redis reject after the shared lockout threshold;
production boot fails without `RELEASE_KEY_ENCRYPTION_KEY`; a seeded DB with
`license_info`-only rows passes backfill verification before the column is dropped (feature
flag off by default); audit rows hash-chain verify query exists; security headers include
CSP; no third-party script origin on same-site pages.

**Stage R3 — Integrity (≈3 weeks)**
R-15 → R-16 → (R-17 if the clone-detection use case is confirmed).
*Acceptance:* replaying an older feed for the same channel is rejected by a reference
client; challenge round-trip validates in the conformance kit; a copied database license
key without the binding fails re-activation when binding is enabled.

**Stage R4 — Ecosystem (continuous)**
R-18, R-20, SDK conformance kit releases, dashboards/alerting for revocation and rotation.

Dependency notes: R-09 depends on R-08 (same signing-key infra). R-13 must land after R-03
(documented transition) and needs a DB backup drill. R-17 depends on R-10 (canonical ids).
Nothing in R1–R3 requires data migration *breakage*; all are additive except Phase C
(flag-gated) and identifier validation (grandfathered).

---

## 4. Testing strategy

**Harnesses already present (keep, never delete):**
- Atomicity/race: `internal/store/activation_race_test.go`, `floating_lease_test.go`,
  `idempotency_test.go`, `usage_test.go`, `release_publish_race_test.go`,
  `last_owner_race_test.go`, `unlink_race_test.go`, `processed_event_claim_test.go`,
  payment concurrency/renewal races.
- Contract/log: `pkg/response/contract_test.go` (one-status-per-code, ≥200 sites or fail),
  `cmd/server/log_redaction_test.go`, `internal/middleware/retry_after_test.go`,
  `internal/handler/sort_test.go`, `pagination_test.go`, `csv_test.go`.
- Crypto: `internal/license/token_test.go`, `crypto/*_test.go`,
  `release_signing_test.go`, `tauri_minisign_test.go` (external-verifier re-implementation
  pattern — generalize this for Sparkle and the JOSE token).
- Security behavior: `setup_test.go`, `session_cookie_test.go`, `refresh_grace_test.go`,
  `webhook_ssrf_test.go`, `email_auth_test.go`, `release_download_gate_test.go`,
  `feed_gate_trigger_test.go`.

**Required additions per stage:**

| Stage | Tests |
|---|---|
| R0 | CI runs `go test ./internal/store/` (not `-run "Atomic"`); body-limit tests; `TRUSTED_PROXY` default test asserting spoofed XFF is ignored |
| R1 | OpenAPI ↔ router drift check; schema validation of golden responses; token vectors (all negative cases) across formats; `kid` overlap rotation test; revocation-list signature verification test; identifier rejection/grandfather tests; stale-reclaim policy test at boundary `last_verified = cutoff±1s` |
| R2 | multi-instance lockout test (two processes, one Redis); encryption-mandatory boot test; backfill verification query test (read-only assert on a fixture DB); audit chain verification + failure-injection (DB down ⇒ sync path errors visibly); header suite (CSP/HSTS/metrics auth); OTP log-masking assertion |
| R3 | feed-replay/rollback test with `rev` counter; challenge–response vectors incl. replayed nonce refusal; binding mismatch rejection; ported Sparkle `edSignature` verifier port (parity with the Tauri port) |
| R4 | SBOM/CVE/license gate jobs; conformance kit runnable standalone (`go test ./testdata/vectors` + a script per target language, nightly) |

**Process rules:**
- Concurrency tests must run against real PostgreSQL in CI (they self-skip without
  `TEST_DATABASE_URL` — `internal/store/integration_test.go:16-30`; this silent skip is the
  failure mode R-01 removes).
- No security test may be marked `t.Skip` outside a guarded DB-skip block.
- Every roadmap ticket merges only with: implementation + test + doc/OpenAPI update in the
  same change (the drift check enforces the doc half).
- Non-goals for testing here: no load/perf harness exists today; add a `k6`-style script
  before changing TTL recommendations (R-20 dependency noted).

---

## 5. Licensing and dependency tasks

1. **Attribution & branding (license-mandatory):** preserve `"Powered by Keygate"`
   (UI sidebar/footer, `X-Powered-By`, `/api/v1/config` `attribution_text`/`attribution_url`,
   email footers) per `LICENSE` §7(b) + `NOTICE` + `AGENTS.md`. Rebranding name/logo is
   *required* by `NOTICE` for derivatives while the attribution must stay — schedule a
   branding task that renames only the identity constants (`internal/branding/branding.go`,
   `web/public/*`, manifest labels) and *never touches* the attribution fields. Commercial
   license (hello@keygate.app) is the only path to removal.
2. **AGPL compliance for our deployment:** ship source of our modified server to network
   users (offer URL in UI footer alongside attribution); track modifications per release.
3. **Dependency license inventory in CI:** run `go-licenses` (or equivalent) + an npm
   license checker as a workflow job; current graph is all permissive (MIT/BSD/Apache-2.0 —
   see `docs/security-audit.md` §5); fail on unknown/copyleft.
4. **CVE scanning:** add OSV/Trivy/govulncheck job (Go 1.27 toolchain supports
   `govulncheck`); currently absent.
5. **Action pinning:** move `.github/workflows/*` from major tags (`@v7`) to full SHAs.
6. **Third-party runtime origins:** self-host the Scalar docs bundle (removes
   `cdn.jsdelivr.net` runtime dependency; also required for air-gapped installs and R2's
   CSP).
7. **Compose/db defaults:** production guide must document overriding the quickstart
   Postgres credential and not exposing port 9000 without TLS in front (compose file itself
   already hints at `.env` secrets; `ValidateSecurityDefaults` covers JWT/signing key
   length).

---

## 6. Summary of findings (audit deliverable checklist)

1. **Executive summary** — `docs/security-audit.md` §1, `docs/feature-gap-analysis.md` §1, `docs/architecture-proposal.md` §1.
2. **Verified current capabilities** — `docs/feature-gap-analysis.md` §2 tables; test execution results in `docs/security-audit.md` §1–2.
3. **Missing functionality** — `docs/feature-gap-analysis.md` §3 (A–E groups).
4. **Security findings with severity + evidence** — `docs/security-audit.md` §3 (S1–S12 + "explicitly not found" + unknowns), summarized in `docs/feature-gap-analysis.md` §4.
5. **API and database observations** — `docs/security-audit.md` §4, `docs/feature-gap-analysis.md` §5.
6. **Language interoperability findings** — `docs/feature-gap-analysis.md` §6 (all 8 mandated languages assessed), `docs/architecture-proposal.md` §4.
7. **Backward-compatibility risks** — `docs/feature-gap-analysis.md` §7, `docs/architecture-proposal.md` §7.
8. **Recommended implementation order** — §2–§3 of this document; summary in `docs/architecture-proposal.md` §8.
9. **Testing strategy** — §4 of this document; `docs/feature-gap-analysis.md` §8; `docs/security-audit.md` §6.
10. **Licensing and dependency concerns** — `docs/security-audit.md` §5, `docs/feature-gap-analysis.md` §9, §5 of this document.

Files examined for this audit: consolidated list in `docs/feature-gap-analysis.md` §10.