# شروع کار با Keygate

این راهنما شما را از صفر تا اجرای کامل پروژه روی سیستم خودتان می‌برد. همهٔ دستورها با فایل‌های
واقعی مخزن (`Makefile`، `docker-compose.yml`، `.env.example`) تطبیق داده شده‌اند.

> همهٔ مقادیر نمونه در این فایل ساختگی‌اند. هیچ رمز، توکن یا کلید واقعی را داخل مستندات یا
> مخزن قرار ندهید. فایل `.env` را هرگز commit نکنید (در `.gitignore` است).

---

## ۱. پیش‌نیازها

| ابزار | نسخه | چرا |
|---|---|---|
| Go | ۱.۲۷ یا بالاتر (`go.mod`: `go 1.27.0`) | ساخت و اجرای backend |
| Bun | ۱.x | ساخت فرانت‌اند (`web/`)، به‌جای npm |
| PostgreSQL | ۱۸ (در CI: `postgres:18`) | دیتابیس اصلی |
| Docker + Docker Compose | Compose ۲.۲۴+ (برای `env_file.required`) | اجرای کانتینری |
| `air` (اختیاری) | آخرین نسخه | اجرای backend با hot reload (`make dev`) |

اجزای اختیاری (پروژه بدون آن‌ها هم بالا می‌آید، اما قابلیت‌های مربوطه غیرفعال می‌شوند):

* **Redis یا Valkey** — برای اشتراک محدودیت نرخ بین چند نمونه (`REDIS_URL`).
* **S3 / Cloudflare R2 / MinIO** — برای مدیریت نسخه‌ها و فیدها (`STORAGE_*`).
* **Stripe** — برای فروش و اشتراک (`STRIPE_SECRET_KEY`).
* **SMTP یا Cloudflare Email** — برای ارسال ایمیل؛ در نبود آن، کدهای OTP در لاگ چاپ می‌شوند.

---

## ۲. ساختار کلی مخزن

```
keygate/
├── cmd/
│   ├── server/             # نقطهٔ ورود سرور (main.go: مسیرها، middlewareها)
│   └── keygate-manifest/   # CLI مستقل ساخت/بررسی مانیفست صحت
├── internal/
│   ├── handler/            # هندلرهای HTTP (منطق هر endpoint)
│   ├── middleware/         # auth، apikey، idempotency، ratelimit، bruteforce
│   ├── service/            # منطق دامنه (license، release، usage، seat، …)
│   ├── store/              # لایهٔ دیتابیس (Bun) و اجرای migrationها
│   ├── model/              # structها و ثابت‌ها (status/scope/event/…)
│   ├── license/            # تولید کلید و توکن آفلاین (Ed25519)
│   ├── crypto/             # AES-GCM و HKDF
│   ├── config/             # خواندن متغیرهای محیطی
│   ├── payment/            # Stripe و fulfilment
│   └── storage/            # انتزاع S3
├── pkg/
│   ├── deviceid/           # شناسهٔ دستگاه v1 + drift scoring
│   ├── integrity/          # مانیفست امضاشدهٔ فایل‌ها
│   ├── apperr/             # خطاهای دامنه/اعتبارسنجی
│   └── response/           # envelope پاسخ‌ها
├── sdk/python/             # SDK مرجع پایتون (بدون وابستگی خارجی)
├── db/migrations/          # migrationهای SQL (up/down)
├── web/                    # فرانت‌اند React/Vite/TypeScript
├── docs/                   # openapi.yaml، api-contract.md و این راهنما
├── docker-compose.yml
├── docker-compose.dev.yml
├── Dockerfile
├── Makefile
└── .env.example
```

> `cmd/seed` وجود ندارد؛ هدف `make seed` در Makefile به آن اشاره می‌کند و خطا می‌دهد
> (جزئیات در `documentation-audit.md`).

---

## ۳. تنظیم متغیرهای محیطی

فایل نمونه را کپی کنید:

```bash
cp .env.example .env
```

حداقل مقادیر **الزامی** (بدون آن‌ها سرور با خطا بالا نمی‌آید — `config.Load` بررسی می‌کند):

