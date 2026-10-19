# مرجع API نسخه‌ی فعلی

این مرجع از **کد واقعی** استخراج شده است: ثبت مسیرها در `cmd/server/main.go` به‌همراه handlerها و سرویس‌های مربوطه. هیچ endpoint خیالی در این سند نیست و هر endpoint‌ای که در کد وجود دارد، در اینجا آمده است.

شمارش دقیق (بخش ۱۱ `documentation-audit.md` را ببینید):

* **۱۵۰** ثبت مسیر `GET/POST/PUT/PATCH/DELETE` در `cmd/server/main.go`
* به‌علاوه **۱** ثبت `StaticFile("/docs/openapi.yaml")` → مجموعاً **۱۵۱** endpoint
* `docs/openapi.yaml` تنها **۲۱** مسیر را مستند کرده است (فقط زیرمجموعه‌ی عمومی/SDK)

---

## ۱. قواعد مشترک

### ۱.۱ پوشش پاسخ‌ها

```json
// موفق
{ "success": true, "data": { } }

// خطا
{ "success": false, "error": { "code": "LICENSE_NOT_FOUND", "message": "license not found", "details": { } } }
```

* `details` اختیاری است و در خطاهای ساخت‌یافته می‌آید (مثلاً `ACTIVATION_LIMIT` → `{max, current}`).
* لیست‌ها همیشه `total`, `limit`, `offset` دارند و مجموعه‌ی خالی `[]` است، نه `null`.
* استثناها: `204` در Tauri (بدون بدنه)، فیدهای XML/JSON خام، `/health`, `/metrics`, `/docs`.

### ۱.۲ صفحه‌بندی، مرتب‌سازی و فیلترها (فقط APIهای مدیریتی)

| پارامتر | مقدار پیش‌فرض | محدوده |
| --- | --- | --- |
| `limit` | ۵۰ | بزرگ‌تر از ۲۰۰ به ۲۰۰ «کلمپ» می‌شود و مقدار واقعی در پاسخ برمی‌گردد |
| `offset` | ۰ | منفی نادیده گرفته می‌شود |
| `sort` | بر اساس endpoint | فقط ستون‌های allowlist؛ نام ناشناخته `400 INVALID_SORT` |
| `order` | جهت طبیعی ستون | `asc` یا `desc`؛ غیر از این `400 INVALID_ORDER` |

### ۱.۳ احراز هویت (خلاصه؛ جزئیات در سند امنیت)

| دسته | اعتبارنامه |
| --- | --- |
| `/license/*` و فیدها | `license_key` در بدنه، یا `X-License-Key` / `license_token` برای فیدها — **بدون** API key |
| `/portal/*` | کوکی نشست (`session`)؛ کلید لایسنس در بدنه فقط «نام» لایسنس است |
| `/admin/*` | کوکی/توکن نشست ادمین، یا `Authorization: Bearer kg_live_…` با scope مناسب |
| `/setup/*`, `/auth/*`, `/config`, `/version` | عمومی |

### ۱.۴ Idempotency

هدر `Idempotency-Key` فقط روی سه مسیر نوشتنی بررسی می‌شود و **اختیاری** است:

| مسیر | رفتار |
| --- | --- |
| `POST /license/activate` | همان کلید + همان بدنه → پاسخ کش‌شده با هدر `Idempotent-Replayed: true` |
| `POST /license/usage` | همان |
| `POST /license/floating/checkout` | همان |

* عمر کش ۲۴ ساعت (پیش‌فرض جدول).
* همان کلید + بدنه‌ی متفاوت → `422 IDEMPOTENCY_KEY_CONFLICT`.
* درخواست هم‌زمان با همان کلید → `409 IDEMPOTENCY_IN_FLIGHT` و هدر `Retry-After: 1`.
* پاسخ‌های `5xx` کش **نمی‌شوند** (پاک می‌شود تا تلاش مجدد واقعاً اجرا شود)؛ پاسخ‌های `4xx` کش می‌شوند.
* سقف بدنه ۲۵۶ KiB؛ بزرگ‌تر از آن `413 BODY_TOO_LARGE`.
* کلید نامعتبر (خالی/کنترل‌کاراکتر/بیشتر از ۲۵۶ کاراکتر) → `400 INVALID_IDEMPOTENCY_KEY`.

### ۱.۵ محدودسازی نرخ و قفل brute force

* همه‌ی مسیرها rate limit به‌ازای IP دارند؛ سبد هر «خانواده‌ی مسیر» جداست (`license`, `feed`, `auth`, `otp_send`, `auth_refresh`, `public_plans`, `stripe_webhook`, `checkout_verify`, `invites`, `admin`).
* سقف‌ها: `license` = `max(RATE_LIMIT_API×2, 120)` در دقیقه؛ `feed` = `max(RATE_LIMIT_API×4, 240)`؛ `admin` = `RATE_LIMIT_ADMIN` (۱۲۰) و `auth` = `RATE_LIMIT_AUTH` (۶۰) و `otp_send` = `RATE_LIMIT_OTP_SEND` (۳۰) **در ساعت**.
* سرریز: `429` با کد `RATE_LIMITED` و در مواردی هدر `Retry-After`.
* روی `/license/*` یک نگهبان اضافه هم هست: پس از `BF_MAX_FAILS` (پیش‌فرض ۵) تلاش ناموفق از یک IP، پاسخ `429 LOCKED_OUT` با `details.retry_after` و هدر `Retry-After` برمی‌گردد تا `BF_LOCKOUT_SECONDS` (۳۰ ثانیه).

---

## ۲. اندپوینت‌های ریشه و عمومی

### `GET /metrics`
* **کاربرد:** متریک‌های Prometheus. **احراز هویت: ندارد** (باید در لبه‌ی شبکه محدود شود).
* **پاسخ:** متن متریک‌های Prometheus (خارج از پوشش).

