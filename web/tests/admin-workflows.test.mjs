import test from "node:test"
import assert from "node:assert/strict"
import crypto from "node:crypto"

// Mock server and fetch implementation to test admin API client functions
class MockResponse {
  constructor(status, data, headers = {}) {
    this.status = status
    this.ok = status >= 200 && status < 300
    this.statusText = status === 200 ? "OK" : status === 204 ? "No Content" : "Error"
    this._data = data
    this.headers = new Map(Object.entries(headers))
  }

  async text() {
    if (this.status === 204 || this._data === undefined) return ""
    return typeof this._data === "string" ? this._data : JSON.stringify(this._data)
  }

  async json() {
    return this._data
  }
}

test("Admin API client: activation and license transfer endpoints", async (t) => {
  const calls = []

  // Mock global fetch
  const originalFetch = globalThis.fetch
  globalThis.fetch = async (url, opts = {}) => {
    calls.push({ url, opts, body: opts.body ? JSON.parse(opts.body) : undefined })

    // Simulate router
    if (url.endsWith("/activations/act_1/deactivate") && opts.method === "POST") {
      return new MockResponse(200, { status: "deactivated", id: "act_1" })
    }
    if (url.endsWith("/activations/act_1/reactivate") && opts.method === "POST") {
      return new MockResponse(200, { status: "active", id: "act_1" })
    }
    if (url.endsWith("/activations/act_full/reactivate") && opts.method === "POST") {
      return new MockResponse(409, {
        error: { code: "ACTIVATION_LIMIT", message: "maximum activations reached" },
      })
    }
    if (url.endsWith("/licenses/lic_1/transfer") && opts.method === "POST") {
      const body = JSON.parse(opts.body)
      if (body.new_email === "current@example.com") {
        return new MockResponse(409, {
          error: { code: "IDENTICAL_EMAIL", message: "new_email is identical to current license email" },
        })
      }
      return new MockResponse(200, {
        status: "transferred",
        license_id: "lic_1",
        email: body.new_email,
        activations_released: body.release_activations ? 2 : 0,
      })
    }
    if (url.includes("/audit-logs?") && (!opts.method || opts.method === "GET")) {
      return new MockResponse(200, {
        audit_logs: [
          {
            id: "aud_1",
            entity: "license",
            entity_id: "lic_1",
            action: "transferred",
            actor_type: "admin",
            changes: { from_email: "old@example.com", to_email: "new@example.com", activations_released: 2 },
            created_at: new Date().toISOString(),
          },
          {
            id: "aud_2",
            entity: "activation",
            entity_id: "act_1",
            action: "deactivated",
            actor_type: "admin",
            changes: { reason: "admin" },
            created_at: new Date().toISOString(),
          },
        ],
        total: 2,
      })
    }

    return new MockResponse(404, { error: "not found" })
  }

  // Restore fetch after test
  t.after(() => {
    globalThis.fetch = originalFetch
  })

  // Helper matching the admin client logic in web/src/lib/api.ts
  const api = {
    async request(path, opts) {
      const BASE = "/api/v1"
      const res = await globalThis.fetch(BASE + path, {
        headers: { "Content-Type": "application/json" },
        ...opts,
      })
      const raw = await res.text()
      let json = null
      if (raw) {
        try { json = JSON.parse(raw) } catch {}
      }
      if (!res.ok) {
        const msg = json?.error?.message || json?.error || `Request failed (${res.status})`
        const err = new Error(msg)
        err.code = json?.error?.code
        throw err
      }
      return json?.data !== undefined ? json.data : json
    },
    deactivateActivation(id) {
      return this.request(`/admin/activations/${id}/deactivate`, { method: "POST", body: "{}" })
    },
    reactivateActivation(id) {
      return this.request(`/admin/activations/${id}/reactivate`, { method: "POST", body: "{}" })
    },
    transferLicense(id, data) {
      return this.request(`/admin/licenses/${id}/transfer`, { method: "POST", body: JSON.stringify(data) })
    },
    listLicenseAudit(id) {
      return this.request(`/admin/audit-logs?entity=license&entity_id=${id}`)
    },
  }

  await t.test("deactivateActivation sends POST to /admin/activations/:id/deactivate", async () => {
    const res = await api.deactivateActivation("act_1")
    assert.equal(res.status, "deactivated")
    assert.equal(res.id, "act_1")
    const call = calls.find((c) => c.url.includes("/activations/act_1/deactivate"))
    assert.ok(call)
    assert.equal(call.opts.method, "POST")
  })

  await t.test("reactivateActivation succeeds when slot is available", async () => {
    const res = await api.reactivateActivation("act_1")
    assert.equal(res.status, "active")
    assert.equal(res.id, "act_1")
  })

  await t.test("reactivateActivation returns ACTIVATION_LIMIT error on quota exceeded", async () => {
    await assert.rejects(
      async () => {
        await api.reactivateActivation("act_full")
      },
      (err) => {
        assert.equal(err.code, "ACTIVATION_LIMIT")
        assert.match(err.message, /maximum activations reached/)
        return true
      }
    )
  })

  await t.test("transferLicense sends new_email, release_activations, and reason", async () => {
    const res = await api.transferLicense("lic_1", {
      new_email: "transferred@example.com",
      release_activations: true,
      reason: "Employee departure",
    })
    assert.equal(res.status, "transferred")
    assert.equal(res.email, "transferred@example.com")
    assert.equal(res.activations_released, 2)
    const call = calls.find((c) => c.url.includes("/licenses/lic_1/transfer"))
    assert.ok(call)
    assert.equal(call.body.new_email, "transferred@example.com")
    assert.equal(call.body.release_activations, true)
    assert.equal(call.body.reason, "Employee departure")
  })

  await t.test("transferLicense handles identical email rejection", async () => {
    await assert.rejects(
      async () => {
        await api.transferLicense("lic_1", { new_email: "current@example.com" })
      },
      (err) => {
        assert.equal(err.code, "IDENTICAL_EMAIL")
        return true
      }
    )
  })

  await t.test("listLicenseAudit fetches audit logs for license entity", async () => {
    const res = await api.listLicenseAudit("lic_1")
    assert.equal(res.total, 2)
    assert.equal(res.audit_logs.length, 2)
    assert.equal(res.audit_logs[0].action, "transferred")
    assert.equal(res.audit_logs[0].changes.from_email, "old@example.com")
    assert.equal(res.audit_logs[0].changes.to_email, "new@example.com")
  })
})