* `DATABASE_URL`
* `JWT_SECRET`
* `LICENSE_SIGNING_KEY`

هر دو مقدار امنیتی را با دستور زیر بسازید:

```bash
openssl rand -hex 32
```

جدول مهم‌ترین متغیرها (فهرست کامل در `.env.example`):

| متغیر | پیش‌فرض | توضیح |
|---|---|---|
| `PORT` | `9000` | پورت سرور |
| `ENVIRONMENT` | `development` | `development` / `staging` / `production` |
| `BASE_URL` | `http://localhost:9000` | آدرس عمومی؛ برای ساخت لینک و کوکی `Secure` |
| `DATABASE_URL` | — | رشتهٔ اتصال PostgreSQL |
| `JWT_SECRET` | — (**الزامی**) | کلید امضای JWT نشست |
| `LICENSE_SIGNING_KEY` | — (**الزامی**) | seed کلید خصوصی Ed25519 لایسنس (`openssl rand -hex 32`) |
| `REDIS_URL` | خالی | فعال‌سازی محدودیت نرخ مشترک |
| `RATE_LIMIT_API` | `60` | سقف درخواست عمومی در دقیقه |
| `RATE_LIMIT_ADMIN` | `120` | سقف درخواست مدیریتی در دقیقه |
| `RATE_LIMIT_AUTH` | `60` | سقف ورود/verify در دقیقه |
| `RATE_LIMIT_OTP_SEND` | `30` | سقف ارسال OTP در هر ساعت |
| `BF_MAX_FAILS` | `5` | تعداد شکست قبل از قفل موقت روی `/license/*` |
| `BF_LOCKOUT_SECONDS` | `30` | مدت قفل اولیه |
| `ADMIN_EMAILS` | خالی | ایمیل‌هایی که در اولین ورود admin می‌شوند؛ اگر تنظیم شود، **فقط همین ایمیل‌ها** می‌توانند در setup اولیه owner شوند |
| `STRIPE_SECRET_KEY` | خالی | فعال‌سازی پرداخت |
| `SMTP_HOST` / `SMTP_PORT` / `SMTP_USERNAME` / `SMTP_PASSWORD` / `SMTP_FROM` | خالی | ارسال ایمیل |
| `STORAGE_BUCKET` / `STORAGE_ACCESS_KEY` / `STORAGE_SECRET_KEY` / `STORAGE_ENDPOINT` | خالی | object storage نسخه‌ها |
| `RELEASE_KEY_ENCRYPTION_KEY` | خالی | کلید AES-256-GCM برای رمزنگاری کلیدهای امضای release (`openssl rand -hex 32`) |

> **هشدار:** `RELEASE_KEY_ENCRYPTION_KEY` و `LICENSE_SIGNING_KEY` را گم نکنید. تغییر یا گم شدن
> `RELEASE_KEY_ENCRYPTION_KEY` باعث می‌شود کلیدهای امضای موجود غیرقابل‌رمزگشایی شوند و انتشار
> نسخه شکست بخورد. تغییر `LICENSE_SIGNING_KEY` همهٔ توکن‌های آفلاین در گردش را بی‌اعتبار می‌کند.

---

## ۴. راه‌اندازی دیتابیس

یک دیتابیس PostgreSQL بسازید (نام و کاربر نمونه):

```bash
createdb -U postgres keygate
# یا داخل psql:
# CREATE USER keygate WITH PASSWORD 'keygate';
# CREATE DATABASE keygate OWNER keygate;
```

`DATABASE_URL` را مطابق آن تنظیم کنید؛ مثلاً:

```
DATABASE_URL=postgres://keygate:keygate@localhost:5432/keygate?sslmode=disable
```

**migrationها خودکار اجرا می‌شوند.** در `cmd/server/main.go` بلافاصله پس از ساخت اتصال،
`db.RunMigrations("db/migrations")` صدا زده می‌شود. پس نیازی به دستور جداگانهٔ migration نیست؛
فقط کافی است سرور را اجرا کنید. migrationها در جدول `schema_migrations` ثبت و با یک advisory lock
(`7367616`) از اجرای هم‌زمان چند نمونه محافظت می‌شوند.