### `GET /health`
* **کاربرد:** بررسی سلامت. **احراز هویت: ندارد.**
* **پاسخ موفق:** `200` → `{"status":"ok","checks":{"database":"ok"},"version":"dev"}`
* **پاسخ خطا:** `503` → `status: "degraded"` و `checks.database` حاوی متن خطا.

### `GET /docs` و `GET /docs/` و `GET /docs/openapi.yaml`
* **کاربرد:** رابط Scalar برای مرور API و فایل OpenAPI. **احراز هویت: ندارد.**

### `GET /api/v1/version`
* **پاسخ:** `{version, commit, build_date, project, project_url}`

### `GET /api/v1/config`
* **کاربرد:** تنظیمات عمومی برای صفحه‌ی ورود و برندینگ. **بدون احراز هویت.**
* **پاسخ:** مجموعه‌ی کلیدهای عمومی (`site_name`, `timezone`, `brand_color`, `language`, `logo_url`) + `base_url` + **`attribution_text` و `attribution_url`** (الزام AGPL؛ حذف آن‌ها نقض لایسنس است).

### `GET /api/v1/setup/status`
* **پاسخ:** `{needed: bool, step: "initialize" | "complete"}`

### `POST /api/v1/setup/initialize`
* **کاربرد:** تنها مسیری که بدون احراز هویت اولین owner را می‌سازد.
* **بدنه اجباری:** `admin_name`, `admin_email`, `site_name`, `product_name`, `product_slug`, `product_type` (`desktop`|`saas`|`hybrid`)
* **پاسخ موفق:** `200` با `{user, product, plan}`
* **خطا:** `409 SETUP_COMPLETE` اگر قبلاً انجام شده باشد؛ `400` برای ورودی نامعتبر.
* **اثر:** یک کاربر owner، یک محصول و یک طرح پیش‌فرض می‌سازد و `setup_complete` را ثبت می‌کند.

---

## ۳. کلید عمومی توکن آفلاین

### `GET /api/v1/license/pubkey`
* **احراز هویت: ندارد.** هدر `Cache-Control: public, max-age=3600`.
* **پاسخ:**

```json
{
  "success": true,
  "data": {
    "kid": "key_1a2b3c4d5e6f7a8b",
    "algorithm": "ed25519",
    "public_key": "<64 بایت به صورت hex: 128 کاراکتر>",
    "format": "hex",
    "keys": [
      { "kid": "key_1a2b3c4d5e6f7a8b", "algorithm": "ed25519", "public_key": "…", "format": "hex", "status": "active" }
    ]
  }
}
```

* **نکته‌ی مهم:** فرمت **hex** است، نه base64/PEM. `kid` از `"key_" + sha256(pubkey)[:8]` ساخته می‌شود.
* **اثر چرخش کلید:** این endpoint فقط کلید **جاری سرور** را برمی‌گرداند. اگر روی سرور `LICENSE_SIGNING_KEY` عوض شود، توکن‌های صادرشده با کلید قبلی برای کلاینتی که کلید را کش کرده، «امضای نامعتبر» می‌شوند. کلید عمومی را باید در کنار `kid` ذخیره کنید.

---

## ۴. APIهای عمومی لایسنس (SDK) — گروه `/api/v1/license/*`

همه‌ی این‌ها **بدون API key** هستند: `license_key` خودش اعتبارنامه است. همه زیر `LicenseBruteForceGuard` و rate limit گروه `license` هستند.

### `POST /api/v1/license/activate`
* **کاربرد:** فعال‌سازی لایسنس روی یک شناسه (دستگاه یا کاربر) و دریافت توکن امضاشده.
* **بدنه:**

| فیلد | نوع | اجباری | توضیح |
| --- | --- | --- | --- |
| `license_key` | string | ✅ | کلید، به شکل `KG-XXXX-XXXX-XXXX-XXXX` |
| `identifier` | string | ✅ | حداکثر ۲۵۶ کاراکتر؛ فرمت‌های مجاز در بخش ۹ |
| `identifier_type` | string | ❌ | `device` (پیش‌فرض) یا `user` |
| `label` | string | ❌ | نام خوانا برای دستگاه |
| `previous_identifier` | string | ❌ | شناسه‌ی قبلی برای مهاجرت درجا بدون مصرف سهمیه |

فیلد `scheme` در برخی نمونه‌کدها فرستاده می‌شود اما **سرور آن را نمی‌خواند**؛ Scheme از شکل `identifier` محاسبه می‌شود.

* **نمونه درخواست:**

```bash
curl -sX POST "$BASE/api/v1/license/activate" \
  -H 'Content-Type: application/json' \
  -H 'Idempotency-Key: 8f14e45f' \
  -d '{
    "license_key": "KG-ABCD1234-EFGH5678-IJKL9012-MNOP3456",
    "identifier": "sha256:3a7bd3e2360a3d29eea436fcfb7e44c735d117c42d1c1835420b6b9942dd4f1b",
    "identifier_type": "device",
    "label": "WORKSTATION-01"
  }'
```

* **پاسخ موفق (`200`):**

```json
{
  "success": true,
  "data": {
    "status": "activated",
    "license_id": "lic_01J8Z…",
    "token": "eyJ2ZXIiOiIxIiwia2lkIjoia2V5XzFhMmIzYzRk….<signature>",
    "meta": { "server": "Keygate", "url": "https://keygate.app" }
  }
}
```

`status` یا `"activated"` است یا `"already_activated"` (وقتی همان `identifier` از قبل فعال است؛ سهمیه‌ی جدید مصرف نمی‌شود).

* **کدهای وضعیت:**