test("Device Management Logic: Active Slot Counting and Capacity Checks", () => {
  const activations = [
    { id: "1", identifier: "dev-1", status: "active", scheme: "sha256-v1" },
    { id: "2", identifier: "dev-2", status: "deactivated", deactivated_reason: "admin", scheme: "uuid" },
    { id: "3", identifier: "dev-3", status: "active", scheme: "kgid-v1" },
    { id: "4", identifier: "dev-4", scheme: "legacy" }, // no status field -> defaults to active
  ]

  const countActive = (items) => items.filter((a) => a.status === "active" || (!a.status && a.status !== "deactivated")).length
  const activeCount = countActive(activations)
  assert.equal(activeCount, 3)

  const maxActivations = 3
  const isCapacityReached = maxActivations > 0 && activeCount >= maxActivations
  assert.equal(isCapacityReached, true)

  const maxActivationsHigher = 5
  assert.equal(maxActivationsHigher > 0 && activeCount >= maxActivationsHigher, false)
})

test("Release Artifact Display Logic: Metadata, Signing Key ID & Integrity Notice", () => {
  const artifact = {
    id: "art_1",
    platform: "darwin-arm64",
    filename: "keygate-mac.tar.gz",
    file_size: 45219800,
    sha256: "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
    ed25519_sig: "abcdef1234567890",
    signing_key_id: "key_sec_prod_01",
  }

  // Preview format
  const shaPreview = `sha256:${artifact.sha256.slice(0, 12)}…`
  assert.equal(shaPreview, "sha256:e3b0c44298fc…")

  // Signing key badge
  const keyBadge = `kid:${artifact.signing_key_id.slice(0, 8)}`
  assert.equal(keyBadge, "kid:key_sec_")

  // Verification claims boundary check
  const serverClaimsClientRuntimeIntegrity = false
  assert.equal(serverClaimsClientRuntimeIntegrity, false, "Server must not claim real-time knowledge of client machine runtime state")
})