اگر می‌خواهید بعد از بالا آمدن، دادهٔ نمونه بریزید:

```bash
make seed     # go run ./cmd/seed
```

---

## ۵. اجرای پروژه به‌صورت محلی

### الف) backend

```bash
# بدون hot reload
make build-go && ./bin/keygate

# یا با hot reload (نیازمند نصب air)
make dev
```

سرور روی `http://localhost:9000` بالا می‌آید.

### ب) فرانت‌اند (حالت توسعه)

در ترمینال جداگانه:

```bash
make dev-web      # cd web && bun run dev
```

و برای ساخت نسخهٔ production فرانت‌اند:

```bash
make build-web    # cd web && bun run build  →  web/dist
```

سرور به‌صورت خودکار فایل‌های `web/dist` را سرو می‌کند (`serveFrontend`). اگر `web/dist` نباشد،
فقط API کار می‌کند و مسیرهای SPA پاسخ نمی‌دهند.

### ج) ساخت کامل (backend + frontend)

```bash
make build        # build-web سپس build-go
```

---

## ۶. اجرای پروژه با Docker Compose

`docker-compose.yml` دو سرویس دارد: `keygate` (ایمیج `ghcr.io/tabloy/keygate:latest`) و
`postgres`. پورت `9000` منتشر می‌شود.

1. مطمئن شوید `.env` مقدار `JWT_SECRET` و `LICENSE_SIGNING_KEY` را دارد
   (Compose آن‌ها را الزامی می‌کند: `${JWT_SECRET:?...}`).
2. اجرا:

```bash
make docker-up        # docker compose up -d
```

3. مشاهدهٔ لاگ‌ها:

```bash
make docker-logs      # docker compose logs -f keygate
```

نکته: در Compose، `DATABASE_URL` عمداً به سرویس داخلی `postgres` اشاره می‌کند، نه `localhost`.
اگر می‌خواهید از سورس build شود، خط `# build: .` در `docker-compose.yml` را از حالت کامنت دربیاورید.

---

## ۷. بررسی وضعیت سرویس‌ها

* سلامت سرور و دیتابیس:

```bash
curl -s http://localhost:9000/health
```

خروجی چیزی شبیه این است (ساختار واقعی: `status`، `checks`، `version`):

```json
{
  "status": "ok",
  "checks": { "database": "ok" },
  "version": "dev"
}
```

اگر دیتابیس در دسترس نباشد، `status` برابر `degraded` و کد وضعیت `503` می‌شود.

* بررسی نسخه و پیکربندی عمومی:

```bash
curl -s http://localhost:9000/api/v1/version
curl -s http://localhost:9000/api/v1/config
```

---

## ۸. دسترسی به پنل مدیریت

مرورگر را روی آدرس زیر باز کنید:

```
http://localhost:9000/
```

در اولین اجرا (وقتی هیچ ownerی وجود ندارد) به مسیر `/setup` هدایت می‌شوید؛ پس از تکمیل setup،
صفحهٔ ورود `/login` نمایش داده می‌شود و پس از ورود، پنل مدیریت در `/admin` و پورتال مشتری در
`/portal` در دسترس است.

روش ورود در حالت توسعه: در `ENVIRONMENT=development` دکمهٔ «Dev login» فعال است
(`POST /api/v1/auth/dev-login`). در محیط production این endpoint غیرفعال می‌شود و ورود از طریق
OTP ایمیل انجام می‌گیرد.

---

## ۹. ساخت تنظیمات اولیه (Setup Wizard)

`GET /api/v1/setup/status` نشان می‌دهد آیا setup لازم است:

```json
{ "needed": true, "step": "initialize" }
```

سپس `POST /api/v1/setup/initialize` را صدا بزنید (از طریق صفحهٔ `/setup` یا مستقیم):

```bash
curl -s -X POST http://localhost:9000/api/v1/setup/initialize \
  -H 'Content-Type: application/json' \
  -d '{
        "admin_email": "owner@example.com",
        "admin_name": "Owner",
        "site_name": "Example",
        "product_name": "Example App",
        "product_slug": "example-app",
        "product_type": "desktop"
      }'
```