| کد | code | معنا |
| --- | --- | --- |
| 200 | — | فعال یا از قبل فعال |
| 400 | `BAD_REQUEST` | فیلد اجباری خالی یا `identifier` نامعتبر |
| 403 | `LICENSE_EXPIRED` / `LICENSE_SUSPENDED` / `LICENSE_REVOKED` / `LICENSE_CANCELED` / `LICENSE_INVALID` | لایسنس قابل استفاده نیست |
| 404 | `LICENSE_NOT_FOUND` | کلید وجود ندارد **یا** `product_id` ناسازگار |
| 404 | `FEATURE_NOT_AVAILABLE` | محصول از نوع `saas` است و فعال‌سازی دستگاه ندارد |
| 409 | `ACTIVATION_LIMIT` | سهمیه پر است؛ `details: {max, current}` |
| 409 | `IDEMPOTENCY_IN_FLIGHT` | درخواست هم‌زمان با همان کلید |
| 413 | `BODY_TOO_LARGE` | بدنه بیش از ۲۵۶ KiB |
| 422 | `IDEMPOTENCY_KEY_CONFLICT` | همان Idempotency-Key با بدنه‌ی دیگر |
| 429 | `RATE_LIMITED` / `LOCKED_OUT` | محدودیت نرخ یا قفل brute force |

* **اثر بر وضعیت:** در حالت موفق یک ردیف `activations` با `status="active"` ساخته می‌شود، `audit_logs` یک ردیف `activated` می‌گیرد، رویداد `license.activated` به webhook می‌رود، و متریک `license_activations_total` افزایش می‌یابد.
* **اثر دیتابیس:** INSERT در `activations`؛ در مسیر مهاجرت، UPDATE شناسه‌ی فعال‌سازی موجود.

### `POST /api/v1/license/verify`
* **کاربرد:** بررسی اعتبار لایسنس + فعال‌بودن همین شناسه + دریافت **توکن تازه**.
* **بدنه اجباری:** `license_key`, `identifier`
* **پاسخ موفق (`200`):**

```json
{
  "success": true,
  "data": {
    "status": "active",
    "plan_id": "pln_…",
    "plan_name": "Pro Annual",
    "valid_until": "2027-01-01T00:00:00Z",
    "updates_until": "2026-07-01T00:00:00Z",
    "features": { "export_pdf": true, "api_calls": 1000 },
    "token": "<توکن امضاشده>",
    "grace_days": 7,
    "meta": { "server": "Keygate", "url": "https://keygate.app" },
    "external_customer_id": "cust_123",
    "external_workspace_id": "ws_456"
  }
}
```

* **کدهای وضعیت:** `200` موفق، `400` ورودی نامعتبر، `404 LICENSE_NOT_FOUND` برای **همه‌ی** موارد زیر:
  کلید ناموجود، محصول ناسازگار، محصول بدون قابلیت فعال‌سازی، لایسنس expired/suspended/revoked/canceled، و «این دستگاه فعال نشده».
  این «جمع‌کردن در یک ۴۰۴» عمدی است تا endpoint به اوراکل وجود کلید تبدیل نشود.
* **اثر دیتابیس:** `UPDATE activations SET last_verified = now()`، افزایش متریک `license_verifications_total{result="valid"|"unusable"|"not_activated"}`.
* **هدر Idempotency:** پشتیبانی **نمی‌شود** (این مسیر idempotent است).

### `POST /api/v1/license/deactivate`
* **بدنه اجباری:** `license_key`, `identifier`
* **پاسخ موفق:** `200 {status: "deactivated"}`
* **خطا:** `404 LICENSE_NOT_FOUND` (هم برای کلید ناموجود و هم برای شناسه‌ی ناشناس).
* **اثر:** «غیرفعال‌سازی نرم» (`status = 'deactivated'`، `deactivated_at`, `deactivated_reason='client'`) — سهمیه آزاد می‌شود، ردیف حذف نمی‌شود. رویداد `license.deactivated` به webhook می‌رود.
* **نکته:** این عمل را نمی‌توان به‌عنوان «اثبات لغو» در دستگاه آفلاین تضمین کرد؛ کلاینتی که اینترنت ندارد همچنان تا پایان `exp` توکن خود کار می‌کند.

### `POST /api/v1/license/entitlements`
* **بدنه:** `license_key` (اجباری)، `feature` (اختیاری — بررسی یک قابلیت خاص)
* **پاسخ موفق:** `{licensed: true, status, plan_id, plan_name, features: { "<feature>": {enabled, value_type, value, used?, limit?, remaining?, period?, resets_at?} }}`
* **نکته:** مقادیر `bool` با `enabled` و `value_type="quota"` با `used/limit/remaining/period/resets_at` می‌آیند. `remaining = -1` یعنی سهمیه‌ی نامحدود (وقتی `value` صفر باشد).
* **خطا:** `404 LICENSE_NOT_FOUND` برای هر حالت غیرقابل‌استفاده.
* **اثر:** هیچ نوشتنی ندارد (فقط خواندن؛ `last_verified` را هم لمس نمی‌کند).

### `POST /api/v1/license/usage`
* **بدنه:** `license_key` ✅، `feature` ✅، `quantity` (پیش‌فرض ۱؛ منفی `400`)، `metadata` (JSON آزاد)
* **پاسخ موفق:** `{accepted: true, used, limit, remaining, period, period_key}`
* **خطاهای کلیدی:** `400 NO_QUOTA` (قابلیت از نوع quota وجود ندارد)، `429 QUOTA_EXCEEDED` (`details: {used, limit, period}`)، `404 LICENSE_NOT_FOUND`.
* **اثر دیتابیس:** افزایش **اتمی** `usage_counters` و درج `usage_events`؛ در صورت پر شدن، webhook `quota.exceeded`؛ در عبور از آستانه‌ی هشدار (پیش‌فرض ۸۰٪) یک‌بار webhook `quota.warning` + ایمیل.
* **Idempotency:** پشتیبانی می‌شود.
* **توجه:** اگر entitlement به `stripe_meter_event_name` وصل باشد، یک ردیف `metered_billing` با «دلتای همین فراخوانی» ساخته می‌شود و کار پس‌زمینه آن را به Stripe می‌فرستد.

