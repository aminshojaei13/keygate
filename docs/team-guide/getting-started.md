# شروع کار (Getting Started)

این سند بر اساس فایل‌های واقعی مخزن نوشته شده است: `Makefile`، `Dockerfile`، `docker-compose.yml`، `docker-compose.dev.yml`، `.env.example`، `go.mod`، `web/package.json` و بخش راه‌اندازی `cmd/server/main.go`.

---

## ۱. پیش‌نیازها

| ابزار | نسخه‌ی مورد نیاز | چرا |
| --- | --- | --- |
| Go | **1.27** (در `go.mod` به‌صورت `go 1.27.0`) | بک‌اند `cmd/server` |
| PostgreSQL | **18** (در compose: `postgres:18-alpine`) | پایگاه داده |
| bun | نسخه‌ی ۱ به بالا (در Dockerfile: `oven/bun:1`) | نصب و build فرانت‌اند (`Makefile` از `bun` استفاده می‌کند) |
| Node.js | نسخه‌ی ۲۰ به بالا | اجرای تست‌های فرانت‌اند (`node --test`) |
| Docker + Compose | Compose 2.24+ (به‌دلیل `env_file: required: false`) | مسیر پیشنهادی اجرا |
| air (اختیاری) | آخرین نسخه | `make dev` برای hot reload بک‌اند |
| Python | 3.9+ | SDK و اسکریپت‌های `scripts/` و `test/` |

نکته‌ی مهم: در محیط فعلی این مخزن روی ماشین توسعه، `go`، `bun` و `dotnet` نصب نبودند و daemon داکر هم بالا نبود؛ جزئیات در [documentation-audit.md](documentation-audit.md) بخش «کارهای انجام‌نشده».

## ۲. ساختار پروژه (فقط مسیرهای مهم)

```
cmd/server/            بک‌اند و ثبت تمام routeها (main.go)
cmd/keygate-manifest/  ابزار CLI برای مانیفست صحت فایل‌ها
internal/config/       بارگذاری و اعتبارسنجی متغیرهای محیطی
internal/handler/      لایه‌ی HTTP (admin, license, release, portal, otp, ...)
internal/service/      منطق دامنه (لایسنس، استفاده، صندلی، release، webhook، ایمیل)
internal/store/        دسترسی به PostgreSQL (bun) + مهاجرت‌ها
internal/model/        مدل‌های داده و JSON آن‌ها
internal/middleware/   احراز هویت، scope، rate limit، idempotency، brute force
internal/license/      تولید کلید و توکن آفلاین Ed25519
internal/crypto/       HKDF + AES-256-GCM
pkg/deviceid/          قرارداد شناسه‌ی دستگاه (در سرور فقط اعتبارسنجی فرمت)
pkg/integrity/         ساخت/بررسی مانیفست صحت
pkg/response/          پوشش استاندارد پاسخ‌ها
web/                   فرانت‌اند React 19 + Vite 8 (خروجی در web/dist)
db/migrations/         مهاجرت‌های SQL (up/down)
sdk/python/            SDK پایتون
examples/dotnet/       نمونه‌ی C#/.NET
docs/                  مستندات انگلیسی + این راهنما در docs/team-guide/
```

## ۳. آماده‌سازی محیط

```bash
cp .env.example .env
```

سه متغیر **اجباری** هستند؛ سرور بدون آن‌ها بالا نمی‌آید (`internal/config/config.go`):

```bash
# ۱) کلید امضای نشست‌ها؛ حداقل ۳۲ کاراکتر (در production اجباری است)
openssl rand -hex 32          # → JWT_SECRET

# ۲) بذر ۳۲ بایتی Ed25519 برای امضای توکن آفلاین (باید دقیقاً ۶۴ کاراکتر hex باشد)
openssl rand -hex 32          # → LICENSE_SIGNING_KEY

# ۳) کلید اصلی رمزنگاری AES-256 (۶۴ کاراکتر hex)
#    اگر STORAGE_BUCKET تنظیم شود، این کلید اجباری است.
openssl rand -hex 32          # → RELEASE_KEY_ENCRYPTION_KEY
```

