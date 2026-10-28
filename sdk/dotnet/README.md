# Keygate.Protection — .NET SDK

Client-side license protection for .NET: stable device binding, signed
offline licenses, signed file-integrity manifests, and tamper reporting that
the server can act on with a policy.

> نسخه‌ی فارسی این راهنما: [`README.fa.md`](README.fa.md)

| Package | Target | Purpose |
| --- | --- | --- |
| `Keygate.Protection` | `netstandard2.0`, `net8.0` | Core SDK (console, desktop, services, .NET Core 3.1, .NET Framework 4.6.2+) |
| `Keygate.Protection.AspNetCore` | `net8.0` | DI, middleware, `[RequireKeygateFeature]` |
| `Keygate.Protection.Tests` | `net8.0` | xUnit tests incl. Go protocol vectors |

## Install

```bash
dotnet add package Keygate.Protection
dotnet add package Keygate.Protection.AspNetCore   # ASP.NET Core only
```

From this repository, reference the projects instead:

```xml
<ProjectReference Include="sdk/dotnet/Keygate.Protection/Keygate.Protection.csproj" />
```

Ed25519 is provided by `BouncyCastle.Cryptography` (pure managed, no native
dependency). On Windows the local state is encrypted with DPAPI; on macOS and
Linux it is a `0600` file (see *Limitations*).

## Quick start (console / desktop)

```csharp
using Keygate.Protection;

var options = new KeygateOptions
{
    BaseUrl          = "https://licenses.example.com",   // https only
    LicenseKey       = licenseKeyFromYourOwnConfig,
    ProductId        = "prod_...",                       // strongly recommended
    LicensePublicKey = "…64 hex or base64url…",          // pin the signing key
    LicensePublicKeyId = "key_...",
    DeviceLabel      = "Acme Desktop",
};

using var protection = new KeygateProtection(options);

await protection.InitializeAsync();          // local only, no network
if (protection.CurrentLicense is null)
    await protection.ActivateAsync();        // first run; idempotent afterwards

LicenseValidation check = await protection.ValidateLicenseAsync();
if (!check.IsValid) { /* check.ProblemCode, check.Problem */ }

if (!protection.HasFeature("pro")) { /* upsell */ }
```

Runnable samples:

```bash
dotnet run --project examples/dotnet/Keygate.Sample.Console
dotnet run --project examples/dotnet/Keygate.Sample.Desktop
dotnet run --project examples/dotnet/Keygate.Sample.AspNetCore
```

## API surface

| Member | Notes |
| --- | --- |
| `InitializeAsync()` | Loads local state, derives the device ID, creates the device Ed25519 key. Never touches the network, never fails when unactivated. |
| `ActivateAsync()` | `POST /api/v1/license/activate` with `identifier`, `device_public_key`, `device_key_alg`. Registers the device's report-signing key. |
| `ValidateLicenseAsync(forceOnline = false)` | `POST /api/v1/license/verify`; verifies the returned token locally; falls back to the cached token on network/5xx errors. |
| `CheckIntegrityAsync(installDirectory?)` | Verifies a signed manifest and the installed files; auto-reports mismatches (configurable). |
| `ReportSecurityEventAsync(SecurityEvent)` | `POST /api/v1/license/security-events`, signed with the device key. |
| `HasFeature(name)` | Entitlement check on the verified token. |
| `IsUpdateAllowed(publishedAt)` | Maintenance-window check (`upd`). |
| `StartPeriodicValidation()` / `StopPeriodicValidation()` | Background revalidation. |
| `DeactivateAsync()` | Releases the seat, keeps the device key so re-activation reuses the identity. |
| `DeviceId`, `Device`, `CurrentLicense` | Diagnostics. |

Errors: `KeygateConfigurationException` (fail fast), `KeygateNetworkException`
(DNS/TCP/TLS/timeout — **never** treated as tampering), `KeygateLicenseException`
(`Code` such as `TOKEN_EXPIRED`, `DEVICE_MISMATCH`, `MISSING_FEATURE`),
`KeygateApiException` (server envelope with `Code` and `StatusCode`).