### `POST /api/v1/license/usage/status`
* **بدنه:** `license_key`, `feature`
* **پاسخ:** `{feature, used, limit, remaining, period, period_key}`
* **خطا:** `404 QUOTA_NOT_FOUND` اگر آن feature سهمیه نداشته باشد.

### `POST /api/v1/license/floating/checkout`
* **کاربرد:** گرفتن یک نشست شناور (floating) برای پلن‌های `license_model="floating"`.
* **بدنه:** `license_key` ✅، `identifier` ✅، `label` ❌
* **پاسخ موفق:** `{session_id, expires_at, active_sessions, max_sessions}`
* **خطا:** `409 FLOATING_LIMIT` با `details {active, max}`؛ `404 LICENSE_NOT_FOUND` (شامل پلن‌های غیر floating، تا اوراکل نشود).
* **انقضا:** `floating_timeout` دقیقه‌ای پلن (اگر صفر/منفی باشد ۳۰ دقیقه). ظرفیت = `max_activations`.
* **Idempotency:** پشتیبانی می‌شود.

### `POST /api/v1/license/floating/checkin`
* **بدنه:** `license_key`, `identifier` → `200 {status: "checked_in"}`

### `POST /api/v1/license/floating/heartbeat`
* **بدنه:** `license_key`, `identifier`
* **پاسخ موفق:** `{status: "ok", expires_at, remaining_seconds, active_sessions, max_sessions}`
* **خطا:** `404 SESSION_NOT_FOUND` وقتی نشستی برای این شناسه وجود ندارد یا منقضی شده — یعنی «باید دوباره checkout کنی».

### `POST /api/v1/license/download`
* **کاربرد:** دریافت URL امضاشده‌ی دانلود (موقت) برای نسخه‌ی مجاز یک لایسنس.
* **بدنه:** `license_key` ✅، `platform` ✅ (`darwin-arm64`, `darwin-x64`, `windows-arm64`, `windows-x64`, `linux-arm64`, `linux-x64`, `linux-armhf`)، `version` ❌ (SemVer؛ خالی = آخرین نسخه‌ی همان پلتفرم)، `channel` ❌ (`stable` پیش‌فرض؛ `beta`/`alpha`/`dev`).
* **پاسخ موفق:** `{url, expires_at, version, platform, sha256, file_size}`
* **خطا:** `400 INVALID_PLATFORM` / `INVALID_CHANNEL`، `403 UPDATES_EXPIRED` (نسخه بعد از پایان دوره‌ی آپدیت منتشر شده)، `404 LICENSE_NOT_FOUND` / `RELEASE_NOT_FOUND` / `PLATFORM_NOT_AVAILABLE`، `410 RELEASE_YANKED`، `503 STORAGE_DISABLED`.
* **نکته:** طول عمر URL از `STORAGE_DOWNLOAD_TTL` (پیش‌فرض `10m`).

---

## ۵. فیدهای عمومی آپدیت — گروه `/api/v1/releases/*`

همه‌ی کانال‌ها (`stable`, `beta`, `alpha`, `dev`) **عمومی** هستند؛ اعتماد از امضای artifact می‌آید، نه از مخفی بودن URL.

| متد | مسیر | کاربرد |
| --- | --- | --- |
| GET | `/releases/:product_slug/feed.xml` | فید Sparkle (appcast) |
| GET | `/releases/:product_slug/feed.json` | فید Velopack |
| GET | `/releases/:product_slug/velopack/*path` | مسیر اختصاصی Velopack (فایل `releases.<channel>.json` و `.nupkg`) |
| GET | `/releases/:product_slug/upgrade.json` | مانیفست Tauri (یک نسخه؛ `204` اگر آپدیتی نیست) |
| GET | `/releases/:product_slug/signing-key` | کشف کلیدهای عمومی امضای محصول |
| GET | `/releases/feed.xml` | **۴۱۰ `FEED_PATH_REMOVED`** — مسیر قدیمی، حذف شده |
| GET | `/releases/feed.json` | همان ۴۱۰ |
| GET | `/releases/upgrade.json` | همان ۴۱۰ |
| GET | `/releases/feed` | همان ۴۱۰ |

**پارامترهای query مشترک:** `platform` (اجباری)، `channel`، `limit` (۱ تا ۱۰۰، پیش‌فرض ۲۰ در فیدها و ۱ در Tauri)، و برای Velopack: `id`, `rid`, `localVersion`, `channel`.

**ارسال لایسنس (اختیاری، برای محصولات با دوره‌ی آپدیت):**

* کلید در **هدر** `X-License-Key` (فرستادن `license_key` در query → `400`).
* یا توکن امضاشده در query `license_token` / هدر `X-License-Token`.
* اگر `product.feed_license_required = true` و هیچ‌کدام نیاید → `401 LICENSE_KEY_REQUIRED`.
* کلید/توکن نامعتبر → `404 LICENSE_NOT_FOUND` (اگر فید عمومی برمی‌گشت، دروازه دور زده می‌شد).
* پاسخ‌های وابسته به لایسنس با `Cache-Control: private, no-store` و `Vary: X-License-Key, X-License-Token` می‌آیند؛ پاسخ عمومی `public, max-age=<FeedPublicMaxAge>`.

**`GET .../signing-key` پاسخ:**

```json
{
  "success": true,
  "data": {
    "keys": [
      {
        "kid": "key_1a2b…",
        "algorithm": "ed25519",
        "public_key_pem": "-----BEGIN ED25519 PUBLIC KEY-----\n…\n",
        "public_key_b64": "<base64 32 بایت>",
        "tauri_pubkey": "<کلید مینی‌ساین Tauri>",
        "status": "active"   // یا "retired"
      }
    ]
  }
}
```

**توجه:** هم کلید فعال و هم کلیدهای بازنشسته برگردانده می‌شوند تا کلاینت بتواند امضای نسخه‌های قدیمی را هم بررسی کند.

