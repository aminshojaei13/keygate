# Keygate Configuration & Secrets Reference

> **Status:** Production Standard  
> **Target Audience:** DevOps Engineers, Security Architects, System Administrators  
> **Attribution:** *Powered by Keygate* (AGPL v3 Section 7(b))

---

## 1. Environment Variable Reference

All configuration is provided via environment variables (or `.env` file).

| Variable | Type | Required | Default | Description & Security Implications |
|---|---|---|---|---|
| `PORT` | Integer | No | `9000` | Port on which the HTTP server listens inside the container. |
| `ENVIRONMENT` | String | No | `development` | Runtime environment: `development`, `staging`, or `production`. `production` enforces strict security defaults (TLS warnings, no dev login). |
| `BASE_URL` | String | Yes | `http://localhost:9000` | Canonical public URL (e.g., `https://licensing.example.com`). Must use `https://` in production to allow secure session cookies. |
| `DATABASE_URL` | String | Yes* | *Derived* | Full PostgreSQL connection URI. If unset in Docker Compose, derived from `POSTGRES_*` credentials. |
| `POSTGRES_USER` | String | No | `keygate` | Database username for bundled Docker Compose PostgreSQL service. |
| `POSTGRES_PASSWORD` | String | Yes* | *None* | Database password. Required in production for Docker Compose. |
| `POSTGRES_DB` | String | No | `keygate` | Database name for bundled PostgreSQL service. |
| `JWT_SECRET` | String (Hex/Base64) | **Yes** | *None* | Secret key for signing web session tokens (HMAC-SHA256). Must be at least 32 characters. |
| `LICENSE_SIGNING_KEY` | String (Hex) | **Yes** | *None* | 32-byte Ed25519 seed encoded as 64 hex characters. Signs offline tokens and entitlement tokens. |
| `RELEASE_KEY_ENCRYPTION_KEY` | String (Hex) | **Conditional** | *None* | 32-byte AES-256-GCM master key (64 hex characters). Encrypts release signing keys and license keys at rest. Required if storage is enabled. |
| `MIGRATIONS_DIR` | String | No | `db/migrations` | Path to directory containing `.up.sql` database migrations. |
| `MIGRATE_ONLY` | Boolean | No | `false` | When set to `true`, applies pending database migrations and exits with status code 0. |
| `ADMIN_EMAILS` | Comma-separated | No | *Empty* | Comma-separated list of emails auto-promoted to `admin` / `owner` on initial setup bootstrap. |
| `STRIPE_SECRET_KEY` | String | No | *Empty* | Stripe Secret Key (`sk_live_...` or `sk_test_...`). Required for payment processing. |
| `STRIPE_WEBHOOK_SECRET`| String | No | *Empty* | Stripe webhook signing secret (`whsec_...`). |
| `STRIPE_LIVEMODE` | Boolean | No | *Auto* | Auto-derived from `STRIPE_SECRET_KEY` prefix (`sk_live_` -> true, `sk_test_` -> false) unless set. |
| `SMTP_HOST` | String | No | *Empty* | SMTP server hostname. If unset, OTP login codes are printed to stdout logs. |
| `SMTP_PORT` | Integer | No | `587` | SMTP port (typically 587 for STARTTLS or 465 for TLS). |
| `SMTP_USERNAME` | String | No | *Empty* | SMTP authentication username. |
| `SMTP_PASSWORD` | String | No | *Empty* | SMTP authentication password. |
| `SMTP_FROM` | String | No | *Empty* | Sender address for outgoing emails (e.g., `noreply@example.com` or `Licensing <noreply@example.com>`). |
| `REDIS_URL` | String | No | *Empty* | Redis connection URI (e.g., `redis://:password@host:6379/0`). Enables shared rate limiting across multi-instance clusters. |
| `RATE_LIMIT_API` | Integer | No | `60` | Public API rate limit (requests per minute per client). |
| `RATE_LIMIT_ADMIN` | Integer | No | `120` | Admin API rate limit (requests per minute). |
| `RATE_LIMIT_AUTH` | Integer | No | `60` | Login / OTP submission limit per IP per minute. |
| `RATE_LIMIT_OTP_SEND` | Integer | No | `30` | OTP code dispatch limit per IP per hour (prevents email relay spam). |
| `BF_MAX_FAILS` | Integer | No | `5` | Maximum failed license key attempts before IP lockout. |
| `BF_LOCKOUT_SECONDS` | Integer | No | `30` | Duration in seconds of brute-force IP lockout. |
| `WEBHOOK_MAX_ATTEMPTS` | Integer | No | `5` | Maximum delivery attempts for outbound webhooks with exponential backoff. |
| `WEBHOOK_RETRY_INTERVAL` | Duration | No | `30s` | Initial retry delay for failed webhook deliveries. |
| `WEBHOOK_HTTP_TIMEOUT` | Duration | No | `10s` | Timeout for outbound webhook HTTP requests. |
| `WEBHOOK_ALLOW_PRIVATE` | Boolean | No | `false` | When `false`, blocks SSRF by rejecting webhook targets on loopback, private, or link-local IPs. |
| `QUOTA_WARNING_THRESHOLD` | Float | No | `0.8` | Threshold (0.0 to 1.0) at which metered usage warnings trigger webhooks. |
| `STORAGE_ENDPOINT` | String | No | *Empty* | Custom S3 endpoint (e.g., `https://<account>.r2.cloudflarestorage.com` or `http://minio:9000`). Empty for AWS S3. |
| `STORAGE_REGION` | String | No | `auto` | Object storage region (e.g., `us-east-1` or `auto` for Cloudflare R2). |
| `STORAGE_BUCKET` | String | No | *Empty* | S3 bucket name for release binaries and updates. |
| `STORAGE_ACCESS_KEY` | String | No | *Empty* | Object storage access key ID. |
| `STORAGE_SECRET_KEY` | String | No | *Empty* | Object storage secret access key. |
| `STORAGE_FORCE_PATH_STYLE`| Boolean | No | `false` | Must be `true` for MinIO or self-hosted S3 endpoints. |
| `STORAGE_UPLOAD_TTL` | Duration | No | `1h` | Lifetime of presigned artifact upload URLs. |
| `STORAGE_DOWNLOAD_TTL` | Duration | No | `10m` | Lifetime of license-gated customer download URLs. |
| `STORAGE_FEED_URL_TTL` | Duration | No | `24h` | Lifetime of artifact URLs in public updater feeds (Sparkle/Velopack/Tauri). |
| `MAX_RELEASE_SIGN_SIZE_MB`| Integer | No | `500` | Max artifact size (MB) loaded into memory for server-side Ed25519 signing. |