> اگر `ADMIN_EMAILS` را در `.env` تنظیم کرده باشید، `admin_email` **باید** یکی از همان
> ایمیل‌ها باشد؛ در غیر این صورت `403 SETUP_EMAIL_NOT_ALLOWED` می‌گیرید. این محافظ جلوی
> ربودن نصب تازه توسط اولین بازدیدکننده را می‌گیرد.

پس از موفقیت (کد `201`)، پاسخ شامل `user`، `product` و `plan` است و `setup_complete` ثبت می‌شود.
پس از این مرحله، اگر بخواهید محصول/پلن دیگری بسازید از پنل مدیریت یا API مدیریتی استفاده کنید.

---

## ۱۰. اجرای تست‌ها

تست‌های واحد (بدون دیتابیس؛ مسیرهای وابسته به دیتابیس خودشان skip می‌شوند):

```bash
go test ./...
# یا
make test
```

برای اجرای تست‌هایی که به دیتابیس زنده نیاز دارند، `TEST_DATABASE_URL` را تنظیم کنید:

```bash
TEST_DATABASE_URL='postgres://keygate:keygate@localhost:5432/keygate?sslmode=disable' \
  go test ./...
```

> **هشدار:** `TEST_DATABASE_URL` باید به یک دیتابیس **مخصوص تست** اشاره کند، نه دیتابیس
> production. این تست‌ها داده می‌سازند و پاک می‌کنند.

تست‌های پایتون (SDK و اسکریپت‌های مستقل، بدون نیاز به سرور):

```bash
python3 -m unittest discover -s sdk/python/tests -v
python3 test/offline_token_test.py
python3 test/integrity_test.py
python3 test/device_identity_test.py
```

ابزار مانیفست را می‌توانید مستقیماً هم اجرا کنید:

```bash
go run ./cmd/keygate-manifest help
```

دقیقاً همان دستورهایی که CI اجرا می‌کند (`.github/workflows/ci.yml`):

```bash
go vet ./...
go test ./... -count=1
TEST_DATABASE_URL='postgres://keygate:keygate@localhost:5432/keygate?sslmode=disable' \
  go test -count=1 -run "Atomic" ./internal/store/
go build -o /dev/null ./cmd/server
```

و برای فرانت‌اند:

```bash
cd web
bun install --frozen-lockfile
bun run typecheck
bun run lint
bun run build
```

---

## ۱۱. توقف و اجرای مجدد

با Docker Compose:

```bash
make docker-down      # docker compose down  (داده‌های postgres حفظ می‌شود)
make docker-up        # اجرای مجدد
```

با اجرای محلی، کافی است فرایند `keygate` را با `Ctrl+C` متوقف کنید؛ سرور در سیگنال
`SIGINT`/`SIGTERM` با مهلت ۱۵ ثانیه به‌صورت تمیز خاموش می‌شود.

### ⚠️ هشدار دربارهٔ دستورهای مخرب

دستورهای زیر **داده را حذف می‌کنند** و به‌طور پیش‌فرض توصیه نمی‌شوند:

```bash
docker compose down -v        # حذف volume دیتابیس → پاک شدن همهٔ لایسنس‌ها و تنظیمات
```

اگر می‌خواهید وضعیت را از صفر بسازید، اول **بکاپ بگیرید**:

```bash
make db-backup                # pg_dump روی DATABASE_URL → backup-YYYYMMDD-HHMMSS.sql
```

و برای بازگردانی:

```bash
make db-restore BACKUP=backup-20260101-000000.sql
```

---

## ۱۲. جمع‌بندی مسیر پیشنهادی برای تازه‌وارد

1. `.env` بسازید و سه مقدار الزامی را پر کنید.
2. دیتابیس بسازید و `DATABASE_URL` را درست کنید.
3. `make dev` (backend) و `make dev-web` (frontend) را اجرا کنید.
4. `/health` را چک کنید.
5. مرورگر را روی `http://localhost:9000/` باز کنید و setup را کامل کنید.
6. حالا به `admin-panel-guide.md` و `api-reference.md` بروید.