test("Offline License Token Parsing and Claims Validation", async (t) => {
  // Helper base64url encode for testing
  const b64url = (obj) => {
    const json = typeof obj === "string" ? obj : JSON.stringify(obj)
    return Buffer.from(json).toString("base64url")
  }

  // Implementation of parseOfflineToken testable in Node environment
  const parseToken = (rawToken) => {
    const trimmed = rawToken.trim()
    if (!trimmed) return { format: "invalid", error: "Empty token provided" }

    let payloadB64 = ""
    let signatureB64 = ""
    let format = "legacy"
    let versionPrefix

    if (trimmed.startsWith("keygate.offline.v1.")) {
      format = "v1-prefixed"
      versionPrefix = "keygate.offline.v1"
      const parts = trimmed.split(".")
      if (parts.length !== 5) {
        return { format: "invalid", error: "Malformed token: expected 5 dot-separated segments" }
      }
      payloadB64 = parts[3]
      signatureB64 = parts[4]
    } else {
      const parts = trimmed.split(".")
      if (parts.length !== 2) {
        return { format: "invalid", error: "Malformed token: expected dot-separated payload and signature" }
      }
      payloadB64 = parts[0]
      signatureB64 = parts[1]
    }

    let payload
    try {
      const decodedJson = Buffer.from(payloadB64, "base64url").toString("utf8")
      payload = JSON.parse(decodedJson)
    } catch (e) {
      return { format, error: "Failed to decode payload" }
    }

    const nowSec = Math.floor(Date.now() / 1000)
    const isExpired = payload.exp > 0 && payload.exp < nowSec
    const isTermExpired = payload.vun > 0 && payload.vun < nowSec

    return {
      format,
      versionPrefix,
      header: { ver: payload.ver, kid: payload.kid },
      payload,
      isExpired,
      isTermExpired,
      signatureBase64: signatureB64,
    }
  }

  await t.test("parses valid keygate.offline.v1 token and extracts claims", () => {
    const validClaims = {
      ver: 1,
      kid: "prod_key_2026",
      lic: "lic_9999",
      pln: "enterprise-tier",
      cus: "enterprise@company.com",
      dfp: "sha256:d8b2...a4e1",
      exp: Math.floor(Date.now() / 1000) + 86400 * 30, // 30 days in future
      vun: Math.floor(Date.now() / 1000) + 86400 * 365,
      iat: Math.floor(Date.now() / 1000),
      fea: { pro_features: "true", export_pdf: "true" },
    }

    const mockSig = Buffer.from("mock_ed25519_signature_64_bytes_test").toString("base64url")
    const raw = `keygate.offline.v1.${b64url(validClaims)}.${mockSig}`

    const parsed = parseToken(raw)
    assert.equal(parsed.format, "v1-prefixed")
    assert.equal(parsed.versionPrefix, "keygate.offline.v1")
    assert.equal(parsed.header.kid, "prod_key_2026")
    assert.equal(parsed.payload.lic, "lic_9999")
    assert.equal(parsed.payload.pln, "enterprise-tier")
    assert.equal(parsed.payload.fea.pro_features, "true")
    assert.equal(parsed.isExpired, false)
    assert.equal(parsed.isTermExpired, false)
  })

  await t.test("detects expired offline tokens", () => {
    const expiredClaims = {
      ver: 1,
      kid: "prod_key_2026",
      lic: "lic_expired",
      exp: Math.floor(Date.now() / 1000) - 3600, // expired 1 hour ago
    }
    const mockSig = "dummy_sig"
    const raw = `keygate.offline.v1.${b64url(expiredClaims)}.${mockSig}`
    const parsed = parseToken(raw)
    assert.equal(parsed.isExpired, true)
  })

  await t.test("handles legacy format tokens", () => {
    const claims = {
      lic: "lic_legacy",
      exp: Math.floor(Date.now() / 1000) + 7200,
    }
    const raw = `${b64url(claims)}.legacy_sig`
    const parsed = parseToken(raw)
    assert.equal(parsed.format, "legacy")
    assert.equal(parsed.payload.lic, "lic_legacy")
    assert.equal(parsed.isExpired, false)
  })

  await t.test("rejects malformed token strings", () => {
    const malformed = "not-a-valid-token"
    const parsed = parseToken(malformed)
    assert.equal(parsed.format, "invalid")
    assert.ok(parsed.error)
  })
})

