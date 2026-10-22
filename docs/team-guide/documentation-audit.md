# ممیزی مستندات (Documentation Audit)

این سند وضعیت مستندات را با **کد واقعی** مقایسه می‌کند. هر عدد از بازبینی مستقیم
`cmd/server/main.go`، `internal/handler/*`، `internal/service/*`، `internal/store/*` و
`docs/openapi.yaml` آمده است.

---

## ۱. خلاصهٔ اجرایی

| شاخص | مقدار |
|---|---|
| routeهای واقعی ثبت‌شده | **۱۵۲** (۱۵۱ multi-method + ۱ `StaticFile`) |
| routeهای مستندشده در `api-reference.md` | **۱۵۲** (۱۰۰٪) |
| مسیرهای موجود در `docs/openapi.yaml` | **۲۱** |
| فاصلهٔ OpenAPI با کد | **۱۳۱** مسیر مستندنشده در OpenAPI |
| فایل‌های تست Go | **۹۷** |
| تست‌های پایتون (SDK + اسکریپت‌های مستقل) | **۴** فایل (`sdk/python/tests/test_sdk.py`، `test/*_test.py`) |
| سوئیت کامل مستندات فارسی | ۱۳ فایل زیر `docs/team-guide/` |

> مبنای همهٔ اعداد این سند، شاخهٔ `update-docs` (HEAD = `8124d95`) است، نه
> شاخهٔ `main` بالادستی. تفاوت این دو در بخش ۱۰ توضیح داده شده است.

---

## ۲. شمارش واقعی routeها

منبع: `cmd/server/main.go` (بلوک ثبت مسیرها، حدود خطوط ۶۴۰–۱۳۰۰).

| دسته | تعداد | جزئیات |
|---|---|---|
| زیرساخت | ۶ | `/metrics`, `/health`, `/docs`, `/docs/`, `/docs/openapi.yaml`, `/pay/:checkout_id` |
| عمومی `/api/v1` (غیر از license) | ۵ | `/version`, `/config`, `/setup/status`, `/setup/initialize`, `/license/pubkey` |
| SDK لایسنس | ۱۰ | `activate, verify, deactivate, entitlements, usage, usage/status, floating/{checkout,checkin,heartbeat}, download` |
| دعوت | ۱ | `/invites/accept` |
| فیدها | ۹ | ۵ فید/کشف کلید فعلی (`signing-key` هم) + ۴ مسیر قدیمی `410` |
| احراز هویت | ۶ | `providers, otp/send, otp/verify, dev-login, logout, refresh` |
| کاتالوگ عمومی پلن | ۱ | `/products/:product_slug/plans` |
| Stripe | ۲ | `/webhook/stripe`, `/checkout/verify` |
| پورتال | ۱۶ | `me, licenses, profile, plans, usage, usage/status, seats*, activations*, subscription*, updates/renew` |
| ادمین | ۹۶ | `stats, products, plans, entitlements, licenses(۲۴), activations(۳), api-keys, webhooks, addons, settings, system, email-templates, team, analytics, audit-logs, users, releases(۱۲), signing(۶)` |
| **جمع** | **۱۵۲** | |

از ۱۵۲ مسیر، **۱۴۶** زیر `/api/v1` و **۶** روی router ریشه‌اند.
(۱۵۱ ثبت `GET/POST/PUT/PATCH/DELETE` + ۱ ثبت `StaticFile` برای `docs/openapi.yaml`.)

---

## ۳. شمارش مستندشده

`api-reference.md` همهٔ ۱۵۲ مسیر را با قالب ۲۰ فیلدی مستند می‌کند (ردیف‌هایی که برای یک route
معنا ندارند با `—` پر یا حذف می‌شوند). **۹۶ endpoint ادمین در §۱۰ تک‌به‌تک و با جدول کامل
بازنویسی شده‌اند**؛ پیش از این بازنویسی، این بخش به‌صورت خلاصهٔ پاراگرافی بود و قالب یکسان نداشت.