بقیه‌ی متغیرها را از `.env.example` بردارید. مهم‌ترین‌ها:

| متغیر | پیش‌فرض | توضیح |
| --- | --- | --- |
| `PORT` | `9000` | پورت سرور |
| `ENVIRONMENT` | `development` | فقط `development` / `staging` / `production` مجاز است |
| `BASE_URL` | `http://localhost:9000` | آدرس عمومی سرویس؛ در production باید `https` باشد وگرنه کوکی نشست بدون `Secure` می‌رود |
| `DATABASE_URL` | — | اگر خالی باشد از `POSTGRES_*` در compose ساخته می‌شود |
| `ADMIN_EMAILS` | رشته‌ی خالی | ایمیل‌هایی که در اولین ورود، نقش owner/admin می‌گیرند |
| `MIGRATIONS_DIR` | `db/migrations` | مسیر مهاجرت‌ها |
| `MIGRATE_ONLY` | `false` | اگر `true` باشد، فقط مهاجرت‌ها اجرا و فرآیند خارج می‌شود |
| `RATE_LIMIT_API` / `RATE_LIMIT_ADMIN` / `RATE_LIMIT_AUTH` | 60 / 120 / 60 | سهمیه درخواست در دقیقه |
| `RATE_LIMIT_OTP_SEND` | 30 | کد ورود در ساعت به ازای هر IP |
| `BF_MAX_FAILS` / `BF_LOCKOUT_SECONDS` | 5 / 30 | قفل موقت پس از تلاش‌های ناموفق روی `/license/*` |
| `STORAGE_*` | خالی | اگر خالی باشد، قابلیت انتشار نسخه با `503 STORAGE_DISABLED` پاسخ می‌دهد |
| `SMTP_*` | خالی | بدون SMTP، کد ورود OTP در **لاگ سرور** چاپ می‌شود (فقط برای توسعه) |
| `REDIS_URL` | خالی | اختیاری؛ برای هماهنگ‌کردن rate limit بین چند نمونه |
| `WEBHOOK_ALLOW_PRIVATE` | `false` | باز کردن اجازه‌ی webhook به آدرس‌های داخلی |

⚠️ هشدار امنیتی: مقادیر `.env` واقعی را هرگز commit نکنید و در مستندات/چت کپی نکنید. فایل `.env` در `.gitignore` است.

## ۴. راه‌اندازی دیتابیس

دو مسیر دارید:

**الف) با Docker (ساده‌تر).** دیتابیس در همان compose بالا می‌آید و مهاجرت‌ها هنگام استارت خودکار اجرا می‌شوند.

**ب) دیتابیس موجود.** یک دیتابیس بسازید و `DATABASE_URL` را ست کنید؛ مهاجرت‌ها در استارت سرور اجرا می‌شوند:

```bash
createdb keygate
export DATABASE_URL="postgres://user:pass@localhost:5432/keygate?sslmode=disable"
go run ./cmd/server        # RunMigrations خودکار اجرا می‌شود
```

مهاجرت‌ها در جدول `schema_migrations` ثبت می‌شوند، **checksum** هر فایل بررسی می‌شود و اجرا زیر یک advisory lock انجام می‌گیرد؛ یعنی دو نمونه که هم‌زمان بالا بیایند یک مهاجرت را دو بار اجرا نمی‌کنند. نکته‌ی مهم: فایل مهاجرتِ **اجراشده** را ویرایش نکنید؛ چون سرور با خطای checksum از استارت امتناع می‌کند. به‌جایش یک مهاجرت جدید بسازید.

## ۵. اجرای پروژه به‌صورت محلی

```bash
make build-web      # build فرانت‌اند → web/dist
make build-go       # build باینری Go → bin/keygate
make dev            # اجرای بک‌اند با hot reload (نیازمند air)
make dev-web        # اجرای dev server فرانت‌اند (Vite) روی پورت خودش
```

