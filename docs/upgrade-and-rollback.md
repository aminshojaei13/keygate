# Keygate Upgrade, Migration & Rollback Runbook

> **Status:** Production Standard  
> **Target Audience:** DevOps Engineers, Database Administrators, Site Reliability Engineers  
> **Attribution:** *Powered by Keygate* (AGPL v3 Section 7(b))

---

## 1. Upgrade Philosophy & Compatibility

Keygate follows semantic versioning and guarantees forward-compatibility of the database schema across minor versions. Database migrations are managed via version-stamped SQL scripts executed with transactional DDL and PostgreSQL advisory locking (`pg_advisory_lock(7367616)`).

> [!WARNING]
> **IRREVERSIBLE MIGRATIONS & ROLLBACK LIMITATIONS:**
> **Never assume a database migration can be rolled back safely without data loss.**
> Certain migrations in Keygate drop columns or convert encryption formats (e.g., dropping plaintext license keys in favor of AES-256-GCM ciphertexts, or restructuring device activation tombstones). Once executed against production data, rolling back the database schema via `.down.sql` scripts will permanently delete or corrupt newly recorded data.
>
> **Mandatory Rule:** Always take a full database snapshot (`pg_dump`) immediately before applying any version upgrade or migration.

---

## 2. Standard Upgrade Procedure

### Step 1: Pre-Upgrade Database Snapshot
Before pulling new images or updating configuration, take a verified snapshot of the database:

```bash
cd /opt/keygate

# Using Makefile helper:
make docker-backup

# Or using raw docker compose command:
BACKUP_FILE="backups/pre-upgrade-$(date +%Y%m%d-%H%M%S).sql"
mkdir -p backups
docker compose exec -T postgres pg_dump -U keygate --no-owner --no-privileges keygate > "$BACKUP_FILE"

# Verify non-empty backup
if [ ! -s "$BACKUP_FILE" ]; then
    echo "ERROR: Backup file is empty! Aborting upgrade."
    exit 1
fi
echo "Backup verified at $BACKUP_FILE"
```

### Step 2: Review Release Notes & Schema Changes
Review the release changelog and inspect any new migration files in `db/migrations/`:
```bash
git fetch --tags
git log --oneline $(git describe --tags --abbrev=0)..origin/main -- db/migrations/
```

### Step 3: Pull Updated Docker Image
```bash
docker compose pull keygate
```

### Step 4: Execute Pre-Deploy Migration (Zero-Downtime Pattern)
Keygate supports running database migrations as an independent, one-off task before restarting the main web service:

```bash
# Run migrations using the dedicated migrate runner
docker compose run --rm keygate migrate
```
Keygate will:
1. Acquire PostgreSQL advisory lock `7367616`.
2. Inspect `schema_migrations` and verify SHA-256 checksums of existing migrations.
3. Apply any new `.up.sql` scripts in transactional order.
4. Record the applied migration and release the advisory lock.
5. Exit with status code `0`.

### Step 5: Restart the Service
```bash
docker compose up -d keygate
```

### Step 6: Healthcheck & Verification
```bash
# Verify container health
docker compose ps keygate

# Check health endpoint
curl -sf http://localhost:9000/health || echo "Healthcheck failed!"

# Tail application logs for errors
docker compose logs --tail=100 keygate
```

---

## 3. Database Migration Management

### Migration Architecture
- **Location:** `db/migrations/`
- **Format:** `<YYYYMMDD>_<name>.up.sql` and `<YYYYMMDD>_<name>.down.sql`
- **Execution Mechanism:** Go `store.RunMigrations(dir)`
- **Concurrency Control:** `SELECT pg_advisory_lock(7367616)` guarantees that multiple Keygate instances starting simultaneously will not collide or double-apply migrations.
- **Integrity Validation:** SHA-256 checksums are recorded in `schema_migrations`. Modifying an applied migration file in the source tree is treated as a fatal tampering error.

### Irreversible Migration Matrix

| Migration File | Description | Rollback Risk | Safe to Roll Back? |
|---|---|---|---|
| `20260320_init.up.sql` | Base schema creation | Drops all tables and customer records | ❌ **No (Catastrophic)** |
| `20260514_license_key_encrypted.up.sql` | Encrypts license keys with AES-256-GCM | Down script drops `license_key_encrypted` column; all encrypted keys lost | ❌ **No (Data Loss)** |
| `20260908_maintenance_period.up.sql` | Restructures perpetual licenses & maintenance terms | Downgrade script strips perpetual renewal timestamps | ⚠️ **Data Degraded** |
| `20261017_device_records.up.sql` | Activation status tombstones & scheme tracking | Down script drops status; un-tombstones deactivated devices into active slots | ⚠️ **Business Logic State Risk** |

---

## 4. Rollback Procedures

If an upgrade introduces regressions or application crashes, follow this decision tree:

### Case A: Application-Only Rollback (No New Migrations Applied)
If the new application binary failed before any database migrations were executed, simply roll back the container image tag:

```bash
# 1. Edit docker-compose.yml or set IMAGE tag to previous stable version
# 2. Recreate container
docker compose up -d --force-recreate keygate
```

### Case B: Full Disaster Recovery Rollback (Migrations Were Applied)
Because schema migrations may be irreversible or data-destructive, do **NOT** attempt manual `down.sql` execution in production. Instead, perform an atomic restore from the pre-upgrade snapshot:

```bash
# 1. Stop the application server to prevent incoming writes
docker compose stop keygate

# 2. Terminate existing database connections
docker compose exec -T postgres psql -U keygate -d postgres -c \
  "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = 'keygate' AND pid <> pg_backend_pid();"

# 3. Drop and recreate the keygate database
docker compose exec -T postgres psql -U keygate -d postgres -c "DROP DATABASE keygate;"
docker compose exec -T postgres psql -U keygate -d postgres -c "CREATE DATABASE keygate OWNER keygate;"

# 4. Restore the pre-upgrade snapshot
docker compose exec -T postgres psql -U keygate -d keygate < "$BACKUP_FILE"

# 5. Re-pin the Docker image in docker-compose.yml to the previous stable release

# 6. Restart the application
docker compose up -d keygate

# 7. Validate system health
docker compose logs --tail=50 keygate
curl -sf http://localhost:9000/health
```