* §۱ زیرساخت (۶)
* §۲ سیستم/راه‌اندازی (۵)
* §۳ SDK لایسنس (۱۰)
* §۴ دعوت (۱)
* §۵ فیدها (۹) — شامل `۵.۴.۱` کشف کلید امضا
* §۶ احراز هویت (۶)
* §۷ کاتالوگ پلن (۱)
* §۸ Stripe (۲)
* §۹ پورتال (۱۶)
* §۱۰ ادمین (۹۶) — شامل `۱۰.۴.۲۳` تا `۱۰.۴.۲۶` و `۱۰.۴.۲۴.۱` (transfer، offline-token، reset-device-binding، deactivate، reactivate)

---

## ۴. endpointهایی که پیش از این سند، مستند نبودند

**۱۳۱ مسیر** هیچ مستندات قبلی (نه OpenAPI، نه سند دیگر) نداشتند:

* **تمام ۹۶ مسیر ادمین** — هیچ‌یک در OpenAPI نیست.
* **تمام ۶ مسیر احراز هویت** (`/auth/*`).
* **پورتال (به‌جز seats):** `me`, `licenses`, `profile`, `plans`, `usage`, `usage/status`,
  `activations*`, `subscription/*`, `updates/renew` (یعنی ۱۳ از ۱۶).
* `/setup/status`, `/setup/initialize`, `/version`, `/pay/:checkout_id`, `/checkout/verify`,
  `/webhook/stripe`, `/admin/system/*`.
* ۴ مسیر قدیمی فید (`410`).

**۲۱ مسیر** قبلاً در OpenAPI بودند: `/config`, `/license/pubkey`,
`/license/{activate,verify,deactivate,entitlements,usage,usage/status,download}`,
`/license/floating/{checkout,checkin,heartbeat}`, `/portal/seats{,/add,/remove}`,
`/invites/accept`، ۴ فید فعلی و `/releases/{product_slug}/signing-key`.

---

## ۵. تفاوت‌های OpenAPI با پیاده‌سازی

| موضوع | OpenAPI | پیاده‌سازی واقعی |
|---|---|---|
| پوشش مسیر | ۲۱ مسیر | ۱۵۲ مسیر |
| گروه ادمین | هیچ | ۹۶ مسیر |
| احراز هویت | هیچ | ۶ مسیر |
| پورتال (me/licenses/profile/plans/subscriptions) | هیچ | ۱۳ مسیر |
| مسیر Velopack | `/releases/{product_slug}/velopack/{platform}/{file}` | `GET /releases/:product_slug/velopack/*path` (wildcard) |
| مسیرهای قدیمی فید (`410`) | مستند نشده | ۴ مسیر موجود |
| `/pay/:checkout_id` | مستند نشده | ۱ مسیر |
| `/releases/{product_slug}/signing-key` | مستند شده | مطابق است |
| ساختار envelope خطا | ممکن است `details` را توصیف نکند | `details` در خطاهایی مثل `ACTIVATION_LIMIT`, `LOCKED_OUT` عملاً پر می‌شود |
| `Idempotency-Key` | مستند نشده (در `activate`/`usage`/`floating/checkout` عملاً پذیرفته می‌شود) | پشتیبانی می‌شود |
| فیلدهای `activations` | `scheme`/`status`/`deactivated_*` توصیف نشده | در پاسخ‌ها برمی‌گردند |

**نتیجه:** OpenAPI فقط «زیرمجموعهٔ SDK عمومی» را پوشش می‌دهد و برای مصرف داخلی/تیم ناقص است.

> این ممیزی در سطح **مسیر** انجام شده است. یک diff میدانی (field-by-field) بین
> OpenAPI و پیاده‌سازی انجام نشده و به یک تصمیم تیم نیاز دارد (بخش ۹).

---

## ۶. endpointهای بدون تست خودکار

فایل‌های تست Go ‏**۹۷** عددند (به‌علاوه ۴ فایل تست پایتون) و پوشش همچنان بیشتر در لایهٔ
service/store است، ولی شاخهٔ فعلی چند شکاف مهم را بسته است:

* `internal/handler/license_activation_test.go` — ۱۲ تست روی activation، محدودیت سهم،
  شناسه‌های `kgid`، رد شناسهٔ دارای فاصله، و **transfer / deactivate / reactivate**.
* `internal/store/activation_lifecycle_test.go` — چرخهٔ عمر نرم (tombstone) و انتقال لایسنس.
* `internal/handler/setup_test.go` و `team_test.go` — مسیرهای setup و تیم دیگر بدون تست نیستند.

مسیرهایی که در بازبینی **تست سطح handler برایشان یافت نشد**:

### احراز هویت (همه بدون تست handler)

* `GET /auth/providers`
* `POST /auth/otp/send`
* `POST /auth/otp/verify` (فقط `session_cookie_test.go` برای helper کوکی)
* `POST /auth/dev-login`
* `POST /auth/logout`
* `POST /auth/refresh`

### پورتال (همه بدون تست handler)

* `GET /portal/me`, `GET /portal/licenses`, `PUT /portal/profile`, `GET /portal/plans`
* `POST /portal/usage`, `POST /portal/usage/status`
* `POST /portal/seats`, `/seats/add`, `/seats/remove`
* `GET/DELETE /portal/licenses/:k/activations[/:id]`
* `POST /portal/subscription/cancel`, `/billing-portal`, `GET /portal/subscription/invoices`
  (فقط `change-plan` و `updates/renew` در لایهٔ payment تست دارند)

### ادمین

* بیشتر مسیرهای CRUD محصول/پلن/افزودنی/کلید API/وبهوک/تنظیمات در سطح **handler** تست
  ندارند؛ پوشش در `store`/`service` و در تست‌های موضوعی
  (`product_floor_test.go`, `plan_price_unique_test.go`, `settings_test.go`, …) است.
* `GET /admin/stats`, `analytics/*`, `audit-logs`, `users/*`, `email-templates`,
  `system/update-check`, `system/migrations` → تست handler یافت نشد.
* `GET /releases/:product_slug/signing-key` → تست handler یافت نشد (منطق کلید در
  `release_signing_rotate_test.go` پوشش دارد).

### عمومی

* `GET /version`, `GET /config`, `GET /license/pubkey`, `GET /setup/status` (غیرمستقیم)،
  `GET /products/:slug/plans`, `GET /checkout/verify`, ۴ مسیر `410`.

### آنچه **تست دارد** (نمونه‌های مهم)

`license/activate|verify|usage|entitlements` (در service)، `floating/*` (store)،
`download` (`release_download_gate_test.go`)، فیدها (`release_feed_test.go`,
`release_velopack_test.go`, `tauri_minisign_test.go`)، امضا
(`release_signing_*_test.go`)، Stripe (`internal/payment/*_test.go` — ۱۴ فایل)،
`pay/:checkout_id` (`renewal_test.go`)، idempotency (middleware/store)، rate-limit و
`Retry-After` (middleware).

> توجه: تست‌های وابسته به DB فقط با `TEST_DATABASE_URL` اجرا می‌شوند؛ در غیر این صورت
> **skip** می‌شوند. بنابراین «سبز بودن CI» تضمین اجرای آن‌ها نیست.

---

## ۷. قابلیت‌های ناقص یا کد بدون route

| مورد | وضعیت |
|---|---|
| `payment.StripeHandler.CreateCheckoutSession` | در `internal/payment/stripe.go` وجود دارد ولی در `main.go` **ثبت نشده** |
| جریان OAuth ارائه‌دهنده (Google/GitHub/…) | **پیاده نشده**؛ `/auth/providers` فقط `dev_login` و `otp` را اعلام می‌کند |
| فایل `internal/handler/auth.go` | وجود ندارد؛ منطق auth در `oauth.go` و `otp.go` است |
| فایل `internal/handler/stripe*.go`/`billing*.go` | وجود ندارد؛ handler استریپ `payment.StripeHandler` است |
| `SystemHandler.StartAutoCheck` | حلقهٔ پس‌زمینه است، نه endpoint |
| محلی‌سازی فارسی (`fa`) در فرانت‌اند | **پیاده نشده**؛ فقط `en` و `zh` |
| کف نسخه برای Velopack | سرور اعمال نمی‌کند و فید Velopack جایی برای آن ندارد (Tauri/Sparkle دارند) |
| «integrity manifest» | **وجود دارد، ولی خارج از سرور**: پکیج `pkg/integrity` + CLI `cmd/keygate-manifest` (قالب `keygate-integrity-v1`، امضای Ed25519). هیچ endpoint سروری آن را نمی‌سازد یا توزیع نمی‌کند. |
| شناسهٔ دستگاه (`pkg/deviceid`) | در سرور از `ValidateIdentifierFormat` و در حالت strict از بررسی طرح (`kgid-v1`/`sha256-v1`) استفاده می‌شود؛ binding دقیق در `internal/service/license.go` اعمال می‌شود. `EvaluateDrift` و `VerifyProofOfPossession` هنوز مصرف‌کنندهٔ سروری ندارند. |
| SDK پایتون | پیاده‌شده در `sdk/python` (بدون وابستگی خارجی) و با `test/` پوشش دارد؛ در `Makefile`/CI جزو مسیر پیش‌فرض تست نیست. |
| `make seed` | هدف Makefile به `./cmd/seed` ارجاع می‌دهد که **وجود ندارد** (فقط `cmd/server` و `cmd/keygate-manifest`). |
| `require_signing` فقط در سطح محصول | تأییدشده؛ انتشار بدون کلید → `SIGNING_NOT_CONFIGURED` |

