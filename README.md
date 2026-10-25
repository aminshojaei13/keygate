<img src="web/public/logo.svg" alt="Keygate" width="72" /># Keygate

**Open source software license management platform.**

The self-hosted alternative to Keygen, Cryptlex, and LicenseSpring.

[Website](https://keygate.app) · [Documentation](https://keygate.app/docs) · [Community](https://github.com/tabloy/keygate/discussions)

![License](https://img.shields.io/badge/license-AGPL%20v3-blue.svg)![Release](https://img.shields.io/github/v/release/tabloy/keygate?label=release&color=green)![Stars](https://img.shields.io/github/stars/tabloy/keygate?style=flat)![Sponsor](https://img.shields.io/badge/sponsor-%E2%9D%A4-ff69b4)[**English**](README.md) · [**简体中文**](README.zh-CN.md) · [**فارسی**](https://github.com/aminshojaei13/keygate/blob/update-docs/docs/team-guide/README.md)

> **Fork:** `aminshojaei13/keygate` — a fork of `tabloy/keygate`. Active work lives on the `update-docs`branch; `main` tracks upstream. See [What's in this fork](#whats-in-this-fork) below.

<img src="web/public/screenshot.png" alt="Keygate Dashboard" width="800" />

## Why Keygate?

You've built great software. Now you need to decide who can use it, how they pay for it, and what features they get access to.

Commercial license platforms charge per-seat, per-month, and your customer data lives on someone else's servers. Building your own takes months of engineering on activation logic, payment webhooks, quota tracking, and all the edge cases that come at 2 AM.

**Keygate is the middle ground.** A production-ready license server you deploy on your own infrastructure, connect to your own Stripe, and manage through a clean dashboard. It handles everything from activation to dunning — so you can focus on building your product.

One server. One database. Full control. Free, forever.

## Who is it for?

|  |  |
| --- | --- |
| **🧑‍💻 Indie Developers** — Selling a desktop app, CLI tool, or Electron app? Keygate handles license keys, activation limits, and trials so you can focus on shipping. | **🏢 SaaS Companies** — Managing subscription tiers with different feature sets? Define plans with entitlements, track usage, and let Stripe handle billing automatically. |
| **🏭 Enterprise Vendors** — Need floating licenses for large teams? Concurrent seat checkout with heartbeat monitoring, perfect for shared-seat environments. | **⚡ API Providers** — Enforcing rate limits and usage quotas? Atomic quota enforcement tracks every call and warns customers before they hit limits. |

## Features

### 🔑 License Management

Every model in one platform — **subscriptions**, **perpetual**, **perpetual with a maintenance period** (buy once, get a year of updates, renew from the portal), **trials**, and **floating** (concurrent) licenses. Create, activate, verify, suspend, reinstate, and revoke with full audit trail. Per-device or per-user activation limits with **atomic enforcement** (no double-counting under retry). Grace periods. License keys hashed with SHA-256, encrypted at rest. Signed tokens for offline verification. `Idempotency-Key` **header** support on writes — retries never duplicate.

Public SDK endpoints (activate / verify / deactivate / usage / download) take `license_key` directly — no embedded API keys to leak from your binaries. Customers can self-serve activation slots from the portal (free up a lost laptop without a support ticket).

### 🚀 Software Distribution

Ship signed updates to your installed clients. **Sparkle** (macOS), **Velopack** (Windows), and **Tauri** (cross-platform) updaters all consume the same release feed — one publish, every updater compatible. Per-platform binaries grouped under a single release, **atomic publish gate** (no half-uploaded releases ever leak), **yank** for instant rollback. Per-product **Ed25519 signing keys** with private keys encrypted at rest under AES-256-GCM + HKDF-derived subkeys. Server-side SHA-256 for integrity (never trust the client's hash). Stable feeds are public — your customers' auto-updater never breaks when a license rotates. Products sold with a maintenance period turn on `feed_license_required` — the updater then sends the license and only sees releases published inside the customer's update period, while the app itself keeps running either way. Per-product `minimum_supported_version` floor for forced upgrades.

Object storage is S3-compatible — Cloudflare R2, AWS S3, MinIO, anything that speaks SigV4. Presigned URLs for direct browser upload (no proxying through Keygate), and license-gated short-TTL download URLs.

### 📊 Usage Metering

Track API calls, storage, bandwidth, or any custom metric. Quotas enforced **atomically at the database level** — even under high concurrency, limits are never exceeded. Hourly, daily, monthly, or yearly cycles with automatic reset. Threshold warnings via webhooks.

### 💳 Payments

Stripe integrated end-to-end with **three-layer reliability** — webhook, success-page verification, and periodic sync ensure no payment is ever missed. Customer pays → license created automatically. Payment fails → dunning emails on schedule. Supports checkout, plan upgrades/downgrades with proration, cancellations, refunds, billing portal, and one-time renewals of a perpetual license's update period from the customer portal. Stripe webhook is auto-configured — just set your API key.

### 👥 Team Seats & Entitlements

Customers manage their own teams within a license. Seat roles (owner/admin/member), configurable limits per plan. Feature entitlements as boolean flags, numeric limits, or usage quotas. Purchasable add-ons that extend plan capabilities.

### 🔧 Server-to-Server API

Programmatic admin access via `Authorization: Bearer kg_live_…` — mint licenses from your Stripe webhook, run nightly usage exports from cron, automate everything `/admin/*` can do. **Scope-based authorization** with fail-closed defaults (an API key with no scopes can do nothing). System-wide admin keys or per-product keys — same model as Stripe `sk_live_` and GitHub PATs.

### 📈 Admin Dashboard

Products, plans, licenses, customers, API keys, webhooks, analytics, audit logs, team management, email templates, and brand customization — all from one interface. Search, filter, and export (CSV/JSON).

### 🛡️ Security

Email OTP login with constant-time hash verification, role-based access checked per-request from database, brute-force protection, rate limiting, HMAC-signed webhooks, SameSite cookies, HSTS, and startup validation that rejects weak secrets. License verify endpoints collapse all "license-knowable" failures to a single 404 so `license_key` enumeration is closed off. Idempotency-Key middleware prevents double-execution on retried writes.

### 🌍 Self-Hosted

A Go server + PostgreSQL + (optional) S3-compatible storage for release artifacts. No Redis, no microservices. Auto-migration on startup. Setup wizard for first run. Custom branding, email templates, and i18n (English/Chinese built-in).

## Quick Start

**Requirements:** Docker 24+ and Docker Compose v2.24+ (the compose file uses `env_file: required: false`). Nothing else is needed — Go, Node and PostgreSQL all run inside containers.

### 1. Clone

```bash
git clone https://github.com/aminshojaei13/keygate.git
cd keygate

# The fork's work: strict device binding, offline tokens, integrity
# manifests, admin Devices/Integration pages, and SDK samples
git switch update-docs
```

### 2. Generate config and secrets

```bash
cp .env.example .env
sed -i.bak "s|^JWT_SECRET=.*|JWT_SECRET=$(openssl rand -hex 32)|" .env && rm -f .env.bak
sed -i.bak "s|^LICENSE_SIGNING_KEY=.*|LICENSE_SIGNING_KEY=$(openssl rand -hex 32)|" .env && rm -f .env.bak
```

Optional, but recommended: set `ADMIN_EMAILS=you@example.com` in `.env`. When it is set, only that address can claim the first-run owner account.

`.env.example` enables `STRICT_DEVICE_BINDING=true`: a license is bound to the first hardware-derived identifier, and a different device is refused with `409 DEVICE_CHANGED`. See Authentication & security §12.4 for the client checklist and the admin reset action.

### 3. Build and start

```bash
docker compose up -d --build
```

`docker-compose.override.yml` builds the image from this checkout (instead of pulling the upstream image), so the code you cloned is the code that runs.

### 4. Open the app

<http://localhost:9000> — the setup wizard creates the owner, product and default plan.

First login uses an email OTP. Without SMTP configured, the code is printed in the logs:

```bash
docker compose logs -f keygate | grep -i otp
```

Health check and useful commands:

```bash
curl -s http://localhost:9000/health
docker compose logs -f keygate     # follow logs
docker compose down                # stop (data is kept)
git pull && docker compose up -d --build   # update
docker compose down -v             # ⚠️ stop AND delete the database volume
```

### From source (optional)

Use this only if you want to run the binaries directly. You need Go 1.27+, Bun/Node for the frontend, and PostgreSQL 18.

```bash
# 1. PostgreSQL (the compose file does not publish a host port)
docker run -d --name keygate-pg -p 5432:5432 \
  -e POSTGRES_USER=keygate -e POSTGRES_PASSWORD=keygate -e POSTGRES_DB=keygate \
  postgres:18-alpine

# 2. Config (same secret generation as above)
cp .env.example .env
sed -i.bak "s|^JWT_SECRET=.*|JWT_SECRET=$(openssl rand -hex 32)|" .env && rm -f .env.bak
sed -i.bak "s|^LICENSE_SIGNING_KEY=.*|LICENSE_SIGNING_KEY=$(openssl rand -hex 32)|" .env && rm -f .env.bak

# 3. Build and run
make build
./bin/keygate
```

Open <http://localhost:9000> (migrations run automatically on startup).

### Troubleshooting

| Symptom | Fix |
| --- | --- |
| `Set JWT_SECRET in .env` / `LICENSE_SIGNING_KEY` | Run step 2 again; the lines must not be empty. |
| Port 9000 already in use | Stop the other service, or change `ports` to `"9001:9000"` in `docker-compose.override.yml`. |
| `SETUP_EMAIL_NOT_ALLOWED` | The setup email must be listed in `ADMIN_EMAILS` (or clear `ADMIN_EMAILS`). |
| No login email | Check \`docker compose logs -f keygate |
| Reset everything (destructive) | `docker compose down -v` then repeat step 3. |
| Behind a reverse proxy | Set `BASE_URL=https://your.domain` in `.env` and terminate TLS at the proxy. |

## What's in this fork

The `update-docs`branch adds, on top of upstream:

- **Strict device binding** (default): one active device per license, exact hardware-derived identity (`kgid:v1` or `sha256:`), no client/portal slot release, and no `previous_identifier` migration. Activation with a changed identifier is refused with `409 DEVICE_CHANGED` (verification returns `404`); an admin can move the license with `POST /admin/licenses/:id/reset-device-binding`.
- **Device identity protocol** (`kgid:v1`) with scheme detection. Weighted drift scoring is defined and tested in `pkg/deviceid` but not wired to activate/verify; setting `STRICT_DEVICE_BINDING=false` currently restores multi-device limits and migration.
- **Activation lifecycle**: soft-delete tombstones plus admin deactivate/reactivate, license transfer, and signed offline-token issuance.
- **Offline tokens** with `ver`/`kid` and key rotation, discoverable at `GET /api/v1/license/pubkey` and `GET /api/v1/releases/:product_slug/signing-key`.
- **Signed integrity manifests** (`keygate-integrity-v1`) with `cmd/keygate-manifest`(`create` / `verify` / `inspect`).
- **Admin UI**: Devices page, Integration page, offline-token inspector, and expanded license actions.
- **Client samples**: C# for .NET 8 and .NET Core 3.1, a dependency-free Python SDK, and Python/TypeScript examples.
- **Documentation**: a 13-file Persian team guide plus protocol specifications.

## Documentation

| Guide | What it covers |
| --- | --- |
| [**Persian team guide**](https://github.com/aminshojaei13/keygate/blob/update-docs/docs/team-guide/README.md) | Setup, architecture, security, deployment, troubleshooting (فارسی) |
| [API reference](https://github.com/aminshojaei13/keygate/blob/update-docs/docs/team-guide/api-reference.md) | Every registered endpoint, including strict-binding errors and admin reset |
| [C# / .NET integration](https://github.com/aminshojaei13/keygate/blob/update-docs/docs/team-guide/csharp-dotnet-integration.md) | Activation, verification, offline tokens, samples |
| [C# samples](https://github.com/aminshojaei13/keygate/tree/update-docs/examples/dotnet) | `Keygate.Sample` (net8.0) and `Keygate.Sample.NetCore31` |
| [Python SDK](https://github.com/aminshojaei13/keygate/tree/update-docs/sdk/python) | Zero-dependency client, offline verifier, manifest verifier |
| [Device identity spec](https://github.com/aminshojaei13/keygate/blob/update-docs/docs/device-identity.md) | Strict binding, `kgid:v1`, tolerant multi-device mode |
| [Offline licensing spec](https://github.com/aminshojaei13/keygate/blob/update-docs/docs/offline-licensing.md) | Token format, claims, verification rules |
| [Integrity manifest spec](https://github.com/aminshojaei13/keygate/blob/update-docs/docs/integrity-manifest-format.md) | Envelope, canonicalization, signing |
| [Deployment & configuration](https://github.com/aminshojaei13/keygate/tree/update-docs/docs) | Production install, backups, key management |
| [keygate.app/docs](https://keygate.app/docs) | Upstream public documentation |

> All `docs/team-guide/*` links point at the `update-docs` branch. Once that branch is merged into `main`, they can be relative paths again.

## Compared to Alternatives

|  | **Keygate** | Keygen | Cryptlex | LicenseSpring |
| --- | --- | --- | --- | --- |
| Open source | **✅ AGPL v3** | Partial | ❌ | ❌ |
| Self-hosted | **✅** | ✅ | ❌ | ❌ |
| Price | **Free** | From $99/mo | From $249/mo | From $50/mo |
| Floating licenses | ✅ | ✅ | ✅ | ✅ |
| Usage metering | **✅** | ❌ | ❌ | ❌ |
| Built-in payments | **✅** | ❌ | ❌ | ❌ |
| Auto-update distribution | **✅ Sparkle / Velopack / Tauri** | ✅ Paid add-on | ❌ | ❌ |
| Customer portal | ✅ | ❌ | ✅ | ✅ |
| Admin dashboard | ✅ | ✅ | ✅ | ✅ |
| Webhook system | ✅ | ✅ | ✅ | ✅ |
| Audit trail | ✅ | ✅ | ❌ | ❌ |
| Idempotency-Key | **✅** | ❌ | ❌ | ❌ |
| i18n | ✅ | ❌ | ❌ | ❌ |

## Community

- [**Discussions**](https://github.com/tabloy/keygate/discussions) — Questions, ideas, show & tell
- [**Issues**](https://github.com/tabloy/keygate/issues) — Bug reports and feature requests
- [**Blog**](https://keygate.app/blog) — Updates and engineering stories
- [**Sponsor**](https://keygate.app/sponsorships) — Support the project

## Sponsors

Keygate is free and self-hosted. If it saves you the cost of a hosted licensing service, [sponsoring on Ko-fi](https://ko-fi.com/alanisme) keeps development going.

*Be the first.*

## Contributing

All contributions welcome — bugs, features, docs, translations. Check [open issues](https://github.com/tabloy/keygate/issues) or start a [discussion](https://github.com/tabloy/keygate/discussions), then submit a PR.

## Commercial License

Using Keygate is free, commercially included. Run it for your own business, license the software you sell with it, or serve your own customers from it, and you owe nothing.

The AGPL asks two things in return: the **"Powered by Keygate"** line stays visible in the interface, and if you modify Keygate and let others use it, your changes are published. A commercial license lifts both, which is what white labelling or reselling it as a closed service needs.

Contact [hello@keygate.app](mailto:hello@keygate.app).

## License

[AGPL v3 License](LICENSE) with additional terms per [Section 7(b)](https://www.gnu.org/licenses/agpl-3.0.en.html#section7) — Copyright © 2026 [Tabloy](https://tabloy.app)

You are free to fork, modify, and self-host this software under the AGPL v3. The **"Powered by Keygate"** attribution in the UI must be preserved (see [NOTICE](NOTICE)).

## Star History

<img src="https://api.star-history.com/svg?repos=tabloy/keygate&amp;type=Date" alt="Star History Chart" width="600" />---

If Keygate helps your business, consider giving it a ⭐