نکته: سرور، فایل‌های `web/dist` را خودش سرو می‌کند و برای مسیرهای SPA موجود در React به `index.html` برمی‌گردد. اگر `web/dist` وجود نداشته باشد، سرور فقط API را سرو می‌کند. اگر می‌خواهید frontend dev server جدا اجرا کنید، متغیر `VITE_API_URL` را به آدرس بک‌اند اشاره دهید.

## ۶. اجرای پروژه با Docker Compose

```bash
make docker-up                    # docker compose up -d  (پیکربندی production)
make docker-dev                   # compose + override توسعه (build محلی، پورت 5432 باز)
make docker-logs                  # tail لاگ کنتینر keygate
make docker-down                  # توقف سرویس‌ها (حجم داده حفظ می‌شود)
```

### تفاوت `image` و `build` در این پروژه

در `docker-compose.yml` سرویس `keygate` با `build: .` تعریف شده و خط `image: ghcr.io/tabloy/keygate:latest` **کامنت** شده است. یعنی:

* با `docker compose up` تصویر **از همین سورس محلی** ساخته می‌شود (چند دقیقه طول می‌کشد: build فرانت‌اند با bun و سپس build بک‌اند).
* اگر خط `image` را از کامنت خارج کنید و `build` را بردارید، compose تصویر آماده را از رجیستری می‌کشد؛ دیگر تغییرات کد شما داخل تصویر نیست.
* سرویس `keygate-migrate` **فقط** `image: ghcr.io/tabloy/keygate:latest` دارد و در profile به نام `tools` است، پس به‌صورت پیش‌فرض بالا نمی‌آید:

```bash
# اجرای مهاجرت‌ها با تصویر منتشرشده (مسیر پیشنهادی CI/CD پیش از استقرار)
docker compose --profile tools run --rm keygate-migrate
# یا معادل آن با Makefile:
make docker-migrate
```

معادل دستی داخل کنتینر خودِ سرویس:

```bash
docker compose exec keygate keygate migrate
# یا
docker compose run --rm -e MIGRATE_ONLY=true keygate
```

## ۷. بررسی وضعیت سرویس‌ها

```bash
curl -s http://localhost:9000/health | jq
# {"status":"ok","checks":{"database":"ok"},"version":"dev"}
```

| مسیر | کاربرد |
| --- | --- |
| `GET /health` | سلامت سرور؛ در صورت خطای دیتابیس `503` و `status: "degraded"` برمی‌گرداند |
| `GET /metrics` | متریک‌های Prometheus |
| `GET /version` در `/api/v1` | نسخه، commit، تاریخ build |
| `GET /docs` | رابط Scalar برای مرور `docs/openapi.yaml` |

بررسی مستقیم دیتابیس:

```bash
docker compose exec postgres pg_isready -U keygate -d keygate
docker compose exec postgres psql -U keygate -d keygate -c "SELECT filename FROM schema_migrations ORDER BY filename;"
```

## ۸. مشاهده‌ی لاگ‌ها

```bash
make docker-logs                       # فقط سرویس keygate
docker compose logs -f postgres        # دیتابیس
docker compose logs --tail=200 keygate # ۲۰۰ خط آخر
```

لاگ‌ها JSON ساختاریافته روی stdout هستند. نکته‌ی امنیتی: سرور مسیر درخواست را «پاک‌سازی» می‌کند تا کلید لایسنس یا توکن در لاگ نیفتد (`redactRequestPath` در `cmd/server/main.go`). اگر کدی اضافه می‌کنید که رازی را جای دیگری (مثلاً بادی یا هدر جدید) می‌فرستد، مسیر لاگ را دوباره بررسی کنید.

## ۹. دسترسی به پنل مدیریت