---

## ۸. تناقض‌ها و نکات امنیتی/مستنداتی

1. **کوکی‌ها:** نام `session` و `refresh_token` است؛ `SameSite=Lax` و `HttpOnly` و
   `Secure` (وابسته به HTTPS). در توضیحات عمومی ممکن است مبهم باشد.
2. **توکن آفلاین:** امضا روی **رشتهٔ base64** است، نه JSON خام. هر مستندی که بگوید
   «JSON را امضا کنید» اشتباه است.
3. **`exp` در برابر `vun`:** `exp` انقضای توکن (check-in) است؛ پایان لایسنس `vun` است.
4. **«صحت فایل»:** اکنون یک مانیفست امضاشدهٔ مستقل وجود دارد (`keygate-integrity-v1` در
   `pkg/integrity` + `cmd/keygate-manifest`)، اما **هیچ endpoint سروری** آن را تولید، امضا یا
   توزیع نمی‌کند؛ انتشار نسخه در سرور همچنان بر `sha256` + امضای هر artifact و فید تکیه دارد.
5. **`/docs` و `/metrics` عمومی‌اند** و در production باید محدود شوند (پیام خطای DB در
   `/health` هم افشا می‌شود).
6. **بدون Redis در چند‌نمونه:** سقف rate-limit و بروت‌فورس واقعاً چند برابر می‌شود.
7. **انتساب AGPL v3 §۷(b):** `attribution_text`/`attribution_url` و هدر `X-Powered-By`
   الزام حقوقی‌اند؛ حذف‌شان نقض مجوز است.
8. **OpenAPI گمراه‌کننده:** فقط SDK عمومی را پوشش می‌دهد؛ اگر توسعه‌دهنده فقط به
   `/docs` نگاه کند، ۱۳۱ مسیر را نمی‌بیند.
9. **متن UI در برابر رفتار backend:** در صفحهٔ Devices، دیالوگ حذف می‌گوید
   «Permanently Delete Device Record»، ولی `DELETE /admin/activations/:id` از این نسخه
   **حذف نرم** انجام می‌دهد (خروجی `204` ولی رکورد با `status='deactivated'` باقی می‌ماند).
