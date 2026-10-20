# Keygate Production Installation & Deployment Guide

> **Status:** Production Standard  
> **Target Audience:** DevOps Engineers, Site Reliability Engineers, System Administrators  
> **Legal Notice:** This software is licensed under AGPL v3 with Section 7(b) terms. The *"Powered by Keygate"* attribution notice is a mandatory, legally binding requirement across all UI surfaces and API responses.

---

## 1. Architecture Overview

Keygate is engineered for operational simplicity, resilience, and horizontal scalability with minimal external dependencies.

```
                    Internet (HTTPS / Port 443)
                                 │
                                 ▼
                     ┌───────────────────────┐
                     │ Reverse Proxy / TLS   │ (Caddy / Nginx / Cloudflare / ALB)
                     │ (Terminates TLS 1.3)  │
                     └───────────┬───────────┘
                                 │ HTTP (Port 9000)
                                 ▼
                     ┌───────────────────────┐
                     │    Keygate Server     │
                     │  (Go 1.27 / Non-Root) │
                     └───┬───────────────┬───┘
                         │               │
            SQL (Port 5432)              │ HTTPS (SigV4)
                         │               │
                         ▼               ▼
               ┌────────────────┐   ┌───────────────────────────┐
               │   PostgreSQL   │   │ Object Storage (Optional) │
               │   15-18 Alpine │   │ (Cloudflare R2 / AWS S3)  │
               └────────────────┘   └───────────────────────────┘
```

### Core Architecture Components
1. **Keygate Application Server:**
   - Single stateless compiled Go binary running inside a hardened, non-root Alpine container (`UID/GID 10001:10001`).
   - Serves the customer portal, administrative dashboard, public verification API, and webhook dispatcher.
   - Built-in automatic database migrations with PostgreSQL advisory locking (`pg_advisory_lock(7367616)`).
2. **PostgreSQL Database:**
   - Single source of truth for licenses, entitlements, activations, devices, audit logs, and encrypted keys.
   - Persistent volume storage on `/var/lib/postgresql`.
3. **Release Storage (Optional):**
   - S3-compatible object storage (AWS S3, Cloudflare R2, MinIO) for software binaries, installers, and auto-updater feeds (Sparkle, Velopack, Tauri).
4. **Redis / Valkey (Optional):**
   - Distributed rate-limit synchronization across multiple Keygate application instances. Gracefully degrades to in-memory rate limiting if unreachable.

---

## 2. System Prerequisites & Sizing

| Metric | Minimum (Small / Indie) | Recommended (Medium / SaaS) | Enterprise (High Concurrency) |
|---|---|---|---|
| **CPU** | 1 vCPU | 2 vCPUs | 4+ vCPUs |
| **RAM** | 512 MB | 2 GB | 4 GB+ |
| **Database Storage** | 10 GB SSD | 50 GB NVMe | 100 GB+ NVMe |
| **OS** | Linux (Ubuntu 22.04+, Debian 12+, RHEL 9+, Alpine 3.20+) | Linux | Linux |
| **Docker** | Docker Engine 24.0+ & Docker Compose 2.24+ | Same | Same |

---

## 3. Production Deployment with Docker Compose

### Step 1: Directory Setup
Create a dedicated service directory with restricted permissions:
```bash
sudo mkdir -p /opt/keygate
sudo chown -R $USER:$USER /opt/keygate
cd /opt/keygate
```

### Step 2: Fetch Configuration Templates
```bash
curl -fsSL -O https://raw.githubusercontent.com/tabloy/keygate/main/docker-compose.yml
curl -fsSL -O https://raw.githubusercontent.com/tabloy/keygate/main/.env.example
cp .env.example .env
chmod 600 .env
```

### Step 3: Cryptographic Secret Generation
Run the following commands to generate production secrets:
```bash
# 1. JWT session secret (HMAC-SHA256, 64 hex characters)
JWT_SECRET=$(openssl rand -hex 32)

# 2. License signing private seed (Ed25519, 64 hex characters)
LICENSE_SIGNING_KEY=$(openssl rand -hex 32)

# 3. Master at-rest encryption key (AES-256-GCM, 64 hex characters)
RELEASE_KEY_ENCRYPTION_KEY=$(openssl rand -hex 32)

# 4. Database password (alphanumeric, 32 characters)
POSTGRES_PASSWORD=$(openssl rand -base64 24 | tr -dc 'a-zA-Z0-9')
```

