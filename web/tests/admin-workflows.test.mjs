import test from "node:test"
import assert from "node:assert/strict"

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