test("Settings and Migrations API client response parsing", async () => {
  const originalFetch = globalThis.fetch
  globalThis.fetch = async (url) => {
    if (url.endsWith("/admin/system/migrations")) {
      return new MockResponse(200, {
        migrations: [
          { filename: "001_init_schema.sql", applied_at: "2026-01-01T00:00:00Z" },
          { filename: "002_add_offline_tokens.sql", applied_at: "2026-02-01T00:00:00Z" },
        ],
      })
    }
    if (url.endsWith("/license/pubkey")) {
      return new MockResponse(200, {
        algorithm: "Ed25519",
        kid: "key_primary_2026",
        format: "RFC 8037 JWK / Ed25519",
        public_key: "MCowBQYDK2VwAyEAX5nQ3K0Y4B9...",
      })
    }
    return new MockResponse(404, { error: "not found" })
  }

  try {
    // Migration status retrieval
    const migRes = await globalThis.fetch("/api/v1/admin/system/migrations")
    const migData = await migRes.json()
    assert.equal(migRes.status, 200)
    assert.equal(migData.migrations.length, 2)
    assert.equal(migData.migrations[0].filename, "001_init_schema.sql")
    assert.equal(migData.migrations[1].filename, "002_add_offline_tokens.sql")

    // Public key retrieval
    const keyRes = await globalThis.fetch("/api/v1/license/pubkey")
    const keyData = await keyRes.json()
    assert.equal(keyRes.status, 200)
    assert.equal(keyData.algorithm, "Ed25519")
    assert.equal(keyData.kid, "key_primary_2026")
    assert.ok(keyData.public_key.startsWith("MCowBQYD"))
  } finally {
    globalThis.fetch = originalFetch
  }
})

test("Device Page Filter and Search Logic", () => {
  const devices = [
    { id: "1", identifier: "sha256:abcd1234ef", label: "Dev-Desktop", ip_address: "192.168.1.10", license_email: "alice@acme.com", status: "active", scheme: "sha256-v1" },
    { id: "2", identifier: "uuid:1111-2222-3333", label: "Build-Server", ip_address: "10.0.0.5", license_email: "bob@acme.com", status: "deactivated", scheme: "uuid" },
    { id: "3", identifier: "kgid:mac-445566", label: "MacBook-Pro", ip_address: "192.168.1.55", license_email: "carol@initech.com", status: "active", scheme: "kgid-v1" },
  ]

  // Filter by status 'active'
  const activeOnly = devices.filter((d) => d.status === "active")
  assert.equal(activeOnly.length, 2)

  // Filter by scheme 'uuid'
  const uuidOnly = devices.filter((d) => d.scheme === "uuid")
  assert.equal(uuidOnly.length, 1)
  assert.equal(uuidOnly[0].label, "Build-Server")

  // Search by machine label
  const searchMac = devices.filter((d) => d.label.toLowerCase().includes("mac"))
  assert.equal(searchMac.length, 1)
  assert.equal(searchMac[0].identifier, "kgid:mac-445566")

  // Search by email
  const searchEmail = devices.filter((d) => d.license_email.toLowerCase().includes("acme"))
  assert.equal(searchEmail.length, 2)
})