---

## ۶. احراز هویت، ثبت‌نام و پرداخت

### `GET /api/v1/auth/providers`
* **پاسخ:** `{dev_login: bool, otp: bool}` — `dev_login` فقط در `ENVIRONMENT=development` و `BASE_URL` لوکال `true` است.

### `POST /api/v1/auth/otp/send`
* **بدنه:** `{email}`
* **پاسخ:** `200 {status: "sent"}` (حتی اگر ایمیل در حالت `licensed_only` مجاز نباشد، پاسخ کلی است).
* **محدودیت:** سبد جداگانه‌ی ۳۰ درخواست در **ساعت** به‌ازای IP؛ بیشتر از آن `429 RATE_LIMITED`.
* **حالت توسعه:** بدون SMTP، کد در لاگ سرور چاپ می‌شود.

### `POST /api/v1/auth/otp/verify`
* **بدنه:** `{email, code}`
* **پاسخ موفق:** `{status, email, name, is_admin, role}` و **ست‌شدن دو کوکی**:
  * `session`: عمر ۲۴ ساعت، مسیر `/`، `HttpOnly`، `SameSite=Lax`، `Secure` اگر درخواست HTTPS باشد.
  * `refresh_token`: عمر ۳۰ روز، مسیر محدود `/api/v1/auth/refresh`.
* **خطا:** `401` برای کد نامعتبر/منقضی/تلاش بیش از حد.
* **قواعد واقعی:** کد ۶ رقمی است، **هش SHA-256** آن ذخیره می‌شود، تعداد تلاش محدود است، و `signup_mode=licensed_only` جلوی ساخت حساب برای ایمیل‌های بدون لایسنس را می‌گیرد.

### `POST /api/v1/auth/dev-login`
* **فقط توسعه.** بدنه: `{email, name?}` → ست‌کردن `session` + `refresh_token`. در غیر این حالت `404`.

### `POST /api/v1/auth/logout`
* **احراز هویت:** نشست لازم است. کوکی‌ها را پاک و توکن‌های refresh کاربر را باطل می‌کند.

### `POST /api/v1/auth/refresh`
* **بدون بدنه؛** با کوکی `refresh_token`. توکن را **می‌چرخاند**.
* **خطا:** `401` اگر کوکی نباشد/نامعتبر باشد. اگر توکنِ چرخیده‌شده دوباره استفاده شود (نشانه‌ی سرقت)، **همه‌ی** توکن‌های آن کاربر باطل و `401` برگردانده می‌شود.
* **تحمل نشست‌های هم‌زمان:** توکنِ «تازه چرخیده‌شده» چند ثانیه تحمل دارد (برای چند تب).

### `POST /api/v1/invites/accept`
* **عمومی (بدون نشست)؛** بدنه: `{token}`.
* **کاربرد:** پذیرش دعوت صندلی و **ورود خودکار** (سرور همان‌جا نشست صادر می‌کند).
* **پاسخ:** `{user_id, email, license_id, product_name?, role}`
* **خطا:** `404 SEAT_INVITE_INVALID` برای توکن نامعتبر/منقضی/پذیرفته‌شده **یا** لایسنس نامعتبر — همه در یک کد، برای جلوگیری از آزمودن توکن‌ها.
* **محدودیت:** ۶۰ درخواست در دقیقه به‌ازای IP.

### `GET /api/v1/products/:product_slug/plans`
* **کاربرد:** کاتالوگ طرح برای صفحه‌ی قیمت‌گذاری، **بدون نشست**.
* **پاسخ:** طرح‌های فعال محصول به‌همراه قیمت خوانده‌شده از Stripe (با کش و بودجه‌ی زمانی برای جلوگیری از تبدیل صفحه‌ی عمومی به آمپلی‌فایر Stripe).

### `POST /api/v1/webhook/stripe`
* **کاربرد:** دریافت رویدادهای Stripe. احراز هویت با **امضای خود Stripe** و بررسی `livemode` مطابق `STRIPE_LIVEMODE`. بدون امضا معتبر `400`.
* **محدودیت:** ۶۰ درخواست در دقیقه به‌ازای IP.
* **اثر:** ساخت/به‌روزرسانی لایسنس، اشتراک، تمدید، لغو و همگام‌سازی وضعیت.

### `GET /api/v1/checkout/verify`
* **query:** `session_id`
* **کاربرد:** بازگشت از Checkout Stripe؛ اعتبارسنجی نشست خرید.
* **محدودیت:** ۶۰ در هر دقیقه (هر فراخوانی یک تماس به Stripe می‌زند).

### `GET /pay/:checkout_id`
* **کاربرد:** ورود یکپارچه به Stripe Checkout با `checkout_id` طرح. **بدون احراز هویت** (خریدار تازه هنوز حساب ندارد).

---

## ۷. APIهای پورتال مشتری — گروه `/api/v1/portal/*`

**احراز هویت: کوکی نشست (`SessionAuth`) برای همه.** درخواست‌های بدنه‌دار از دو نگهبان عبور می‌کنند: `portalLicenseGuard` (مالک یا صندلی پذیرفته‌شده) و `portalSeatMutationGuard` (فقط مالک یا صندلی با نقش `admin` و وضعیت پذیرفته‌شده). سقف بدنه‌ی این نگهبان‌ها ۶۴ KiB است.