A definitive server answer (400/401/403/404/409/410/422) is returned as an
invalid license and is **not** masked by the cache. Only transport errors and
5xx/429 fall back to the cached signed token.

## ASP.NET Core

```csharp
builder.Services.AddKeygate(o =>
{
    o.Protection.BaseUrl          = builder.Configuration["Keygate:BaseUrl"]!;
    o.Protection.LicenseKey       = builder.Configuration["Keygate:LicenseKey"];
    o.Protection.ProductId        = builder.Configuration["Keygate:ProductId"];
    o.Protection.LicensePublicKey = builder.Configuration["Keygate:PublicKey"];
    o.BlockOnInvalidLicense       = true;      // whole-app gate
    o.PeriodicValidationInterval  = TimeSpan.FromHours(6);
});

var app = builder.Build();
app.UseKeygate();       // license gate + X-Powered-By attribution
app.MapControllers();
```

* `AddKeygate` registers a singleton `KeygateProtection` and a hosted service
  that validates at startup and starts the background timer.
* `UseKeygate` refuses licensed requests with **402** and
  `{ "error": { "code": "LICENSE_EXPIRED", … } }`. `/health`, `/healthz`,
  `/ready`, `/readyz`, `/livez` are excluded by default.
* `[RequireKeygateFeature("pro")]` on a controller/action returns 402 when the
  license is unusable and 403 when the feature is missing. Controllers only;
  for minimal APIs call `protection.HasFeature(...)` inside the endpoint.
* `HttpContext.Items["Keygate.License"]` holds the verified `LicenseSnapshot`.

Deployment note: a server-side license binds to the **host**, so rotating
containers change the device ID. Either run with a stable machine identity
(pinned node, VM) or inject a stable `KeygateOptions.DeviceId` from your
platform (for example a cluster/tenant ID) — the SDK accepts a
`kgid:v1:<platform>:<64-hex>` value.

## Device binding

The device ID is `kgid:v1:<platform>:<sha256>`, computed exactly like
`pkg/deviceid` in the server (cross-checked by a test vector):

* per-component hash: `sha256("keygate:device:v1:" + name + ":" + normalized)`
* composite: `sha256("keygate:device:v1:composite:" + platform + ":" + k=v;…)`

Components are hardware/OS facts only: macOS `IOPlatformUUID`, volume UUID, CPU
model, primary NIC MAC; Windows `MachineGuid`, CPU name, baseboard
serial/product UUID, volume serial, MAC; Linux `product_uuid`/`board_serial`,
`machine-id`, root filesystem UUID, CPU model, MAC. Hostname is **off** by
default (weak entropy, changes often). Raw serials never leave the process —
only domain-separated hashes.

If no component is readable (locked-down container), the SDK falls back to a
random 64-hex digest persisted locally and reports `Device.IsHardwareDerived =
false`.

## File integrity

```csharp
options.IntegrityManifestPath     = "manifest.json";     // or IntegrityManifestUri
options.IntegrityInstallDirectory = AppContext.BaseDirectory;
options.ReleasePublicKeys["rel_..."] = "…64 hex…";       // pinned, from /api/v1/releases/:slug/signing-key

IntegrityCheckResult result = await protection.CheckIntegrityAsync();
```

The manifest is the server's `pkg/integrity` envelope:
`{ format: "keygate-integrity-v1", payload: base64url(json), signature: base64url(ed25519 over the payload TEXT), signing_key_id, algorithm: "ed25519" }`.

Rules enforced:

* SHA-256 + size per file; forward-slash relative paths only; no `..`, no
  absolute paths, no symlinks/reparse points, resolved path must stay inside
  the install directory.
* Unreadable/locked files go to `Errors` and **never** count as tampering.
* A bad signature is `ManifestError` and is reported as `manifest_invalid`.
* Only application files belong in the manifest — never logs, temp files,
  caches, or user data.

## Security events and server policies

```csharp
await protection.ReportSecurityEventAsync(new SecurityEvent
{
    Type = SecurityEventTypes.IntegrityViolation,
    Severity = "high",
    Details = { ["source"] = "sdk", ["first_path"] = "app.dll" },
});
```

Signed canonical string (mirrored from `pkg/securityevent`, test-vector
checked):