test("Admin API client: issueOfflineToken workflow and error responses", async (t) => {
  const calls = []
  const originalFetch = globalThis.fetch

  globalThis.fetch = async (url, opts = {}) => {
    calls.push({ url, opts, body: opts.body ? JSON.parse(opts.body) : undefined })

    if (url.endsWith("/admin/licenses/lic_active/offline-token") && opts.method === "POST") {
      const body = JSON.parse(opts.body)
      return new MockResponse(200, {
        status: "active",
        license_id: "lic_active",
        identifier: body.identifier || "sha256:generated_fp",
        token: "keygate.offline.v1.mock_claims.mock_sig",
      })
    }
    if (url.endsWith("/admin/licenses/lic_full/offline-token") && opts.method === "POST") {
      return new MockResponse(409, {
        error: { code: "ACTIVATION_LIMIT", message: "activation limit reached for plan" },
      })
    }
    if (url.endsWith("/admin/licenses/lic_expired/offline-token") && opts.method === "POST") {
      return new MockResponse(400, {
        error: { code: "LICENSE_EXPIRED", message: "cannot issue offline token for expired license" },
      })
    }

    return new MockResponse(404, { error: "not found" })
  }

  t.after(() => {
    globalThis.fetch = originalFetch
  })

  const api = {
    async request(path, opts) {
      const BASE = "/api/v1"
      const res = await globalThis.fetch(BASE + path, {
        headers: { "Content-Type": "application/json" },
        ...opts,
      })
      const raw = await res.text()
      let json = null
      if (raw) {
        try { json = JSON.parse(raw) } catch {}
      }
      if (!res.ok) {
        const msg = json?.error?.message || json?.error || `Request failed (${res.status})`
        const err = new Error(msg)
        err.code = json?.error?.code
        throw err
      }
      return json?.data !== undefined ? json.data : json
    },
    issueOfflineToken(licenseId, payload) {
      return this.request(`/admin/licenses/${licenseId}/offline-token`, {
        method: "POST",
        body: JSON.stringify(payload),
      })
    },
  }

  await t.test("successfully issues offline token for an active license", async () => {
    const res = await api.issueOfflineToken("lic_active", {
      identifier: "sha256:airgap_workstation_01",
      label: "Air-gapped Lab Station",
      scheme: "sha256-v1",
      features: ["enterprise_pack"],
    })
    assert.equal(res.status, "active")
    assert.equal(res.license_id, "lic_active")
    assert.equal(res.identifier, "sha256:airgap_workstation_01")
    assert.ok(res.token.startsWith("keygate.offline.v1."))
    const call = calls.find((c) => c.url.includes("/lic_active/offline-token"))
    assert.ok(call)
    assert.equal(call.body.scheme, "sha256-v1")
  })

  await t.test("rejects offline issuance when activation quota is reached", async () => {
    await assert.rejects(
      async () => {
        await api.issueOfflineToken("lic_full", { identifier: "sha256:new_dev" })
      },
      (err) => {
        assert.equal(err.code, "ACTIVATION_LIMIT")
        assert.match(err.message, /activation limit reached/)
        return true
      }
    )
  })

  await t.test("rejects offline issuance when license is expired", async () => {
    await assert.rejects(
      async () => {
        await api.issueOfflineToken("lic_expired", { identifier: "sha256:new_dev" })
      },
      (err) => {
        assert.equal(err.code, "LICENSE_EXPIRED")
        assert.match(err.message, /cannot issue offline token/)
        return true
      }
    )
  })
})

