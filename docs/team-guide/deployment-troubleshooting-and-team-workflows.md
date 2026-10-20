# استقرار، عیب‌یابی و گردش‌کار تیم

این سند سه چیز را پوشش می‌دهد: راه‌اندازی/local، استقرار production، و روش کار روزمرهٔ تیم.
همهٔ دستورها از `Makefile`, `docker-compose.yml`, `Dockerfile` و `ci.yml` گرفته شده‌اند.

---

## ۱. راه‌اندازی محلی

### ۱.۱ با Docker Compose

```bash
cp .env.example .env      # سپس مقادیر را پر کنید
make docker-up            # docker compose up -d
make docker-logs          # docker compose logs -f keygate
make docker-down
```

`docker-compose.yml` سرویس‌های `keygate`، `postgres` و (به‌صورت اختیاری) `redis` را بالا
می‌آورد. برای توسعهٔ محلی می‌توانید Redis را نادیده بگیرید (rate-limit درون‌حافظه‌ای می‌شود).

### ۱.۲ بدون Docker (بومی)

```bash
# پیش‌نیاز: Go 1.27+, Node/Bun, PostgreSQL 18
make dev          # بک‌اند با hot reload (air)
make dev-web      # فرانت‌اند Vite
```

### ۱.۳ ساخت کامل

```bash
make build        # build-web + build-go → bin/keygate
make check        # lint + test + build (معادل CI)
```

### ۱.۴ دادهٔ نمونه

```bash
make seed         # go run ./cmd/seed
```

⚠️ هدف `make seed` به `./cmd/seed` اشاره می‌کند که **در مخزن وجود ندارد** (فقط `cmd/server` و
`cmd/keygate-manifest`). این دستور خطا می‌دهد. برای دادهٔ نمایشی از setup wizard
(`POST /api/v1/setup/initialize`) و سپس ساخت دستی محصول/پلن/لایسنس استفاده کنید.

---

## ۲. متغیرهای محیطی

### ۲.۱ الزامی

| متغیر | چرا لازم است |
|---|---|
| `DATABASE_URL` | اتصال PostgreSQL |
| `JWT_SECRET` | امضای نشست HS256 |
| `LICENSE_SIGNING_KEY` | کلید خصوصی Ed25519 برای توکن آفلاین |

ساخت رازها:

```bash
openssl rand -hex 32   # JWT_SECRET
openssl rand -hex 32   # LICENSE_SIGNING_KEY (یا هر کلید Ed25519 سازگار)
```

### ۲.۲ اختیاری/مهم

| متغیر | اثر |
|---|---|
| `BASE_URL` | آدرس عمومی؛ روی `https://` باشد تا کوکی `Secure` شود |
| `REDIS_URL` | اشتراک rate-limit بین نمونه‌ها (الزامی برای استقرار چند‌نمونه‌ای) |
| `RELEASE_KEY_ENCRYPTION_KEY` | رمزنگاری کلیدهای امضای release (AES-256-GCM) |
| `ADMIN_EMAILS` | ایمیل‌هایی که در اولین ورود admin می‌شوند |
| `STRIPE_*` | پرداخت |
| `SMTP_*` | ارسال ایمیل (بدون آن، OTP فقط لاگ می‌شود) |
| `STORAGE_*` | ذخیره‌سازی فایل artifact (S3-compatible) |
| `RATE_LIMIT_API` / `RATE_LIMIT_ADMIN` / `RATE_LIMIT_AUTH` / `RATE_LIMIT_OTP_SEND` | محدودیت نرخ |
| `BF_MAX_FAILS` / `BF_LOCKOUT_SECONDS` | پارامترهای بروت‌فورس لایسنس |
| `ENVIRONMENT` | `development` (dev-login فعال) / `production` (خاموش) |

> هرگز `.env` را commit نکنید. `.env.example` فقط جای‌نگهدار دارد.

---

## ۳. مهاجرت‌های دیتابیس

* مهاجرت‌ها در `db/migrations/` هستند و در استارت سرور (`RunMigrations`) اعمال می‌شوند.
* برای بررسی وضعیت: `GET /api/v1/admin/system/migrations` (scope `admin`).
* پشتیبان‌گیری/بازگردانی:

```bash
make db-backup                 # pg_dump → backup-YYYYMMDD-HHMMSS.sql
make db-restore BACKUP=backup.sql
```

**قاعده:** روی production هرگز مهاجرت را به‌صورت دستی دست‌کاری نکنید؛ فقط نسخهٔ سرور را
بالا ببرید تا مهاجرت‌ها اجرا شوند.

---

## ۴. استقرار در production

### ۴.۱ چک‌لیست پیش از انتشار

1. `ENVIRONMENT=production`.
2. `BASE_URL` روی `https://…` (کوکی `Secure`).
3. `JWT_SECRET`, `LICENSE_SIGNING_KEY`, `RELEASE_KEY_ENCRYPTION_KEY` تصادفی + بکاپ.
4. `REDIS_URL` اگر > ۱ نمونه.
5. `SMTP_*` درست تا OTP واقعاً برسد.
6. `ADMIN_EMAILS` فقط افراد مجاز.
7. پشت reverse proxy با TLS (هدر `X-Forwarded-Proto: https` را forward کنید).
8. `/metrics` و `/docs` را از اینترنت عمومی ببندید (یا محدود کنید).