| متد | مسیر | کاربرد | بدنه/پارامتر | نکات |
| --- | --- | --- | --- | --- |
| GET | `/portal/me` | هویت کاربر جاری | — | `{id, email, name, avatar_url, is_admin, role}` |
| GET | `/portal/licenses` | لایسنس‌های کاربر | — | **کلید لایسنس را هم برمی‌گرداند** (اعتبارنامه‌ی خود مشتری) + `renewals_enabled`؛ `Cache-Control: no-store` |
| PUT | `/portal/profile` | ویرایش نام | `{name}` | تگ‌های HTML حذف و طول محدود (۱۰۰) می‌شود |
| GET | `/portal/plans?product_id=` | طرح‌های فعال یک محصول | query `product_id` ✅ | بدون `product_id` → `400` |
| POST | `/portal/usage` | ثبت مصرف | `{license_key, feature, quantity?, metadata?}` | همان قواعد `/license/usage` |
| POST | `/portal/usage/status` | وضعیت سهمیه | `{license_key, feature}` | — |
| POST | `/portal/seats` | فهرست صندلی‌ها | `{license_key}` | `{seats: []}` |
| POST | `/portal/seats/add` | دعوت عضو | `{license_key, email, role?}` (`admin`/`member`) | `400` برای دعوت مالک، `409 SEAT_LIMIT` |
| POST | `/portal/seats/remove` | حذف عضو | `{license_key, seat_id}` | — |
| GET | `/portal/licenses/:license_key/activations` | دستگاه‌های فعال | — | `{activations: [], max}` — فقط فعال‌ها |
| DELETE | `/portal/licenses/:license_key/activations/:activation_id` | آزادسازی سهمیه | — | مالک یا هر صندلی پذیرفته‌شده؛ خروجی `{status:"deleted"}` (حتی اگر قبلاً غیرفعال شده باشد) |
| POST | `/portal/subscription/change-plan` | تغییر طرح | `{license_id, new_price_id, prorate?}` | مالکیت لایسنس با ایمیل نشست بررسی می‌شود |
| POST | `/portal/subscription/cancel` | لغو اشتراک | `{license_id, immediate?}` | — |
| POST | `/portal/subscription/billing-portal` | لینک پورتال Stripe | `{license_id}` | `{url}` |
| GET | `/portal/subscription/invoices?license_id=` | فاکتورها | query | `{invoices: []}` |
| POST | `/portal/updates/renew` | تمدید دوره‌ی آپدیت | `{license_id}` | `{url}` — خرید یک‌باره برای لایسنس perpetual |

---

## ۸. APIهای مدیریتی — گروه `/api/v1/admin/*`

**دو مسیر احراز هویت:** نشست ادمین (`is_admin=true` از دیتابیس، نه از توکن) **یا** کلید `kg_live_…`. هر گروه سه دسته دارد:

| دسته | scope لازم | نمونه |
| --- | --- | --- |
| `admin` | `admin` (یا نشست ادمین) | محصول، طرح، قابلیت، کلید API، webhook، افزودنی، تنظیمات، تیم، سیستم، تحلیل، حسابرسی، کاربران، **کلیدهای امضا** |
| `licWrite` | `admin` یا `licenses:write` | CRUD و چرخه‌ی عمر لایسنس، فعال‌سازی‌ها، transfer، offline-token |
| `relWrite` | `admin` یا `releases:write` | انتشار، artifactها، publish/yank/unyank |

تمام مسیرهای مدیریتی rate limit ادمین (پیش‌فرض ۱۲۰ در دقیقه) دارند. کلیدهای API می‌توانند به یک `product_id` مقید باشند؛ در آن حالت درخواست برای محصول دیگر با `403 PRODUCT_SCOPE_MISMATCH` رد می‌شود.

### ۸.۱ آمار، محصول، طرح، قابلیت

| متد | مسیر | توضیح | بدنه‌ی کلیدی |
| --- | --- | --- | --- |
| GET | `/admin/stats` | آمار کلی | — |
| GET | `/admin/products` | فهرست محصولات | query: `search`, `type` (چند مقدار با کاما), `limit`, `offset` |
| GET | `/admin/products/:id` | یک محصول | — |
| POST | `/admin/products` | ساخت محصول | `name` ✅، `slug` ✅، `type` ✅ (`desktop`/`saas`/`hybrid`)، `feed_license_required?`، `download_url?` |
| PUT | `/admin/products/:id` | ویرایش محصول | فیلدهای محصول (مثل `require_signing`, `minimum_supported_version`, `minimum_supported_message`) |
| DELETE | `/admin/products/:id` | حذف محصول | — |
| GET | `/admin/plans` | فهرست طرح‌ها | `product_id`, `search`, `limit`, `offset` |
| GET | `/admin/plans/:id` | یک طرح | — |
| POST | `/admin/plans` | ساخت طرح | `product_id` ✅، `name` ✅، `slug` ✅، `license_type` ✅ (`subscription`/`perpetual`/`trial`)، `billing_interval?`، `max_activations?`، `max_seats?`، `trial_days?`، `grace_days?`، `updates_days?`، `renewal_days?`، `stripe_price_id?`، `stripe_renewal_price_id?`، `license_model?`، `floating_timeout?`، `token_ttl_days?`، `sort_order?` |
| PUT | `/admin/plans/:id` | ویرایش طرح | همان فیلدها |
| DELETE | `/admin/plans/:id` | حذف طرح | — |
| POST | `/admin/entitlements` | افزودن قابلیت به طرح | `plan_id` ✅، `feature` ✅، `value_type` ✅ (`bool`/`quota`)، `value` ✅، `quota_period?`، `quota_unit?`، `stripe_meter_event_name?` |
| PUT | `/admin/entitlements/:id` | ویرایش قابلیت | — |
| DELETE | `/admin/entitlements/:id` | حذف قابلیت | — |

### ۸.۲ لایسنس‌ها (دسته `licWrite`)

