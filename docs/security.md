# Keygate Production Security & Hardening Guide

> **Status:** Production Standard  
> **Target Audience:** Security Engineers, DevOps Engineers, System Administrators  
> **Attribution:** *Powered by Keygate* (AGPL v3 Section 7(b))

---

## 1. Security Architecture & Threat Boundary

Keygate provides a zero-trust software licensing and distribution perimeter.

```
       Untrusted Clients / Browsers           Trusted Perimeter
   ┌───────────────────────────────────┐    ┌──────────────────────────────────┐
   │ - Desktop Apps (macOS/Win/Linux)  │    │ - Reverse Proxy (TLS 1.3 Term.)  │
   │ - Web Browsers (Customer Portal)  │───>│ - Keygate Server (Non-root)      │
   │ - Admin Browsers (Admin Console)  │    │ - PostgreSQL (Encrypted At-Rest) │
   │ - Payment Webhooks (Stripe)       │    │ - S3 Bucket (Private / Presigned)│
   └───────────────────────────────────┘    └──────────────────────────────────┘
```

### Core Security Guarantees:
1. **Zero Client Secrets:** Private Ed25519 signing keys and AES-256-GCM master keys **never** leave the server. SDKs and client runtimes only possess public keys.
2. **Domain-Separated Cryptography:** Separate algorithms and keys for session tokens (HMAC-SHA256), offline licensing (Ed25519), release signing (Ed25519), and at-rest field encryption (AES-256-GCM with AAD).
3. **Database At-Rest Encryption:** License keys and release signing private keys are encrypted in PostgreSQL using AES-256-GCM with Additional Authenticated Data (`AAD = product_id`), preventing ciphertext tampering or cross-tenant substitution.
4. **Least Privilege Container Runtime:** Keygate Docker image runs as an unprivileged user `keygate` (UID 10001, GID 10001) with `no-new-privileges:true`.

---

## 2. Hardening Checklist

### 2.1 Transport Layer Security (TLS)
- **Mandatory HTTPS:** In production, `ENVIRONMENT=production` and `BASE_URL=https://...` must be set. Keygate validates that session cookies carry the `Secure` flag.
- **TLS Version:** Enforce TLS 1.3 (or modern TLS 1.2) at the reverse proxy (Caddy / Nginx / Cloudflare).
- **HSTS:** Enable HTTP Strict Transport Security (`max-age=63072000; includeSubDomains; preload`).

### 2.2 Secret & Key Management
- **Entropy Requirement:** All secrets (`JWT_SECRET`, `LICENSE_SIGNING_KEY`, `RELEASE_KEY_ENCRYPTION_KEY`, `POSTGRES_PASSWORD`) must be generated with at least 256 bits of cryptographic entropy (`openssl rand -hex 32`).
- **No Committed Secrets:** Never commit `.env` or database backup dumps (`*.sql`) to version control. The repository's `.gitignore` and `.dockerignore` strictly exclude them.
- **KMS / Vault Storage:** Store `RELEASE_KEY_ENCRYPTION_KEY` and `LICENSE_SIGNING_KEY` in AWS KMS, HashiCorp Vault, or 1Password Secrets Automation. Loss of `RELEASE_KEY_ENCRYPTION_KEY` makes existing encrypted database records permanently unreadable.

### 2.3 Network & Firewall Controls
- **PostgreSQL Isolation:** Port 5432 should **never** be exposed to the public internet. Use Docker internal networks (`keygate-net`) or a private VPC subnet.
- **SSRF Prevention:** `WEBHOOK_ALLOW_PRIVATE` is set to `false` by default, blocking webhook deliveries to internal/loopback IP addresses (127.0.0.1, 10.0.0.0/8, 192.168.0.0/16, 169.254.169.254).
- **Rate Limiting & Anti-Abuse:** Built-in rate limiters protect public endpoints:
  - `/api/v1/license/*`: 60 requests/minute per client.
  - `/auth/otp/send`: 30 emails/hour per IP (prevents mail-relay abuse).
  - Brute-force lockout: 5 failed attempts locks IP for 30 seconds.

### 2.4 Container Hardening
- **Non-Root Execution:** The container runs as unprivileged user `keygate` (UID 10001).
- **Container Security Options:** `security_opt: ["no-new-privileges:true"]`.
- **Log Rotation:** Docker compose limits container logs to 10MB per file with 3 files retention, preventing disk exhaustion attacks.
- **Health Checks:** Built-in container healthchecks monitor `/health` and PostgreSQL connectivity.

---

## 3. Incident Response & Key Compromise

### License Signing Key Compromise
If `LICENSE_SIGNING_KEY` is suspected to be compromised:
1. Generate a new Ed25519 seed: `openssl rand -hex 32`.
2. Update `LICENSE_SIGNING_KEY` in `.env` and restart Keygate.
3. Publish an application update containing the new public key in the client's verification keyring.
4. Old tokens will be rejected as soon as clients receive the updated keyring.

### Release Signing Key Compromise
1. Log in to the Admin Dashboard (`/admin/products/:id`).
2. Navigate to **Signing Keys** and click **Rotate Key**.
3. The server immediately transitions the old key to `retired` and signs all subsequent releases with a newly generated key ID (`kid`).
4. Yank any compromised releases (`POST /admin/releases/:id/yank`).