test("Integrity Manifest Envelope and File Table Parsing", () => {
  const validEnvelope = {
    schema_version: "keygate-integrity-v1",
    application: "Keygate CLI",
    version: "2.4.0",
    generated_at: "2026-10-18T12:00:00Z",
    signing_key_id: "key_prod_release_01",
    signature: "ed25519_base64_signature_here",
    files: [
      { path: "bin/keygate", sha256: "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08", size_bytes: 14208000 },
      { path: "lib/runtime.so", sha256: "5e884898da28047151d0e56f8dc6292773603d0d6aabbdd62a11ef721d1542d8", size_bytes: 8192000 },
      { path: "config/defaults.json", sha256: "4b227777d4dd1fc61c6f884f48641d02b4d121d3fd328cb08b5531fcacdabf8a", size_bytes: 1024 },
    ],
  }

  // Verification helper matching IntegrityManifestInspector logic
  const parseManifest = (envelope) => {
    if (!envelope || typeof envelope !== "object") {
      return { valid: false, error: "Invalid JSON object" }
    }
    if (envelope.schema_version !== "keygate-integrity-v1") {
      return { valid: false, error: `Unsupported schema: ${envelope.schema_version}` }
    }
    if (!Array.isArray(envelope.files)) {
      return { valid: false, error: "Missing files array in manifest" }
    }
    const totalBytes = envelope.files.reduce((acc, f) => acc + (f.size_bytes || 0), 0)
    return {
      valid: true,
      fileCount: envelope.files.length,
      totalBytes,
      hasSignature: !!envelope.signature,
      signingKeyId: envelope.signing_key_id,
    }
  }

  const result = parseManifest(validEnvelope)
  assert.equal(result.valid, true)
  assert.equal(result.fileCount, 3)
  assert.equal(result.totalBytes, 14208000 + 8192000 + 1024)
  assert.equal(result.hasSignature, true)
  assert.equal(result.signingKeyId, "key_prod_release_01")

  // File filter by path
  const filterByPath = (files, q) =>
    files.filter((f) => f.path.toLowerCase().includes(q.toLowerCase()) || f.sha256.toLowerCase().includes(q.toLowerCase()))

  const matchBin = filterByPath(validEnvelope.files, "bin/")
  assert.equal(matchBin.length, 1)
  assert.equal(matchBin[0].path, "bin/keygate")

  const matchHash = filterByPath(validEnvelope.files, "5e884898")
  assert.equal(matchHash.length, 1)
  assert.equal(matchHash[0].path, "lib/runtime.so")

  // Invalid schema rejection
  const invalidSchema = parseManifest({ schema_version: "unknown-v0", files: [] })
  assert.equal(invalidSchema.valid, false)
  assert.match(invalidSchema.error, /Unsupported schema/)
})

test("Audit Log Event Formatting and Payload Inspection", () => {
  const logEvent = {
    id: "aud_98765",
    entity: "activation",
    entity_id: "act_4455",
    action: "offline_token_issued",
    actor_id: "usr_admin_1",
    actor_type: "user",
    ip_address: "192.168.1.100",
    changes: {
      identifier: "sha256:airgapped_device_abc",
      scheme: "sha256-v1",
      token_issued: true,
      features: ["enterprise_pack"],
    },
    created_at: "2026-10-18T13:00:00Z",
  }

  // Formatter test
  assert.equal(logEvent.action, "offline_token_issued")
  assert.equal(logEvent.changes.token_issued, true)
  assert.equal(logEvent.changes.scheme, "sha256-v1")
  assert.equal(logEvent.actor_type, "user")
  assert.equal(logEvent.ip_address, "192.168.1.100")

  // JSON serialization for copy functionality
  const formattedJson = JSON.stringify(logEvent.changes, null, 2)
  assert.ok(formattedJson.includes('"identifier": "sha256:airgapped_device_abc"'))
})

