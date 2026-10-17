# Protocol Versioning & Backward-Compatibility Strategy

Scope: how versions, capabilities and deprecations are expressed for the licensing
platform so that a server and a client written in any language, released at different
times, can always interoperate. Design phase only — no code changed.

---

## 1. Executive summary

Four *independent* version axes, each with one unambiguous source of truth:

| Axis | Current value | Where expressed | Changes when… |
|---|---|---|---|
| **URL/API version** | `v1` | path prefix `/api/v1` (existing) | a breaking HTTP contract change ships (never planned in this roadmap) |
| **Spec version (OpenAPI)** | `1.2.0` | `docs/openapi.yaml info.version` (existing) | documenting changes; SemVer: additive = minor, removals/shape changes = major |
| **Token format** | `legacy-ed25519` (two-part, current) | claim-free format in the token itself + `protocol.token_format` in the **PROPOSED** `/license/pubkey` extension | JOSE (`jws-eddsa`) tokens are introduced (additive, dual-issued) |
| **Server protocol capabilities** | implicit today | **PROPOSED** `protocol:{…}` block on `GET /license/pubkey` and `GET /version` | any new optional behavior (revocations, challenge, kid) ships with a capability flag |

Humans read `docs/api-contract.md`; machines read OpenAPI; clients branch only on
**numeric-safe additive fields, HTTP status and `error.code`** — never on prose.

---

## 2. Versioning rules

### 2.1 HTTP API (URL version)

- `v1` is permanent. The server answers unknown paths with the standard envelope
  `404 NOT_FOUND` (never bare text), so a client from the future talking to an older
  server fails *detectably*, not silently.
- **Additive evolution inside v1** (allowed freely):
  - new optional request fields with safe defaults (absent = old behavior);
  - new response fields (clients must ignore unknown fields — written into the SDK
    conformance rules);
  - new routes (revocations, transfer, signing-key discovery);
  - new `error.code` values **on new routes only**; never change the status an existing
    code maps to (protected by `TestErrorCodeMapsToExactlyOneStatus`).
- **Breaking changes (require `v2` side-by-side):** removing/renaming a field, changing a
  code's status, changing auth class of a route, altering pagination defaults, changing
  token verification bytes. v2 would be served *alongside* v1 (`/api/v2` keeps `/api/v1`
  frozen but operational for ≥2 spec-minor cycles).

### 2.2 Spec version (`info.version`, SemVer)

- **MAJOR** — a documented contract break (removal/formal semantic change).
- **MINOR** — new paths, new optional fields, new codes on new paths. (`1.2.0 → 1.3.0` for
  this proposal.)
- **PATCH** — documentation fixes, examples, descriptions.
- CI drift check (roadmap R-06) compares `main.go` route registrations with `paths:` —
  a route can never exist undocumented.

### 2.3 Token format versioning

- Format **`legacy-ed25519`** (today): `base64url(payload) "." base64url(ed25519_sig)`,
  signature over the payload-string bytes, no header ⇒ *no in-token version marker is
  possible*; format is identified by shape (exactly one `.`, decodable JSON payload).
- Format **`jws-eddsa`** (PROPOSED, optional): standard JWS compact
  `BASE64URL(protected) "." BASE64URL(payload) "." BASE64URL(sig)` with
  `{"alg":"EdDSA","kid":"…"}` in `protected` ⇒ self-describing, three segments.
- **Detection rule (normative):** split on `.` → 2 segments = legacy, **verify with the
  single current key, ignoring `kid`**; 3 segments = JWS, verify per `kid` from the key
  list. No negotiation round-trip needed; both formats are verifiable with stock
  libraries + 10 lines for legacy.
- Server control: `protocol.token_format` advertises what it *issues*; it must keep
  accepting `legacy-ed25519` for at least **2 declared deprecation cycles** after issuance
  stops (see §5). During dual-issuance the server may return **both** tokens
  (`token` legacy + `token_v2` JWS, same claims) so a client migrates by field presence.

### 2.4 Key identity (`kid`) versioning

- `kid` values are opaque, monotonically introduced strings (proposal: `ksk_<YYYY>_<MM>`),
  **never reused**; retired keys stay listed (status `retired`) until every token bearing
  that `kid` has expired (`max exp` + 30 d), then may be dropped from the response but
  kept in server storage for audit.
- Rotation protocol: add new active key (overlap) → start issuing with new `kid` → old key
  `retired` → after ≥ `max token_ttl_days` remove. Clients must accept any key in the
  *current* `keys[]` snapshot; they should persist the last snapshot to tolerate pubkey
  endpoint downtime (mirror-read rule).

---

## 3. Capability discovery (PROPOSED, additive)

Today a client cannot ask the server what it supports. Two additive answers:

1. `GET /api/v1/license/pubkey` → adds `protocol:{version:int, token_format:string,
   revocations:bool, kids:bool, challenge:bool}` (§8 of `docs/api-design.md`).