10. **دو پیاده‌سازی متفاوت از `kgid`:** `pkg/deviceid` (Go) و `sdk/python/keygate/device.py`
    یک digest یکسان تولید نمی‌کنند: Go هر مؤلفه را با domain separation هش و سپس digest مرکب
    می‌سازد؛ پایتون مقادیر خام را در یک رشتهٔ مرکب هش می‌کند. در حالت tolerant اثر عملی
    ندارد، ولی در strict binding، لایسنس به **همان رشتهٔ** اولین activate بسته می‌شود؛ اگر
    یک محصول با دو SDK (مثلاً پایتون و C#/Go) شناسهٔ متفاوت بسازد، SDK دوم
    `DEVICE_CHANGED` می‌گیرد. پیش از production باید الگوریتم یکی شود؛ تغییر الگوریتم هم
    چون migration غیرفعال است به reset ادمین برای همهٔ لایسنس‌های فعال نیاز دارد.
11. **`pkg/deviceid` نیمه‌مصرف:** `EvaluateDrift` و `VerifyProofOfPossession` تست دارند ولی
    هیچ‌جا در سرور صدا زده نمی‌شوند. حالت strict با مقایسهٔ دقیق `identifier` (و
    `kgid`/`sha256` برای binding جدید) کار می‌کند؛ حالت tolerant فعلی هم فقط یعنی strict
    خاموش + سقف پلن + `previous_identifier`، نه امتیازدهی drift. مدل drift طرح آینده است.
12. **`make seed` شکسته:** هدف Makefile به `./cmd/seed` اشاره می‌کند که وجود ندارد.
13. **ناسازگاری keyring در SDK پایتون:** توکن‌های همین شاخه فیلد `kid` دارند
    (`key_<16hex>`)، ولی `KeygateClient(license_public_key_hex=…)` کلید را با نام
    `bootstrap` ثبت می‌کند؛ نتیجه `OfflineVerificationError: UNKNOWN_SIGNING_KEY` است.
    راه‌حل فعلی: پیش از بررسی، `client.get_license_public_keys()` را صدا بزنید تا کلیدها با
    `kid` واقعی ثبت شوند (این مسیر در همین شاخه تست و تأیید شد). اصلاح پیشنهادی: SDK هنگام
    ساخت کلاینت، `kid` را از `sha256(public_key)[:8]` بسازد.

---

## ۹. موارد نیازمند تصمیم تیم

1. **آیا OpenAPI را کامل می‌کنیم** (۱۳۱ مسیر) یا برای مصرف داخلی به `api-reference.md`
   بسنده می‌کنیم؟ (تولید خودکار از routeها ممکن است بهتر باشد.)
2. **سیاست تست handler:** آیا برای `/auth/*` و `/portal/*` که هیچ تست handler ندارند،
   تست یکپارچه نوشته می‌شود؟
3. **`CreateCheckoutSession`:** حذف شود یا به‌عنوان endpoint ثبت شود؟
4. **OAuth واقعی:** آیا `/auth/providers` باید `dev_login`/`otp` تنها بماند یا ارائه‌دهنده
   اضافه شود؟
5. **افزودن locale `fa`** به فرانت‌اند.
6. **کف نسخه Velopack:** آیا باید اعمال شود؟
7. **مستندسازی `Idempotency-Key` در OpenAPI.**
8. **`X-Forwarded-Proto` و IP واقعی** در استقرار چند‌نمونه‌ای — تعیین رویهٔ استاندارد.
9. **کاهش افشا:** پنهان‌کردن `/metrics`, `/docs`, و جزئیات `/health` در production.
10. **یکپارچه‌سازی مانیفست صحت:** آیا `keygate-manifest` باید خروجی‌اش را در release artifact
    ثبت کند و endpointای برای توزیع `integrity.manifest.json` اضافه شود، یا ابزار CLI مستقل بماند؟
11. **مصرف‌کنندهٔ `pkg/deviceid`:** strict binding اکنون دقیق است؛ drift هنوز به هیچ
    endpointی وصل نیست (نه strict، نه tolerant). تصمیم باقی‌مانده: آیا drift و
    proof-of-possession باید در آینده به activate/verify وصل شوند تا جعل سمت کلاینت هم
    پوشش داده شود؟

---

## ۱۰. رابطهٔ ایمیج Docker با سورس (اصلاح یک برداشت اشتباه)

در نسخهٔ اول این ممیزی نوشته شده بود که ایمیج محلی `keygate-keygate:latest` **۱۵۱** مسیر دارد
ولی سورس **۱۴۶** مسیر، و پنج مسیر «در هیچ‌جای تاریخ git نیست». **این نتیجه اشتباه بود** و علت
آن هم مشخص شد:

| واقعیت | توضیح |
|---|---|
| شاخهٔ `security-enhancements` (همین مخزن) | **۱۵۱** مسیر؛ شامل هر پنج مسیر مورد بحث |
| کلون جداگانه از `main` بالادستی (`tabloy/keygate`، HEAD = `4c27a4d`) | **۱۴۶** مسیر؛ این پنج مسیر در آن وجود ندارد |
| ایمیج `keygate-keygate:latest` | از همین شاخه ساخته شده و با سورس **می‌خواند** |

پنج مسیری که قبلاً «فقط در ایمیج» تصور می‌شدند، در واقع در همین مخزن هستند:

| مسیر | محل ثبت |
|---|---|
| `GET /api/v1/releases/:product_slug/signing-key` | `cmd/server/main.go:822` |
| `POST /api/v1/admin/activations/:id/deactivate` | `cmd/server/main.go:1220` |
| `POST /api/v1/admin/activations/:id/reactivate` | `cmd/server/main.go:1221` |
| `POST /api/v1/admin/licenses/:id/offline-token` | `cmd/server/main.go:1218` |
| `POST /api/v1/admin/licenses/:id/transfer` | `cmd/server/main.go:1217` |

**درس عملی:** پیش از هر ممیزی، مطمئن شوید `git remote -v` و `git rev-parse HEAD` به مخزن و
شاخهٔ درست اشاره می‌کنند. یک کلون از مخزن بالادستی، ۱۵۱ مسیر این شاخه را نشان نمی‌دهد و
شمارش‌ها را بی‌سر‌و‌صدا غلط می‌کند. مستندات این پوشه اکنون بر **سورس ۱۵۱ مسیرهٔ همین شاخه**
استوار است.

> ایمیج محلی همچنان ممکن است از یک commit قدیمی‌تر ساخته شده باشد؛ پیش از استقرار آن را از
> سورس فعلی بازسازی کنید (`docker compose build`).

> تأیید end-to-end نمونهٔ C# روی همین ایمیج انجام شد. مسیرهای استفاده‌شده
> (`/license/pubkey`, `/license/activate`, `/license/verify`, `/setup/*`, `/auth/dev-login`,
> `/admin/licenses`) در سورس هم وجود دارند؛ بنابراین نتیجه برای این مسیرها معتبر است، ولی
> «۱۵۱» عدد ایمیج است نه عدد سورس.

---

## ۱۱. نتیجهٔ تأیید عملی (Executed)

همهٔ موارد زیر روی **همین شاخه** (`update-docs`) اجرا شده‌اند:

| بررسی | محیط | نتیجه |
|---|---|---|
| `go vet ./...` | `golang:1.27-alpine` | ✅ بدون خطا |
| `go build ./...` | `golang:1.27-alpine` | ✅ بدون خطا |
| `go test ./... -count=1` (بدون `TEST_DATABASE_URL`) | `golang:1.27-alpine` | ✅ ۱۸ پکیج `ok`؛ تست‌های DB-محور skip شدند |
| بیلد `Keygate.Sample` (Release) | `mcr.microsoft.com/dotnet/sdk:8.0` | ✅ `0 Warning(s), 0 Error(s)` |
| بیلد `Keygate.Sample.NetCore31` (Release) | `mcr.microsoft.com/dotnet/sdk:3.1` (linux/amd64) | ✅ `0 Warning(s), 0 Error(s)`؛ اجرای `help` روی runtime 3.1 هم موفق |
| end-to-end روی **همین شاخه** (setup → dev-login → license → activate → verify) | `go run ./cmd/server` + PostgreSQL 18 | ✅ `setup` ۲۰۱، `activate` = `activated`، `verify` = `active` |
| اعتبارسنجی شناسه در همین شاخه | همان محیط | ✅ شناسهٔ `kgid:v1:…` پذیرفته شد؛ `machine-abc ` (فاصلهٔ انتهایی) → `400 BAD_REQUEST` |
| مهاجرت شناسه با `previous_identifier` | همان محیط | ✅ رکورد UUID درجا به `kgid:v1:windows:…` منتقل شد، بدون مصرف سهم اضافه (حالت tolerant) |
| **strict device binding** (`DEVICE_CHANGED`، `IDENTIFIER_SCHEME_UNSUPPORTED`، `DEACTIVATION_DISABLED`، reset ادمین، reactivate ادمین) | PostgreSQL 18 موقت | ✅ شش تست handler + تست‌های `strictSchemeSupported`؛ `go test -p 1 ./...` همهٔ پکیج‌ها را پاس کرد |
| بررسی آفلاین توکن با SDK پایتون (سریع، بدون شبکه) | همان محیط | ✅ پس از `get_license_public_keys()` توکن معتبر شناخته شد (`kid` تطبیق یافت) |
| `deactivate` / `reactivate` / `DELETE` (حذف نرم) | همان محیط | ✅ `deactivated` → `reactivated` → `204` و رکورد با `status='deactivated'`, `reason='admin'` باقی ماند |
| `POST /admin/licenses/:id/offline-token` | همان محیط | ✅ توکن صادر شد و یک activation واقعی (`status=activated`) ساخت |
| `POST /admin/licenses/:id/transfer` | همان محیط | ✅ `transferred` + آزادسازی ۲ activation؛ ایمیل تکراری → `409 IDENTICAL_EMAIL` |
| end-to-end کلاینت C# روی **کلون بالادستی** (setup → activate → verify → offline `--network none`) | ایمیج محلی + PostgreSQL 18 | ✅ (روی این شاخه تکرار نشد؛ فقط بیلد و بررسی الگوریتم تأیید شد) |
| تأیید امضای Ed25519 در کلاینت C# | همان محیط کلون | ✅ `signature : VERIFIED` |
| `keygate-manifest create` → `verify` → `inspect` | `golang:1.27-alpine` + پوشهٔ آزمایشی | ✅ امضا تأیید شد، ۲/۲ فایل سالم |
| تشخیص دست‌کاری فایل در `keygate-manifest verify` | همان | ✅ فایل تغییرکرده به‌عنوان `Modified/tampered` گزارش شد (خروجی غیرصفر) |
| `python3 -m unittest discover -s sdk/python/tests` | Python 3 (stdlib) | ✅ ۱۶ تست `OK` |
| `test/offline_token_test.py` | Python 3 (stdlib) | ✅ ۱۴/۱۴ |
| `test/integrity_test.py` | Python 3 (stdlib) | ✅ ۸/۸ |
| `test/device_identity_test.py` | Python 3 (stdlib) | ✅ همهٔ fixtureها |

> **شفافیت دربارهٔ اجرای تست‌ها:** دو شکست تستی که در نسخهٔ اول این سند گزارش شد
> (`TestFeedCutoff_MaintenancePeriod` و `TestQueueMarksPermanentFailureTerminal`) در اجرای
> **کلون بالادستی** دیده شده بودند، نه در این شاخه. روی این شاخه، `go test -p 1 ./...` با
> `TEST_DATABASE_URL` روی PostgreSQL 18 موقت اجرا شد و همهٔ پکیج‌ها `ok` بودند.
> پس از تغییر بررسی `did` در نمونه‌های C#، این محیط `dotnet` نداشت؛ بنابراین بیلد C# در این
> مرحله تکرار نشد. بیلدهای قبلی جدول مربوط به قبل از این تغییر هستند.

### دو شکست تست در کلون بالادستی (برای مرجع)

**۱) `TestFeedCutoff_MaintenancePeriod` — قطعی (۳/۳ شکست)**