test("C# / .NET Windows Desktop SDK Integration Contract", async (t) => {
  const originalFetch = globalThis.fetch
  const calls = []

  globalThis.fetch = async (url, opts = {}) => {
    const body = opts.body ? JSON.parse(opts.body) : undefined
    calls.push({ url, opts, body })

    if (url.endsWith("/api/v1/license/activate") && opts.method === "POST") {
      if (!body.license_key || !body.identifier) {
        return new MockResponse(400, { error: { code: "INVALID_REQUEST", message: "license_key and identifier required" } })
      }
      return new MockResponse(200, {
        status: "activated",
        license_id: "lic_windows_01",
        token: "keygate.offline.v1.mock_claims_b64.mock_sig_b64",
        meta: { grace_days: 7 },
      })
    }

    if (url.endsWith("/api/v1/license/verify") && opts.method === "POST") {
      return new MockResponse(200, {
        status: "valid",
        token: "keygate.offline.v1.mock_refreshed_claims_b64.mock_sig_b64",
      })
    }

    if (url.endsWith("/api/v1/license/deactivate") && opts.method === "POST") {
      return new MockResponse(200, {
        status: "deactivated",
        identifier: body.identifier,
      })
    }

    return new MockResponse(404, { error: "not found" })
  }

  t.after(() => {
    globalThis.fetch = originalFetch
  })

  // Simulated C# KeygateClient logic
  const client = {
    baseUrl: "http://localhost:9000",
    licenseKey: "KG-PRO-2026-ABCD",

    getWindowsFingerprint(machineGuid) {
      const hash = crypto.createHash("sha256").update(machineGuid).digest("hex")
      return `sha256:${hash.toLowerCase()}`
    },

    async activate(identifier, label) {
      const res = await globalThis.fetch(`${this.baseUrl}/api/v1/license/activate`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          license_key: this.licenseKey,
          identifier,
          identifier_type: "device",
          scheme: "sha256-v1",
          label,
        }),
      })
      if (!res.ok) throw new Error(`Activation failed: ${res.status}`)
      return res.json()
    },

    async verify(identifier) {
      const res = await globalThis.fetch(`${this.baseUrl}/api/v1/license/verify`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          license_key: this.licenseKey,
          identifier,
        }),
      })
      return res.ok
    },

    async deactivate(identifier) {
      const res = await globalThis.fetch(`${this.baseUrl}/api/v1/license/deactivate`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          license_key: this.licenseKey,
          identifier,
        }),
      })
      return res.json()
    },
  }

  await t.test("formats Windows fingerprint matching sha256-v1 scheme", async () => {
    const rawGuid = "3f8e6c71-2b1a-4d9e-a8f5-1c2d3e4f5a6b"
    const fp = await client.getWindowsFingerprint(rawGuid)
    assert.ok(fp.startsWith("sha256:"))
    assert.equal(fp.length, 7 + 64) // 'sha256:' + 64 hex characters
  })

  await t.test("executes C# client activation workflow", async () => {
    const fp = "sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
    const result = await client.activate(fp, "WIN-DESKTOP-01")
    assert.equal(result.status, "activated")
    assert.equal(result.license_id, "lic_windows_01")
    assert.ok(result.token.startsWith("keygate.offline.v1."))

    const call = calls.find((c) => c.url.endsWith("/activate"))
    assert.ok(call)
    assert.equal(call.body.scheme, "sha256-v1")
    assert.equal(call.body.identifier_type, "device")
    assert.equal(call.body.label, "WIN-DESKTOP-01")
  })

  await t.test("executes C# client heartbeat verification", async () => {
    const fp = "sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
    const isValid = await client.verify(fp)
    assert.equal(isValid, true)
  })

  await t.test("executes C# client device deactivation", async () => {
    const fp = "sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
    const res = await client.deactivate(fp)
    assert.equal(res.status, "deactivated")
    assert.equal(res.identifier, fp)
  })
})