| متد | مسیر | توضیح | ورودی/خروجی کلیدی |
| --- | --- | --- | --- |
| GET | `/admin/licenses` | فهرست | فیلتر: `product_id`, `status`, `search`, `external_customer_id`, `external_workspace_id`؛ `sort`: `created_at`, `valid_until`, `email`, `status`, `product`, `plan`؛ پاسخ شامل `license_key_hints` |
| GET | `/admin/licenses/export` | خروجی CSV | همان فیلترها (فرمت CSV با مراقبت از فرمول‌های صفحه‌گسترده) |
| GET | `/admin/licenses/:id` | جزئیات | `license_key_hint`, `key_usable`, `key_unusable_reason` |
| GET | `/admin/licenses/:id/key` | نمایش کلید | `{license_key}`، حسابرسی `key_revealed`، `Cache-Control: no-store`؛ اگر رمزگشایی ممکن نباشد `503 LICENSE_KEY_UNAVAILABLE` |
| POST | `/admin/licenses/:id/resend-email` | ارسال مجدد ایمیل کلید | گیرنده = ایمیل روی لایسنس |
| POST | `/admin/licenses` | ساخت لایسنس | `product_id` ✅، `plan_id` ✅، `email` ✅، `notes?`، `external_customer_id?`، `external_workspace_id?`، `valid_until?` (RFC3339 و در آینده) |
| POST | `/admin/licenses/:id/refund` | بازپرداخت (Stripe) | — |
| POST | `/admin/licenses/:id/revoke` | لغو | — |
| POST | `/admin/licenses/:id/suspend` | تعلیق | — |
| POST | `/admin/licenses/:id/reinstate` | بازگردانی | — |
| POST | `/admin/licenses/:id/valid-until` | تعیین انقضا | `{valid_until}` (خالی = همیشگی) |
| POST | `/admin/licenses/:id/updates-until` | تعیین پایان دوره‌ی آپدیت | `{updates_until}` (خالی = مادام‌العمر) |
| POST | `/admin/licenses/:id/change-plan` | تغییر طرح | `{plan_id}` |
| POST | `/admin/licenses/:id/stripe/unlink` | جدا کردن از Stripe | فقط وقتی اشتراک Stripe تمام شده |
| GET | `/admin/licenses/:id/usage` | رویدادها و شمارنده‌های مصرف | query: `feature`, `limit`, `offset` |
| POST | `/admin/licenses/:id/usage/reset` | صفر کردن شمارنده | `{feature, period?, period_key?}` |
| GET | `/admin/licenses/:id/seats` | صندلی‌ها | `{seats, active_count, total, limit, offset}` |
| GET | `/admin/licenses/:id/addons` | افزودنی‌های لایسنس | — |
| POST | `/admin/licenses/:id/addons` | افزودن افزودنی | `{addon_id}` |
| DELETE | `/admin/licenses/:id/addons/:addon_id` | حذف افزودنی | — |
| GET | `/admin/licenses/:id/floating` | نشست‌های شناور | `{sessions, active, total, limit, offset}` |
| POST | `/admin/licenses/:id/transfer` | انتقال لایسنس | `{new_email}` ✅، `release_activations?` (پیش‌فرض true)، `reason?`؛ خطاها: `409 IDENTICAL_EMAIL` |
| POST | `/admin/licenses/:id/offline-token` | صدور توکن آفلاین | `{identifier}` ✅، `label?` → `{token, status, license_id, identifier}`؛ **این کار یک فعال‌سازی واقعی انجام می‌دهد** و حسابرسی `offline_token_issued` ثبت می‌شود |
| POST | `/admin/activations/:id/deactivate` | غیرفعال‌سازی دستگاه | `{status:"deactivated", id}` |
| POST | `/admin/activations/:id/reactivate` | فعال‌سازی مجدد | `409 ACTIVATION_LIMIT` / `403 LICENSE_REVOKED|SUSPENDED|EXPIRED` |
| DELETE | `/admin/activations/:id` | حذف نرم | `204` بدون بدنه |

### ۸.۳ نسخه‌ها (دسته `relWrite`) و کلیدهای امضا (`admin`)

| متد | مسیر | توضیح | ورودی/خروجی |
| --- | --- | --- | --- |
| GET | `/admin/releases` | فهرست نسخه‌ها | query: `product_id`, `channel`, `status`, `limit`, `offset` |
| GET | `/admin/releases/:id` | یک نسخه + artifactها | — |
| POST | `/admin/releases` | ساخت draft | `product_id` ✅، `version` ✅ (SemVer)، `channel?`، `name?`، `release_notes?` |
| PATCH | `/admin/releases/:id` | ویرایش | `name?`, `release_notes?`, `channel?` |
| PATCH | `/admin/releases/:id/notes` | مسیر سازگاری قدیمی | همان handler بالا |
| DELETE | `/admin/releases/:id` | حذف draft | — |
| POST | `/admin/releases/:id/artifacts` | ثبت artifact و گرفتن URL آپلود | `platform` ✅، `content_type?`، `expected_size?`، `filename?` → `{artifact, upload_url, expires_at}` |
| POST | `/admin/releases/:id/artifacts/:aid/finalize` | نهایی‌سازی و محاسبه‌ی sha256 | `{expected_sha256?}` (نام قدیمی `sha256` هم پذیرفته می‌شود)؛ `409 SHA256_MISMATCH` در تناقض |
| DELETE | `/admin/releases/:id/artifacts/:aid` | حذف artifact | — |
| POST | `/admin/releases/:id/actions/publish` | انتشار + امضا | `409 SIGNING_KEY_MISSING` / `RELEASE_ARTIFACTS_NOT_SIGNED` / `ARTIFACT_NOT_UPLOADED` |
| POST | `/admin/releases/:id/actions/yank` | کنارگذاری | `{reason}` ✅ |
| POST | `/admin/releases/:id/actions/unyank` | بازگرداندن | — |
| GET | `/admin/products/:id/signing-keys` | فهرست کلیدها | `{keys: [{id, public_key, active, note, created_at, rotated_at}]}` |
| POST | `/admin/products/:id/signing-key` | ساخت کلید امضا | کلید خصوصی **هرگز** برگردانده نمی‌شود |
| POST | `/admin/products/:id/signing-key/rotate` | چرخش | `{note?}` |
| DELETE | `/admin/products/:id/signing-key` | بازنشسته‌کردن کلید فعال | `{note?}` |
| GET | `/admin/products/:id/signing-key/public.pem` | دانلود کلید عمومی (PEM) | — |
| GET | `/admin/products/:id/signing-key/tauri-pubkey` | کلید عمومی در قالب مینی‌ساین Tauri | `{pubkey}` |