Inject these into `.env` (or edit with a text editor):
```bash
sed -i.bak "s|^JWT_SECRET=.*|JWT_SECRET=${JWT_SECRET}|" .env
sed -i.bak "s|^LICENSE_SIGNING_KEY=.*|LICENSE_SIGNING_KEY=${LICENSE_SIGNING_KEY}|" .env
sed -i.bak "s|^RELEASE_KEY_ENCRYPTION_KEY=.*|RELEASE_KEY_ENCRYPTION_KEY=${RELEASE_KEY_ENCRYPTION_KEY}|" .env
sed -i.bak "s|^POSTGRES_PASSWORD=.*|POSTGRES_PASSWORD=${POSTGRES_PASSWORD}|" .env
rm -f .env.bak
```

> [!CAUTION]
> **CRITICAL SECURITY REQUIREMENT:**
> Store `RELEASE_KEY_ENCRYPTION_KEY` and `LICENSE_SIGNING_KEY` in an encrypted password manager or enterprise KMS.
> If `RELEASE_KEY_ENCRYPTION_KEY` is lost, encrypted license keys and signing keys in the database **cannot be decrypted or recovered**.

### Step 4: Configure Domain, Email & Admin Bootstrap
Edit `.env` and set:
```ini
ENVIRONMENT=production
BASE_URL=https://licensing.yourdomain.com
ADMIN_EMAILS=security@yourdomain.com,ops@yourdomain.com

# SMTP configuration for login OTP codes and transaction emails
SMTP_HOST=smtp.postmarkapp.com
SMTP_PORT=587
SMTP_USERNAME=your-smtp-token
SMTP_PASSWORD=your-smtp-password
SMTP_FROM=Keygate Licensing <licensing@yourdomain.com>
```

### Step 5: Start Keygate
```bash
docker compose up -d
```

### Step 6: Verify Deployment Health
Check service health status:
```bash
docker compose ps
```
Both `keygate` and `postgres` should report `healthy`.

Verify the HTTP healthcheck endpoint:
```bash
curl -s http://localhost:9000/health | jq .
```
Expected output:
```json
{
  "status": "ok",
  "checks": {
    "database": "ok"
  }
}
```

---

## 4. Reverse Proxy & TLS Configuration

In production, Keygate **must run behind TLS 1.3** to protect session cookies, API tokens, and client credentials. Plain HTTP will cause browsers to reject secure session cookies and trigger server security warnings.

### Option A: Caddy (Recommended)
Caddy provides automated Let's Encrypt certificates and modern HTTP/3 support:

```caddyfile
licensing.yourdomain.com {
    encode zstd gzip

    # Reverse proxy to Keygate Docker container
    reverse_proxy localhost:9000 {
        header_up X-Forwarded-Proto {scheme}
        header_up X-Forwarded-Host {host}
        header_up X-Real-IP {remote_host}
    }

    # Security headers
    header {
        Strict-Transport-Security "max-age=63072000; includeSubDomains; preload"
        X-Content-Type-Options "nosniff"
        X-Frame-Options "SAMEORIGIN"
        Referrer-Policy "strict-origin-when-cross-origin"
    }

    # Access logs
    log {
        output file /var/log/caddy/keygate.log
        format json
    }
}
```

### Option B: Nginx
```nginx
server {
    listen 80;
    server_name licensing.yourdomain.com;
    return 301 https://$host$request_uri;
}

server {
    listen 443 ssl http2;
    server_name licensing.yourdomain.com;

    ssl_certificate /etc/letsencrypt/live/licensing.yourdomain.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/licensing.yourdomain.com/privkey.pem;
    ssl_protocols TLSv1.2 TLSv1.3;
    ssl_ciphers ECDHE-ECDSA-AES128-GCM-SHA256:ECDHE-RSA-AES128-GCM-SHA256:ECDHE-ECDSA-AES256-GCM-SHA384:ECDHE-RSA-AES256-GCM-SHA384;
    ssl_prefer_server_ciphers off;

    add_header Strict-Transport-Security "max-age=63072000; includeSubDomains" always;
    add_header X-Content-Type-Options "nosniff" always;
    add_header X-Frame-Options "SAMEORIGIN" always;

    client_max_body_size 500M; # Necessary if uploading release artifacts

    location / {
        proxy_pass http://127.0.0.1:9000;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_read_timeout 60s;
        proxy_connect_timeout 10s;
    }
}
```

---

## 5. Development Deployment Workflow

For local testing, building from source, and schema inspection:
```bash
# Start Keygate in development mode (exposes Postgres on 5432)
make docker-dev
# or:
docker compose -f docker-compose.yml -f docker-compose.dev.yml up -d --build
```
Dev mode features:
- Automatically builds local Go backend and Bun/React frontend.
- Enables dev-login bypass for testing (`ENVIRONMENT=development`).
- Maps PostgreSQL port `5432:5432` to the host for tools like pgAdmin or TablePlus.
- Sets `WEBHOOK_ALLOW_PRIVATE=true` for local webhook consumers.