2. `GET /api/v1/version` (existing handler returns
   `version,commit,build_date,project,project_url`) → adds the same `protocol` object.

Rules: booleans are **feature flags, not versions** (absent/false = feature not present —
old servers simply omit them, which every JSON client already handles); `protocol.version`
is an integer bumped only when the *combination rules* change, not when a flag is added.
Known-server detection pattern for SDKs:

```
kid_supported := proto.restrict("protocol.kids") ?? (!proto.restrict("keys").is_empty()) // absent → old server
```
(implemented per language; no server-side language dependency).

Alternative rejected: adding the fields to `GET /config` — that handler returns a flat
`map[string]string`, so an object value would break the schema; discovery belongs on
`/license/pubkey` + `/version`.

---

## 4. Endpoint & field deprecation policy

1. **Announce in spec first** (`openapi.yaml` description + `docs/api-contract.md`), ship
   the replacement, then deprecate — never in that reverse order.
2. Deprecated routes stay functional; responses gain header `Deprecation: true` (RFC 9745
   style) and, where a replacement exists, `Link: <replacement>; rel="successor-version"`.
   (Both headers are additive: ignored by existing clients.)
3. The one historical precedent (`/releases/feed.*` → `410 FEED_PATH_REMOVED`) was a
   **pre-launch hard cutover** with no live clients (`main.go` comments). For installed
   clients the policy above applies instead: soft deprecation ≥ 2 spec-minor cycles, then
   `410` only if telemetry shows zero traffic.
4. Webhook receivers must ignore unknown `X-Keygate-Event` types (stated in the webhook
   docs; new event `license.transferred` rides this rule).
5. Error-code census is append-only: codes listed in `docs/api-design.md` §3/§4/§7 are
   frozen for their status.

---

## 5. Backward-compatibility strategy (consolidated)

Per roadmap item, the containment mechanism (mirrors `docs/architecture-proposal.md` §7):

| Change | Risk to existing clients | Containment |
|---|---|---|
| OpenAPI completion (R-06) | docs-only | no route/field change; drift check then *enforces* parity |
| Token vectors/kid (R-07/R-08) | none until issuance changes | `/license/pubkey` keeps `algorithm`+`public_key` scalars forever-ish; new fields optional; dual-token issuance via `token`+`token_v2` |
| JOSE `jws-eddsa` tokens | none | additive field; 2-epoch acceptance guarantee for legacy (rule §2.3); normative split rule already stable |
| Revocations list (R-09) | none (new route) | clients opt in; absence ⇒ today's behavior |
| Identifier canonicalization (R-10) | field devices with free-form ids | grandfather existing activations; validate only new rows; `max_activations` math unchanged for old rows |
| Stale reclamation (R-11) | slots freed overnight surprise operators | off unless `plans.stale_reclaim_days > 0`; portal already shows `last_verified` so the admin can see it coming |
| Transfer endpoints (new) | none | new paths only; webhook `license.transferred` ignored by old receivers |
| Encryption mandatory / Phase C (R-13) | operators with no master key | staged: warn (today) → loud per-boot warning + ops check → opt-in flag `ALLOW_PLAINTEXT_LICENSE_KEYS` explicitly false → drop column; a release note per stage |
| All changes | attribution contract | `attribution_text`/`attribution_url` in `/config`, `meta.server/url`, `X-Powered-By` remain — license-mandated (NOTICE); never "cleared" by a version bump |

**Client-side compatibility contract (publish in the conformance kit):**

1. Parse JSON leniently: ignore unknown fields, never hard-fail on extra keys.
2. Branch on `error.code` + HTTP status (code→status is 1:1, but status decides *retries*).
3. Treat `404 LICENSE_NOT_FOUND` as terminal-with-backoff (not "404 = deleted").
4. Persist: last good token, last key snapshot (`keys[]`), last `rev` seen — all optional;
   absence must not break startup.
5. Support both token formats until the server's `protocol.token_format` says issuance
   moved **and** the deprecation epoch elapsed.
6. Timestamps: accept RFC 3339 with `Z` (server always UTC); tolerate fractional seconds.
7. Tolerate pagination clamp (`limit` echoed back may differ from request).

---

## 6. Interaction with DB & server versions

- Schema changes ride the existing numbered migration pairs (auto-run at boot,
  `*/down.sql` provided); migrations are additive-first — the server binary stays
  N/N-1 compatible with the schema for one release, matching the rolling-restart Docker
  story.
- `GET /admin/system/migrations` (existing) lets an integrator verify the server's schema
  state before trusting a new feature flag.
- Multi-replica rule: a feature flag flips only after *all* replicas are on a build that
  understands it (pattern already used by `maintenance_features_enabled` — reuse that
  switch style for `revocations_enabled` if gating is desired).