```
lapsed cutoff: 2026-10-10 06:41:07.60918 +0000 UTC <nil>
```

* تست یک `cutoff := time.Now().Add(-10*24*time.Hour)` با دقت **نانوثانیه** می‌سازد، آن را در
  دیتابیس ذخیره می‌کند، سپس `svc.FeedCutoff(...)` را می‌خواند و با `got.Equal(cutoff)`
  مقایسه می‌کند.
* PostgreSQL مقدار `timestamptz` را به **میکروثانیه** گرد می‌کند؛ بنابراین مقدار برگشتی
  با مقدار محلی برابر نیست.
* این یک **باگ تست** است (نه باگ محصول): مقایسه باید با
  `cutoff.Truncate(time.Microsecond)` انجام شود. تا اصلاح، این تست در هر محیطی با
  `time.Now()` نانوثانیه‌ای و Postgres شکست می‌خورد.

**۲) `TestQueueMarksPermanentFailureTerminal` — flaky / وابسته به ترتیب**

* در اجرای کامل پکیج شکست خورد، ولی در isolation با `-count=5` **۵/۵ پاس** شد.
* نشانهٔ تداخل وضعیت مشترک (تنظیمات `settings` و متغیر global `cfEndpoint`) بین تست‌های
  هم‌زمان است؛ نیاز به جداسازی (isolation) دارد.

### محدودیت‌های باقی‌مانده

* diff فیلدبه‌فیلد OpenAPI انجام نشده (فقط سطح مسیر) و به تصمیم تیم نیاز دارد.
* شمارش «تست‌نداشتن» در بخش ۶ بر اساس **تست سطح handler** است؛ پوشش غیرمستقیم از طریق
  service/store وجود دارد.
* اجرای کامل تست‌ها با دیتابیس واقعی روی این شاخه تکرار نشده است (بخش بالا).
* ایمیج محلی ممکن است از commit قدیمی‌تری ساخته شده باشد؛ قبل از استقرار بازسازی کنید.