```
keygate-security-event-v1
<event_id>
<license_id>
<device_id>
<event_type>
<occurred_at_unix>
```

* The license key, token and details are **not** signed and never needed to
  justify an action.
* `event_id` is 1–128 chars of `[A-Za-z0-9._-]`; reusing one makes the report
  idempotent (server answers `duplicate: true`).
* Details: max 16 entries, key `[a-z][a-z0-9_.-]{0,63}`, value ≤ 512 bytes;
  keys matching `token|license_key|private_key|secret|password|authorization|signature|cookie`
  are dropped client-side, and the server sanitizes again.
* `occurred_at` must be within 10 minutes in the future and 7 days in the past.
* A report needs an activated + validated license; otherwise the SDK throws
  `KeygateSecurityEventException` instead of sending an unattributable event.

Server-side reactions (`security_event_policies`, actions
`log|flag|reject|block_activation|suspend|revoke`):

* Unverified reports (no registered device key, bad signature) are stored but
  can **never** change license state.
* Built-in defaults are conservative: `integrity_violation` → `suspend` only
  after 3 verified events in 24h; everything else `flag`/`log`; nothing
  auto-revokes.
* `revoke` requires a threshold ≥ 2 and cannot be product-wide (`*`).
  Admin revoke requires `confirm_revoke=true` and a confirmed event.
* Every state change is written to the audit log and fires the usual webhook.

Rate limiting: per-license flood guard plus the public endpoint limiter. A 429
is not tampering; treat it as "try later" and never block the user because of
it.

## Limitations (read before relying on this)

* Client-side protection is **deterrence, not proof**. A determined attacker
  with local code execution can patch the client, hook the checks, or replay a
  valid token on the same device. The SDK raises the cost; it does not make
  copying impossible.
* Device IDs are hardware-derived but not attested. A VM clone with identical
  UUIDs/MACs looks like the same device. Use server-side seat limits and
  review events; do not promise hardware attestation.
* Offline mode is bounded by the token's `exp` (and `vun` + grace). The clock
  is the machine's clock: a user can move it backwards. Combine with periodic
  online checks if that matters.
* The local secret store is DPAPI on Windows and `0600` files on macOS/Linux.
  That stops other users and casual inspection, not malware running as the
  same user. Supply your own `IKeygateSecretStore` (Keychain, Credential
  Manager, HSM-backed) for stronger isolation.
* Integrity checking covers files, not memory. It also cannot protect the
  manifest if the attacker can replace both the manifest and the pinned key —
  hence pinning keys inside the binary.
* No absolute security is claimed anywhere in this SDK.

## Production checklist

1. **Pin** `LicensePublicKey` and `ReleasePublicKeys`; do not rely on the
   first-run fetch from `/api/v1/license/pubkey`.
2. Use `https` (`AllowInsecureHttp` is for local development only — a license
   key is a bearer credential).
3. Set `ProductId` so a token for another product cannot be replayed.
4. Keep `AutoReportIntegrityFailures` on but treat integrity failure as
   "warn + report" for the first occurrence; only escalate on repeated verified
   events (that is what the default policy does).
5. Never branch security logic on `NetworkError`; it means "unknown".
6. Rotate signing keys with overlapping entries in `LicensePublicKeys` /
   `ReleasePublicKeys` before removing the old one.
7. Log `ProblemCode`, never the license key or the token.
8. Keep the `X-Powered-By: Keygate` header and the "Powered by Keygate" notice
   — AGPL v3 Section 7(b) requires the attribution, and it is not removable.

## Tests

```bash
export PATH="$HOME/.dotnet:$PATH"
dotnet test sdk/dotnet/Keygate.Protection.Tests
```

Covered: device-ID and security-event-signature vectors from the Go server,
license token verification (valid/expired/wrong device/wrong product/tampered),
signed manifest verification with modified/missing files and path traversal,
detail sanitizer, activation → validate → offline fallback → signed report
against a fake HTTP handler, and activation failure mapping.

Server side:

```bash
TEST_DATABASE_URL=postgres://user@localhost:5432/keygate_test?sslmode=disable \
  go test -p 1 ./internal/handler -run TestSecurityEvent -count=1
```