### ۸.۴ کلیدهای API، webhook، افزودنی

| متد | مسیر | توضیح |
| --- | --- | --- |
| GET/POST | `/admin/api-keys` | فهرست/ساخت (`product_id?`, `name` ✅, `scopes?`) — کلید خام فقط در پاسخ ساخت |
| POST | `/admin/api-keys/:id/rotate` | چرخش و دریافت کلید جدید |
| DELETE | `/admin/api-keys/:id` | حذف |
| GET/POST | `/admin/webhooks` | فهرست/ساخت (`product_id` ✅, `url` ✅, `events` ✅, `secret?`) |
| PUT/DELETE | `/admin/webhooks/:id` | ویرایش/حذف |
| GET | `/admin/webhooks/:id/deliveries` | فهرست تحویل‌ها (فیلتر `status`, `event`) |
| GET | `/admin/webhooks/:id/deliveries/:delivery_id` | جزئیات یک تحویل |
| POST | `/admin/webhooks/:id/deliveries/:delivery_id/resend` | ارسال مجدد |
| POST | `/admin/webhooks/:id/test` | ارسال رویداد آزمایشی |
| GET/POST | `/admin/addons` | فهرست/ساخت افزودنی (`product_id`, `name`, `slug`, `feature`, `value_type`, `value`, `quota_*`) |
| PUT/DELETE | `/admin/addons/:id` | ویرایش/حذف |

### ۸.۵ تنظیمات، تیم، سیستم، تحلیل، حسابرسی، کاربران

| متد | مسیر | توضیح |
| --- | --- | --- |
| GET | `/admin/settings` | `{settings, secrets_set, email}` — رمزها هرگز خوانده نمی‌شوند |
| PUT | `/admin/settings` | بدنه `{settings: {…}}`؛ کلید ناشناخته `400`؛ مقدار خالی روی secret = «تغییر نده» |
| DELETE | `/admin/settings/secrets/:key` | پاک‌کردن صریح یک secret (`smtp_password` یا `cloudflare_api_token`) |
| POST | `/admin/settings/test-email` | ارسال ایمیل آزمایشی (`{to?}`) |
| POST | `/admin/system/run-expiry-checks` | اجرای دستی بررسی‌های انقضا |
| POST | `/admin/system/run-metered-sync` | اجرای دستی همگام‌سازی مصرف با Stripe |
| GET | `/admin/email-templates` | قالب‌های پیش‌فرض و سفارشی |
| GET/POST | `/admin/team` | فهرست/دعوت ادمین (`{email, role?}`) |
| DELETE | `/admin/team/:id` | حذف ادمین (با قواعد آخرین owner) |
| GET | `/admin/system/update-check` | بررسی نسخه‌ی جدید (کش یک‌ساعته) |
| GET | `/admin/system/migrations` | `{migrations: [{filename, applied_at}]}` |
| GET | `/admin/analytics` | اسنپ‌شات‌ها/تجمیع‌ها (`product_id`, `from`, `to`, `granularity`) |
| GET | `/admin/analytics/summary` | خلاصه‌ی شمارشی |
| GET | `/admin/analytics/breakdown` | تفکیک بر اساس `dimension` ✅ |
| GET | `/admin/analytics/usage-top` | پرکاربردترین قابلیت‌ها |
| GET | `/admin/analytics/activation-trend` | روند فعال‌سازی |
| GET | `/admin/analytics/insights` | رشد، نگهداشت، توزیع سن لایسنس، کاربران برتر |
| GET | `/admin/audit-logs` | فیلتر `entity`, `entity_id`, `product_id` + صفحه‌بندی |
| GET | `/admin/users` | `search` + صفحه‌بندی |
| GET | `/admin/users/:id` | جزئیات کاربر + `license_key_hints` |

---

## ۹. قرارداد شناسه‌ی دستگاه (آنچه سرور واقعاً بررسی می‌کند)

سرور فقط **فرمت** را اعتبارسنجی می‌کند (`pkg/apperr.ValidateIdentifier` → `deviceid.ValidateIdentifierFormat`):

| شکل | نمونه | نتیجه |
| --- | --- | --- |
| `kgid:v1` | `kgid:v1:windows:3f2a…(64 hex)` | قبول؛ پلتفرم باید در فهرست `darwin|windows|linux|android|ios|browser|other` باشد |
| `sha256:` | `sha256:<64 hex>` | قبول |
| UUID با پیشوند | `uuid:550e8400-e29b-41d4-a716-446655440000` | قبول |
| UUID خالی | `550e8400-e29b-41d4-a716-446655440000` | قبول |
| سایر رشته‌ها | `device-a1b2c3d4` | قبول به‌عنوان `opaque` |

قواعد سخت:

* فاصله‌ی ابتدا/انتها و کاراکترهای کنترلی/فاصله در میان رشته → `400`.
* طول بیش از ۲۵۶ کاراکتر → `400`.
* مقدار **همان‌طور که آمده ذخیره می‌شود** (trim ضمنی انجام نمی‌شود) تا اعتبارسنجی و ذخیره یکی باشند.
* سرور شناسه را hash **نمی‌کند**؛ اگر می‌خواهید حریم خصوصی حفظ شود، خودِ کلاینت باید مقدار خام را هش کند (روش پیشنهادی در [csharp-dotnet-integration.md](csharp-dotnet-integration.md)).
* سرور تفاوت سخت‌افزاری را تشخیص **نمی‌دهد**؛ `scheme` فقط برچسب است و «امتیاز شباهت» یا بازدارندگی جعل وجود ندارد.