### ۴.۲ ساخت ایمیج

```bash
make docker-build VERSION=v1.2.3
```

### ۴.۳ نکات استقرار چند‌نمونه‌ای

* بدون `REDIS_URL`، هر نمونه سبد rate-limit مستقل دارد → سقف مؤثر چند برابر می‌شود.
* بروت‌فورس لایسنس هم درون‌حافظه‌ای است؛ برای استقرار جدی Redis را فعال کنید.
* کاربران پشت load balancer: `X-Forwarded-For` را درست تنظیم کنید تا `c.ClientIP()` درست
  کار کند (audit و بروت‌فورس بر آن تکیه دارند).

---

## ۵. پایش

| منبع | آدرس | کاربرد |
|---|---|---|
| سلامت | `GET /health` | `status`, `checks.database`, `version` |
| متریک | `GET /metrics` | Prometheus (activation/verification و…) |
| لاگ حسابرسی | `/admin/audit` | رخدادهای حساس |
| نسخه | `GET /api/v1/version` | نسخه/commit/build_date |

---

## ۶. عیب‌یابی (جدول)

| نشانه | علت محتمل | راه‌حل |
|---|---|---|
| سرور بالا نمی‌آید | `JWT_SECRET` یا `LICENSE_SIGNING_KEY` غایب | هر دو را ست کنید |
| `/health` می‌گوید `degraded` | پینگ دیتابیس شکست خورده | `DATABASE_URL` و وضعیت Postgres را بررسی کنید |
| OTP نمی‌آید | `SMTP_*` ست نشده | SMTP را تنظیم کنید یا کد را در لاگ ببینید (فقط dev) |
| کوکی نشست روی HTTPS ست نمی‌شود | `X-Forwarded-Proto` forward نشده | reverse proxy را تنظیم کنید |
| فعال‌سازی همه‌جا `404 LICENSE_NOT_FOUND` | کلید برای محصول دیگری است / کلید اشتباه | کلید و `product` را تطبیق دهید |
| `429 LOCKED_OUT` | بروت‌فورس | `details.retry_after` صبر کنید |
| `409 ACTIVATION_LIMIT` | سقف دستگاه | یک دستگاه را deactivate کنید |
| publish نسخه رد می‌شود | `require_signing` و بدون کلید امضا | کلید امضا بسازید |
| `503 SIGNING_DISABLED` | `RELEASE_KEY_ENCRYPTION_KEY` غایب | آن را ست کنید |
| `409 SHA256_MISMATCH` | فایل artifact تغییر کرده/ناقص آپلود شده | دوباره آپلود و finalize کنید |
| فید `401 LICENSE_KEY_REQUIRED` | `feed_license_required` روشن است | کلید لایسنس را در هدر بفرستید |
| فید `410 FEED_PATH_REMOVED` | کلاینت از مسیر قدیمی استفاده می‌کند | به مسیر جدید با `product_slug` مهاجرت کنید |
| `403 UPDATES_EXPIRED` | دورهٔ نگهداری تمام شده | `updates_until` را تمدید کنید |
| فرانت بالا نمی‌آید | `bun install` نشده | `cd web && bun install` |
| rate-limit ناگهان چند برابر شد | چند نمونه بدون Redis | `REDIS_URL` را ست کنید |

---

## ۷. گردش‌کار تیم

### ۷.۱ شاخه و commit

* شاخهٔ پیش‌فرض: `main`.
* شاخهٔ ویژگی: `feat/…`, `fix/…`.
* پیش از PR حتماً `make check` را بگیرید.

### ۷.۲ CI

`.github/workflows/ci.yml` این‌ها را اجرا می‌کند:

1. **Backend:** `go vet ./...`، `go test ./... -count=1` با سرویس `postgres:18`،
   `go build ./cmd/server`.
2. **Frontend:** `bun install --frozen-lockfile`، `bun run typecheck`، `bun run lint`،
   `bun run build`.

> **توجه:** تست‌های وابسته به دیتابیس فقط وقتی اجرا می‌شوند که `TEST_DATABASE_URL` ست شده
> باشد؛ در غیر این صورت **skip** می‌شوند. این یعنی «CI سبز» به‌تنهایی ثابت نمی‌کند تست‌های
> DB اجرا شده‌اند.

اجرای محلی معادل CI با DB:

```bash
export TEST_DATABASE_URL="postgres://postgres:postgres@localhost:5432/keygate_test?sslmode=disable"
go test ./... -count=1
```

### ۷.۳ مدیریت رازها

* رازها فقط در env یا در جدول `settings` (رمزنگاری‌شده) نگه‌داری می‌شوند.
* `GET /admin/settings` رازها را **masked** برمی‌گرداند (`secrets_set`).
* چرخش کلید امضای release از طریق `…/signing-key/rotate` با scope `admin` انجام می‌شود.

### ۷.۴ انتشار نسخهٔ محصول

به `integrity-and-release-guide.md` مراجعه کنید (draft → artifact → finalize → publish).

### ۷.۵ انتساب (الزام حقوقی)

«Powered by Keygate» (`branding.Tagline`)، هدر `X-Powered-By`، و
`attribution_text`/`attribution_url` در `/api/v1/config` **بخشی از مجوز AGPL v3 §۷(b)**
هستند و حذف/تغییرشان ممنوع است. این موارد را در هیچ PR حذف نکنید.