---

## 2. Secret Provisioning & Entropy Standards

### Generation Commands
All production secrets must be generated using cryptographically secure random sources (`/dev/urandom` via `openssl`):

```bash
# JWT Session Secret (HMAC-SHA256, 256 bits)
openssl rand -hex 32

# License Signing Private Key Seed (Ed25519, 256 bits)
openssl rand -hex 32

# Master At-Rest Encryption Key (AES-256-GCM, 256 bits)
openssl rand -hex 32

# PostgreSQL Database Password (High Entropy Alphanumeric)
openssl rand -base64 24 | tr -dc 'a-zA-Z0-9'
```

### Prohibited Practices
- **Never embed secrets in Docker images:** All private keys, database passwords, and API tokens must be injected at container runtime via environment variables or secret mounts.
- **Never embed private signing keys in client packages:** Client applications and SDKs must only receive the public verification key (retrieved online via `/api/v1/license/pubkey` or bundled as a public verification keyring).
- **Never commit `.env` to version control:** The `.gitignore` and `.dockerignore` files explicitly exclude `.env` and `.env.*`.

---

## 3. Cryptographic Key Provisioning & Rotation

### 3.1 License Signing Key (`LICENSE_SIGNING_KEY`)
- Key type: Ed25519 private seed.
- Used by server to sign offline license tokens (`keygate.offline.v1.<payload>.<sig>`).
- Clients verify tokens using the public half derived from this key.
- Public key endpoint: `GET /api/v1/license/pubkey`.
- Key rotation procedure:
  1. Generate new 32-byte hex seed.
  2. Deploy new key during an overlap window (`max_offline_ttl`, e.g. 30 days).
  3. Clients with cached keyrings continue validating existing tokens using the matching `kid` until re-syncing.

### 3.2 Product Release Signing Keys
- Managed per product via the admin dashboard or `POST /admin/products/:id/signing-key/rotate`.
- Ed25519 keypairs generated server-side.
- Private keys are stored in PostgreSQL encrypted with AES-256-GCM using `RELEASE_KEY_ENCRYPTION_KEY` with Additional Authenticated Data (`AAD = product_id`).
- When rotated:
  - Previous keys transition to `retired`.
  - Past released artifacts remain verifiable.
  - New artifacts are signed with the active key ID (`kid`).

### 3.3 Master Encryption Key (`RELEASE_KEY_ENCRYPTION_KEY`)
- If modified, all existing encrypted database columns must be re-encrypted.
- Loss of this key is **irrecoverable**. Maintain an offline, encrypted backup in an institutional vault or HSM.

---

## 4. Access Control & Authorization Model

Keygate enforces strict Role-Based Access Control (RBAC) and credential scoping:

1. **User Roles:**
   - `owner`: Full control over the entire system, organizations, billing, and cryptographic keys.
   - `admin`: Operational access (product creation, license issuance, webhook configuration). Cannot modify owner accounts or root signing keys.
   - `member`: Read-only access or customer portal self-service.
2. **API Keys (`kg_live_...`):**
   - SHA-256 salted hashes stored in the database.
   - Strict capability scopes (`licenses:read`, `licenses:write`, `releases:read`, `releases:write`, `usage:write`).
   - Gated from cryptographic key management.
3. **Session Authentication:**
   - Passwordless email OTP verification (time-limited, constant-time verification).
   - `HttpOnly`, `SameSite=Lax`, `Secure` session cookies.
