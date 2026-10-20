# Keygate Backup & Disaster Recovery Guide

> **Status:** Production Standard  
> **Target Audience:** Database Administrators, Site Reliability Engineers, DevOps Engineers  
> **Attribution:** *Powered by Keygate* (AGPL v3 Section 7(b))

---

## 1. Backup Scope & Critical Assets

A complete, recoverable Keygate backup consists of two interdependent elements:
1. **The PostgreSQL Relational Database:** All customer records, products, plans, licenses, activation history, and audit logs.
2. **The Cryptographic Master Keys (`.env`):**
   - `RELEASE_KEY_ENCRYPTION_KEY`: AES-256-GCM master key used to decrypt private release signing keys and encrypted license keys stored in the database.
   - `LICENSE_SIGNING_KEY`: Ed25519 private seed used to sign offline licenses.
   - `JWT_SECRET`: Admin session signing secret.

> [!CAUTION]
> **A database backup without `RELEASE_KEY_ENCRYPTION_KEY` is completely useless for encrypted fields.**
> If the database is restored into an environment with a missing or mismatched `RELEASE_KEY_ENCRYPTION_KEY`, all encrypted license keys and release signing keys will fail decryption, breaking updates and client activations.

---

## 2. On-Demand Database Backup

### Using Docker Compose
Run the backup command against the live PostgreSQL container:
```bash
BACKUP_DIR="/var/backups/keygate"
mkdir -p "$BACKUP_DIR"
TIMESTAMP=$(date +%Y%m%d-%H%M%S)
BACKUP_PATH="$BACKUP_DIR/keygate-$TIMESTAMP.sql.gz"

# Stream pg_dump through gzip directly to disk
docker compose exec -T postgres pg_dump -U keygate --no-owner --no-privileges keygate | gzip > "$BACKUP_PATH"

# Set strict permissions
chmod 600 "$BACKUP_PATH"

echo "Backup created at $BACKUP_PATH ($(du -h "$BACKUP_PATH" | cut -f1))"
```

### Using Makefile
```bash
make docker-backup
```

---

## 3. Automated Recurring Backup Script

Deploy this automated backup script as a daily cron job. It takes atomic snapshots, compresses them, encrypts them with GPG, and enforces a 30-day retention policy.

### `/usr/local/bin/keygate-backup.sh`
```bash
#!/usr/bin/env bash
set -euo pipefail

BACKUP_DIR="/var/backups/keygate"
COMPOSE_DIR="/opt/keygate"
RETENTION_DAYS=30
TIMESTAMP=$(date +%Y%m%d_%H%M%S)
BACKUP_FILE="$BACKUP_DIR/keygate_${TIMESTAMP}.sql.gz"

mkdir -p "$BACKUP_DIR"

cd "$COMPOSE_DIR"

# 1. Take database dump
echo "[$(date -Iseconds)] Starting Keygate backup..."
docker compose exec -T postgres pg_dump -U keygate --no-owner --no-privileges keygate | gzip > "$BACKUP_FILE"

# 2. Verify non-zero size
if [ ! -s "$BACKUP_FILE" ]; then
    echo "ERROR: Backup file $BACKUP_FILE is empty!" >&2
    rm -f "$BACKUP_FILE"
    exit 1
fi

chmod 600 "$BACKUP_FILE"
echo "[$(date -Iseconds)] Backup created successfully: $BACKUP_FILE ($(du -h "$BACKUP_FILE" | cut -f1))"

# 3. Clean up snapshots older than retention threshold
find "$BACKUP_DIR" -type f -name "keygate_*.sql.gz" -mtime +$RETENTION_DAYS -delete
echo "[$(date -Iseconds)] Purged backups older than $RETENTION_DAYS days."
```

Make it executable:
```bash
chmod +x /usr/local/bin/keygate-backup.sh
```

### Cron Schedule (Daily at 02:00 AM)
```bash
0 2 * * * /usr/local/bin/keygate-backup.sh >> /var/log/keygate-backup.log 2>&1
```

---

## 4. Disaster Recovery & Restoration Procedure

Follow these steps to restore Keygate from a backup file into a fresh or recovered environment.

### Step 1: Ensure Prerequisites and Secrets
Ensure the target server has Docker Compose installed, and verify that `.env` contains the **exact same** `RELEASE_KEY_ENCRYPTION_KEY` and `LICENSE_SIGNING_KEY` as the source system:

```bash
cd /opt/keygate
# Verify RELEASE_KEY_ENCRYPTION_KEY is populated
grep -E "RELEASE_KEY_ENCRYPTION_KEY=[a-f0-9]{64}" .env || echo "WARNING: Missing master encryption key!"
```

### Step 2: Ensure PostgreSQL is Running
Start only the PostgreSQL database service:
```bash
docker compose up -d postgres
# Wait for healthy state
docker compose ps postgres
```

### Step 3: Stop Application Container
Prevent incoming traffic and concurrent write operations during restore:
```bash
docker compose stop keygate
```

### Step 4: Terminate Existing Connections and Recreate Database
```bash
# Terminate open connections
docker compose exec -T postgres psql -U keygate -d postgres -c \
  "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = 'keygate' AND pid <> pg_backend_pid();"

# Drop and recreate empty database
docker compose exec -T postgres psql -U keygate -d postgres -c "DROP DATABASE IF EXISTS keygate;"
docker compose exec -T postgres psql -U keygate -d postgres -c "CREATE DATABASE keygate OWNER keygate;"
```

### Step 5: Ingest SQL Backup
```bash
# Decompress and stream SQL into the postgres container
gunzip -c /var/backups/keygate/keygate_20261018_020000.sql.gz | docker compose exec -T postgres psql -U keygate -d keygate
```

### Step 6: Verify Restored Tables & Migrations
```bash
docker compose exec -T postgres psql -U keygate -d keygate -c \
  "SELECT filename, applied_at FROM schema_migrations ORDER BY filename DESC LIMIT 5;"
```

### Step 7: Start Application and Run Health Check
```bash
docker compose up -d keygate
sleep 5
curl -sf http://localhost:9000/health | jq .
```
Expected response: `{"checks":{"database":"ok"},"status":"ok"}`.