1. مرورگر: `http://localhost:9000/`
2. اگر نصب تازه است، به `/setup` هدایت می‌شوید. `GET /api/v1/setup/status` وضعیت را می‌گوید و `POST /api/v1/setup/initialize` اولین owner، محصول و طرح را می‌سازد. این endpoint تنها مسیری است که بدون احراز هویت اجازه‌ی ساخت کاربر می‌دهد و پس از تکمیل، `409 SETUP_COMPLETE` برمی‌گرداند.
3. ورود با ایمیل: `POST /api/v1/auth/otp/send` سپس `POST /api/v1/auth/otp/verify`. اگر SMTP تنظیم نشده باشد، کد در لاگ سرور چاپ می‌شود.
4. در `ENVIRONMENT=development` و وقتی `BASE_URL` شامل `localhost` یا `127.0.0.1` باشد، مسیر `POST /api/v1/auth/dev-login` فعال است (در غیر این صورت ۴۰۴ می‌دهد).

## ۱۰. اجرای تست‌ها

```bash
make test                                     # go test ./...
go test ./internal/service/... -run License   # یک بسته/الگو
go vet ./...                                  # lint

# تست‌های یکپارچه‌ی دیتابیس (نیازمند دیتابیس زنده)، مطابق CI:
TEST_DATABASE_URL="postgres://keygate:keygate@localhost:5432/keygate?sslmode=disable" \
  go test -count=1 -run "Atomic" ./internal/store/

# فرانت‌اند
cd web && bun run typecheck && bun run lint && node --test tests/*.test.mjs

# SDK پایتون
cd sdk/python && python3 -m unittest discover -s tests -v

# اسکریپت‌های مستقل پایتون در ریشه‌ی مخزن
python3 test/offline_token_test.py
python3 test/integrity_test.py
python3 test/device_identity_test.py
```

CI در `.github/workflows/ci.yml` همین‌ها را اجرا می‌کند: `go vet`, `go test ./... -count=1`, تست‌های `Atomic` روی دیتابیس، `go build`، و برای فرانت‌اند `bun install --frozen-lockfile`, `bun run typecheck`, `bun run lint`, `bun run build`.

## ۱۱. ساخت مجدد تصویر

```bash
make docker-build          # docker build با VERSION/COMMIT/BUILD_DATE
make docker-dev            # compose + override که همیشه --build می‌زند
# یا فقط یک سرویس:
docker compose build --no-cache keygate
```

اگر تغییرات UI دیده نمی‌شود، دلیل تقریباً همیشه این است که `web/dist` تصویر قدیمی است (بخش عیب‌یابی را ببینید).

## ۱۲. توقف و اجرای مجدد

```bash
make docker-down                        # توقف؛ داده‌ی PostgreSQL در volume می‌ماند
docker compose start                    # اجرای مجدد همان کنتینرها
docker compose up -d --build            # اجرای مجدد با تصویر تازه (بدون حذف داده)
```

⚠️ **هشدار مخرب:** دستور `docker compose down -v` حجم `pgdata` را حذف می‌کند و **تمام لایسنس‌ها، فعال‌سازی‌ها و تنظیمات از بین می‌رود**. پیش از هر کار مخرب، پشتیبان بگیرید:

```bash
make docker-backup                      # خروجی در backups/backup-<timestamp>.sql
# یا
make db-backup                          # از محیط میزبان با pg_dump
```

بازگردانی پشتیبان (روی همین دیتابیس می‌نویسد، پس ابتدا از وضعیت فعلی نسخه بگیرید):

```bash
make docker-restore BACKUP=backups/backup-20260101-120000.sql
```

`make clean` فقط خروجی‌های build (`bin/`, `web/dist`, `tmp/`) را پاک می‌کند و به داده کاری ندارد. `make clean-all` علاوه بر آن `web/node_modules` را حذف می‌کند.

## ۱۳. نکته‌ی شناخته‌شده در Makefile

هدف `make seed` به `./cmd/seed` ارجاع می‌دهد، اما این پوشه در مخزن **وجود ندارد** (فقط `cmd/server` و `cmd/keygate-manifest`). اجرای `make seed` خطا می‌دهد. برای داده‌ی نمایشی از setup wizard (`POST /api/v1/setup/initialize`) استفاده کنید. این مورد در [documentation-audit.md](documentation-audit.md) ثبت شده است.
