# مرجع API در Keygate

این سند **همهٔ routeهای ثبت‌شده** در `cmd/server/main.go` را با یک قالب یکسان مستند می‌کند.
منبع حقیقت، خود کد است؛ هرجا OpenAPI (`docs/openapi.yaml`) با پیاده‌سازی تفاوت داشته باشد، در
`documentation-audit.md` ثبت شده است.

**تعداد کل routeهای ثبت‌شده در این نسخه: ۱۵۲**

مبنای این شمارش، `cmd/server/main.go` در شاخهٔ `update-docs` (HEAD = `8124d95`) است:
**۱۵۱** ثبت `GET/POST/PUT/PATCH/DELETE` به‌همراه **۱** ثبت `StaticFile("/docs/openapi.yaml")`.
مسیرهای `/docs` و `/docs/` یک handler مشترک دارند و در یک عنوان مستند شده‌اند؛ به همین دلیل
تعداد عنوان‌های endpoint در این فایل ۱۵۱ است، ولی routeهای ثبت‌شده ۱۵۲.

| دسته | تعداد | بخش |
|---|---|---|
| زیرساخت (`/metrics`, `/health`, `/docs*`, `/pay/:checkout_id`) | ۶ | §۱ |
| سیستم و راه‌اندازی اولیه | ۵ | §۲ |
| SDK عمومی لایسنس (`/license/*`) | ۱۰ | §۳ |
| دعوت‌نامه (`/invites/accept`) | ۱ | §۴ |
| فیدهای انتشار و کشف کلید امضا | ۹ | §۵ |
| احراز هویت (`/auth/*`) | ۶ | §۶ |
| کاتالوگ عمومی پلن | ۱ | §۷ |
| Stripe | ۲ | §۸ |
| پورتال مشتری | ۱۶ | §۹ |
| پنل/API مدیریتی | ۹۶ | §۱۰ |
| **جمع** | **۱۵۲** | |

---

## قالب مستند هر endpoint

برای هر route یک جدول با ردیف‌های شماره‌دار ۱ تا ۲۰ می‌آید. ردیف‌هایی که برای آن route معنا
ندارند (مثلاً «بدنه» برای یک `GET`) با `—` پر می‌شوند و در برخی بخش‌های قدیمی‌تر حذف شده‌اند؛
شمارهٔ ردیف‌ها ثابت است تا تطبیق دو endpoint با هم آسان بماند.

| # | فیلد | معنا |
|---|---|---|
| ۱ | هدف | این endpoint چه کاری می‌کند |
| ۲ | متد و مسیر | `METHOD /path` |
| ۳ | دسترسی | public / protected |
| ۴ | نوع احراز هویت | session / API Key / license_key / — |
| ۵ | scope | `admin` / `licenses:write` / `releases:write` / — |
| ۶ | پارامترهای path | `:param`ها |
| ۷ | پارامترهای query | query string |
| ۸ | هدرها | هدرهای مهم |
| ۹ | بدنه (body) | ساختار JSON |
| ۱۰ | الزامی/اختیاری | کدام فیلدها لازم‌اند |
| ۱۱ | نوع و معنی فیلدها | نوع و تفسیر |
| ۱۲ | نمونهٔ درخواست | `curl` واقعی |
| ۱۳ | پاسخ موفق | شکل `data` |
| ۱۴ | کدهای وضعیت | 200/201/204/… |
| ۱۵ | خطاهای مورد انتظار | کدهای خطا |
| ۱۶ | نمونهٔ خطا | JSON خطا |
| ۱۷ | اثر روی دیتابیس/وضعیت | نوشتن/تغییر side effect |
| ۱۸ | نکات امنیتی | هشدارها |
| ۱۹ | تست خودکار؟ | آیا تست دارد |
| ۲۰ | یادداشت | نکتهٔ اضافی |

---

## قواعد مشترک (Conventions)

### پوشش پاسخ (Response envelope)

همهٔ مسیرهای زیر `/api/v1`:

* موفق: `{"success":true,"data":<محتوا>}`
* خطا: `{"success":false,"error":{"code":"...","message":"...","details":<optional>}}`
* دقیقاً یکی از `data` یا `error` می‌آید.
* `204 No Content` تنها در پاسخ بدون بدنه (مثلاً DELETEهای موفق خاص) استفاده می‌شود.

استثناها: `/health`, `/metrics`, `/docs*`, `/pay/:checkout_id`, فایل PEM کلید عمومی release
و خروجی CSV اکسل لایسنس‌ها در envelope نیستند.

### کدهای خطای استاندارد

| کد | وضعیت | معنا |
|---|---|---|
| `BAD_REQUEST` | 400 | ورودی نامعتبر |
| `UNAUTHORIZED` | 401 | اعتبارنامه ندارد/نامعتبر |
| `FORBIDDEN` | 403 | اجازه ندارد |
| `NOT_FOUND` | 404 | یافت نشد |
| `INTERNAL_ERROR` | 500 | خطای سرور |

### پاسخ‌های لیستی

```json
{ "items": [ ... ], "total": 12, "limit": 50, "offset": 0 }
```

* `?limit=` پیش‌فرض ۵۰، سقف ۲۰۰ (`maxListLimit=200`).
* `?offset=` پیش‌فرض ۰.
* `?sort=` تنها مقادیر allowlist (در غیر این صورت `400 INVALID_SORT`).
* `?order=` مقادیر `asc`/`desc` (در غیر این صورت `400 INVALID_ORDER`).

### انواع احراز هویت

| نام | نحوهٔ استفاده |
|---|---|
| `—` (public) | بدون اعتبارنامه |
| `license_key` | فیلد `license_key` در بدنهٔ JSON |
| `session` | کوکی `session` (JWT) که مرورگر خودش می‌فرستد |
| `API Key` | هدر `Authorization: Bearer kg_live_…` |
| `SessionOrAPIKey` | هر یک از دو مورد بالا |

### هدرهای مهم

* `Authorization: Bearer <API Key | JWT>` — برای مسیرهای مدیریتی.
* `Idempotency-Key: <string>` — روی `license/activate`, `license/usage`, `license/floating/checkout`.
* `Stripe-Signature` — فقط روی `/webhook/stripe`.

### نکات کلی امنیتی

* کلاینت دسکتاپ هرگز نباید API Key مدیریتی داشته باشد.
* endpointهای `/license/*` عمومی‌اند و خطاها عمداً به یک `404 LICENSE_NOT_FOUND` تبدیل می‌شوند
  تا oracle تشخیص کلید نباشند.
* `X-Powered-By: Keygate` روی پاسخ‌ها الزام مجوز AGPL v3 §۷(b) است و نباید حذف شود.

## §۱. endpointهای زیرساختی (۶)

### ۱.۱ `GET /metrics`
| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | خروجی متریک Prometheus |
| ۲ | متد و مسیر | `GET /metrics` |
| ۳ | دسترسی | public (معمولاً فقط شبکهٔ داخلی) |
| ۴ | احراز هویت | — |
| ۵ | scope | — |
| ۶ | path | — |
| ۷ | query | — |
| ۸ | هدرها | — |
| ۹ | بدنه | — |
| ۱۰ | الزامی/اختیاری | — |
| ۱۱ | فیلدها | فرمت متن Prometheus |
| ۱۲ | نمونه | `curl http://localhost:9000/metrics` |
| ۱۳ | پاسخ موفق | متن متریک (نه envelope) |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | — |
| ۱۶ | نمونهٔ خطا | — |
| ۱۷ | اثر | هیچ |
| ۱۸ | امنیت | شامل متریک‌های حساس (activation/verification)؛ در production پشت فایروال نگه دارید |
| ۱۹ | تست؟ | ندارد |
| ۲۰ | یادداشت | handler استاندارد `promhttp` |

### ۱.۲ `GET /health`
| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | سلامت سرور و اتصال دیتابیس |
| ۲ | متد و مسیر | `GET /health` |
| ۳ | دسترسی | public |
| ۴–۸ | احراز هویت/scope/params/هدر | — |
| ۹ | بدنه | — |
| ۱۰ | الزامی/اختیاری | — |
| ۱۱ | فیلدها | `status` (`ok`/`degraded`)، `checks.database` (`ok` یا `error: …`)، `version` |
| ۱۲ | نمونه | `curl http://localhost:9000/health` |
| ۱۳ | پاسخ موفق | `{"status":"ok","checks":{"database":"ok"},"version":"dev"}` |
| ۱۴ | وضعیت | ۲۰۰ اگر `ok`؛ **۵۰۳** اگر `degraded` |
| ۱۵ | خطاها | وقتی DB پینگ نمی‌شود → ۵۰۳ |
| ۱۶ | نمونهٔ خطا | `{"status":"degraded","checks":{"database":"error: ..."},"version":"dev"}` |
| ۱۷ | اثر | هیچ |
| ۱۸ | امنیت | پیام خطای DB ممکن است در `checks` افشا شود؛ در production محدودش کنید |
| ۱۹ | تست؟ | ندارد |
| ۲۰ | یادداشت | پاسخ envelope ندارد؛ JSON خام است |

### ۱.۳ `GET /docs` و ۱.۴ `GET /docs/`
| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | صفحهٔ مستندات تعاملی API (Scalar) |
| ۲ | متد و مسیر | `GET /docs` و `GET /docs/` (هر دو یک handler) |
| ۳ | دسترسی | public |
| ۴–۸ | احراز هویت/params | — |
| ۹ | بدنه | — |
| ۱۱ | فیلدها | HTML |
| ۱۳ | پاسخ موفق | صفحهٔ HTML که `docs/openapi.yaml` را بارگذاری می‌کند |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | — |
| ۱۸ | امنیت | read-only؛ `hideTryIt=true` |
| ۱۹ | تست؟ | ندارد |
| ۲۰ | یادداشت | دو بار ثبت شده تا اسلش انتهایی هم کار کند |

### ۱.۵ `GET /docs/openapi.yaml`
| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | سرو فایل OpenAPI |
| ۲ | متد و مسیر | `GET /docs/openapi.yaml` |
| ۳ | دسترسی | public |
| ۱۳ | پاسخ موفق | محتوای خام `docs/openapi.yaml` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۸ | امنیت | فقط ۲۱ مسیر در آن مستند شده (نگاه کنید به `documentation-audit.md`) |
| ۱۹ | تست؟ | ندارد |
| ۲۰ | یادداشت | `r.StaticFile` |

### ۱.۶ `GET /pay/:checkout_id`
| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | ریدایرکت مشتری به Checkout استریپ برای پلن |
| ۲ | متد و مسیر | `GET /pay/:checkout_id` |
| ۳ | دسترسی | public (روی router ریشه، نه `/api/v1`) |
| ۴ | احراز هویت | — |
| ۵ | scope | — |
| ۶ | path | `:checkout_id` (باید **دقیقاً ۸ کاراکتر** باشد) |
| ۷ | query | — |
| ۸ | هدرها | — |
| ۹ | بدنه | — |
| ۱۰ | الزامی | `checkout_id` |
| ۱۱ | فیلدها | `checkout_id` = شناسهٔ عمومی پلن |
| ۱۲ | نمونه | `curl -i http://localhost:9000/pay/abcd1234` |
| ۱۳ | پاسخ موفق | **۳۰۷** redirect به URL چک‌اوت Stripe (بدنهٔ plain-text، نه envelope) |
| ۱۴ | وضعیت | ۳۰۷ |
| ۱۵ | خطاها | ۴۰۰ `invalid checkout id`، ۴۰۴ `plan not found`، ۴۱۰ `this plan is no longer available`، ۵۰۳ `this plan is temporarily unavailable` / `payment not configured for this plan`، ۵۰۰ `checkout unavailable` |
| ۱۶ | نمونهٔ خطا | متن plain مثل `plan not found` |
| ۱۷ | اثر | lookup قیمت Stripe + ساخت Checkout Session |
| ۱۸ | امنیت | نرخ‌محدود نمی‌شود در این مسیر؛ برای سوءاستفاده باید checkout_id واقعی را دانست |
| ۱۹ | تست؟ | **بله** — `internal/payment/renewal_test.go` (`TestCheckoutByPlan_BoundedPlanNeedsTheSwitch`) |
| ۲۰ | یادداشت | `stripeH.CheckoutByPlan` |

---

## §۲. سیستم و راه‌اندازی اولیه (۵)

### ۲.۱ `GET /api/v1/version`
| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | نسخهٔ سرور |
| ۲ | متد و مسیر | `GET /api/v1/version` |
| ۳ | دسترسی | public |
| ۴–۸ | احراز هویت/params | — |
| ۱۱ | فیلدها | `version`, `commit`, `build_date`, `project`, `project_url` |
| ۱۳ | پاسخ موفق | `{"version":"v1.0.0","commit":"abc1234","build_date":"2026-01-01T00:00:00Z","project":"Keygate","project_url":"https://keygate.app"}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | — |
| ۱۸ | امنیت | افشای نسخه؛ طبیعی است |
| ۱۹ | تست؟ | ندارد |

### ۲.۲ `GET /api/v1/setup/status`
| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | آیا setup لازم است؟ |
| ۲ | متد و مسیر | `GET /api/v1/setup/status` |
| ۳ | دسترسی | public |
| ۱۱ | فیلدها | `needed` (bool)، `step` (`complete`/`initialize`) |
| ۱۳ | پاسخ موفق | `{"needed":true,"step":"initialize"}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | — |
| ۱۸ | امنیت | فقط وضعیت را لو می‌دهد |
| ۱۹ | تست؟ | غیرمستقیم — `TestSetupAnswersStoredRows` |
| ۲۰ | یادداشت | `setupH.Status` |

### ۲.۳ `POST /api/v1/setup/initialize`
| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | ساخت owner/product/plan اولیه |
| ۲ | متد و مسیر | `POST /api/v1/setup/initialize` |
| ۳ | دسترسی | public (تنها راه ساخت owner بدون ورود؛ یک‌بارمصرف) |
| ۴ | احراز هویت | — (قبل از setup) |
| ۹ | بدنه | `{"admin_email","admin_name","site_name","product_name","product_slug","product_type"}` |
| ۱۰ | الزامی | همهٔ شش فیلد |
| ۱۱ | فیلدها | `admin_email` (email، lowercase می‌شود)، `admin_name`، `site_name`، `product_name` (اعتبارسنجی نام)، `product_slug` (lowercase + اعتبارسنجی slug)، `product_type` ∈ `saas`/`desktop`/`hybrid` |
| ۱۲ | نمونه | `curl -X POST .../setup/initialize -H 'Content-Type: application/json' -d '{"admin_email":"owner@example.com","admin_name":"Owner","site_name":"Acme","product_name":"App","product_slug":"app","product_type":"desktop"}'` |
| ۱۳ | پاسخ موفق | `{"user":{...},"product":{...},"plan":{...}}` |
| ۱۴ | وضعیت | **۲۰۱** |
| ۱۵ | خطاها | **۴۰۳ `SETUP_EMAIL_NOT_ALLOWED`** (ایمیل در `ADMIN_EMAILS` نیست)، ۴۰۹ `SETUP_COMPLETE`، ۴۰۹ `SETUP_IN_PROGRESS`، ۴۰۰ `BAD_REQUEST` (`invalid request: …`/`product_type must be one of…`)، ۵۰۰ `INTERNAL_ERROR` |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"SETUP_COMPLETE","message":"setup already complete"}}` |
| ۱۷ | اثر | یک تراکنش: کاربر `owner`، تنظیم `site_name`، محصول، پلن پیش‌فرض `Pro` (`checkout_id=ShortID()`)، `setup_complete='true'`؛ قفل `pg_try_advisory_lock(8675309)` |
| ۱۸ | امنیت | **عمداً API Key نمی‌سازد**؛ فقط وقتی هیچ ownerی نیست کار می‌کند. اگر `ADMIN_EMAILS` خالی نباشد، فقط ایمیل‌های همان فهرست می‌توانند owner شوند (جلوگیری از ربودن نصب تازه). بررسی بعد از اعتبارسنجی همهٔ فیلدها انجام می‌شود تا نشت اطلاعات رخ ندهد |
| ۱۹ | تست؟ | **بله** — `TestSetupAnswersStoredRows` |

### ۲.۴ `GET /api/v1/config`
| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | تنظیمات عمومی سایت برای صفحهٔ ورود/برندینگ |
| ۲ | متد و مسیر | `GET /api/v1/config` |
| ۳ | دسترسی | public |
| ۱۱ | فیلدها | تنظیمات عمومی + `base_url` + `attribution_text` + `attribution_url` |
| ۱۳ | پاسخ موفق | `{"site_name":"Acme","base_url":"https://...","attribution_text":"Powered by Keygate","attribution_url":"https://keygate.app"}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | — |
| ۱۷ | اثر | فقط خواندن |
| ۱۸ | امنیت | `attribution_text`/`attribution_url` الزام مجوز AGPL v3 §۷(b) — حذف نکنید |
| ۱۹ | تست؟ | ندارد |

### ۲.۵ `GET /api/v1/license/pubkey`
| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | کلید عمومی Ed25519 برای بررسی توکن آفلاین |
| ۲ | متد و مسیر | `GET /api/v1/license/pubkey` |
| ۳ | دسترسی | public |
| ۸ | هدرها | پاسخ `Cache-Control: public, max-age=3600` |
| ۱۱ | فیلدها | `algorithm` (`ed25519`)، `kid`، `public_key` (hex)، `format` (`hex`)، و آرایهٔ `keys[]` با `{kid, algorithm, format, public_key, status}` |
| ۱۳ | پاسخ موفق | `{"algorithm":"ed25519","format":"hex","kid":"key_...","public_key":"1f2e...","keys":[{"kid":"key_...","algorithm":"ed25519","format":"hex","public_key":"1f2e...","status":"active"}]}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | — |
| ۱۸ | امنیت | کلید **عمومی** است؛ امن نیست ولی محرمانه نیست. rotation آن، کلاینت‌ها را وادار به دوباره‌گرفتن می‌کند |
| ۱۹ | تست؟ | **تست مستقیمی یافت نشد** (تابع `SigningPublicKey` توسط `token_test.go` پوشش داده می‌شود) |

---

## §۳. SDK عمومی لایسنس (`/license/*`) (۱۰)

گروه `lic` با middleware `LicenseBruteForceGuard` و `RateLimitByIPScoped("license", max(RateLimitAPI*2,120), 1m)`.
`license_key` در بدنه = اعتبارنامه. روی `activate`, `usage`, `floating/checkout` middleware
`Idempotency` اعمال می‌شود.

### ۳.۱ `POST /api/v1/license/activate`
| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | فعال‌سازی لایسنس روی یک دستگاه/کاربر |
| ۲ | متد و مسیر | `POST /api/v1/license/activate` |
| ۳ | دسترسی | public |
| ۴ | احراز هویت | `license_key` (بدنه) |
| ۵ | scope | — |
| ۶ | path | — |
| ۷ | query | — |
| ۸ | هدرها | `Idempotency-Key` (اختیاری) |
| ۹ | بدنه | `{"license_key","identifier","identifier_type","label","previous_identifier"}` |
| ۱۰ | الزامی | `license_key`, `identifier` |
| ۱۱ | فیلدها | `identifier` رشته (≤۲۵۶، بدون فاصلهٔ ابتدا/انتها)، `identifier_type` ∈ `device`(پیش‌فرض)/`user`، `label` رشته، `previous_identifier` اختیاری برای مهاجرت بی‌درنگ شناسه |
| ۱۲ | نمونه | `curl -X POST .../license/activate -H 'Content-Type: application/json' -d '{"license_key":"KG-XXXX-XXXX-XXXX-XXXX","identifier":"kgid:v1:windows:<64hex>","previous_identifier":"550e8400-e29b-41d4-a716-446655440000","label":"Office PC"}'` |
| ۱۳ | پاسخ موفق | `{"status":"activated","license_id":"...","token":"<b64>.<b64>","meta":{"server":"Keygate","url":"https://keygate.app"}}` (یا `status":"already_activated"`) |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۰ `BAD_REQUEST` (شناسهٔ نامعتبر/طولانی، `identifier_type` نامعتبر)، ۴۰۰ `IDENTIFIER_SCHEME_UNSUPPORTED` (حالت strict و شناسهٔ `uuid`/`opaque` برای binding جدید)، ۴۰۴ `LICENSE_NOT_FOUND`، ۴۰۴ `FEATURE_NOT_AVAILABLE`، **۴۰۹ `DEVICE_CHANGED`** (حالت strict: دستگاه/مشخصات متفاوت یا `previous_identifier`)، **۴۰۹ `ACTIVATION_LIMIT`** (فقط حالت tolerant؛ با `details.max`/`details.current`)، ۴۲۹ `LOCKED_OUT` (با `details.retry_after` و هدر `Retry-After`)، ۴۲۲ `IDEMPOTENCY_KEY_CONFLICT`، ۴۰۹ `IDEMPOTENCY_IN_FLIGHT`، ۵۰۰ `INTERNAL_ERROR` |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"ACTIVATION_LIMIT","message":"maximum activations reached","details":{"max":3,"current":3}}}` |
| ۱۷ | اثر | درج در `activations` با `scheme` تشخیص‌داده‌شده (اتمیک تا سقف `plan.max_activations`)؛ audit `license/activated`؛ webhook `license.activated`. در حالت پیش‌فرض strict، لایسنس فقط **یک** دستگاه فعال دارد؛ شناسهٔ متفاوت `DEVICE_CHANGED` می‌گیرد و `previous_identifier` پذیرفته نمی‌شود. در حالت tolerant، `previous_identifier` رکورد فعال را درجا منتقل می‌کند (بدون مصرف سهم اضافه) و audit `migrated` ثبت می‌شود |
| ۱۸ | امنیت | عبور از سقف‌ها اتمیک است؛ `Retry-After` برای backoff؛ در حالت strict مهاجرت شناسه غیرفعال و آزادسازی سهم فقط با ادمین است (`reset-device-binding`). در حالت tolerant، مهاجرت با دانستن **کلید لایسنس + شناسهٔ قبلی** ممکن است (در `security-review.md` به‌عنوان F-18 ثبت شده: مجاز دانستن، نه اثبات‌شده) |
| ۱۹ | تست؟ | **بله** — `internal/handler/license_activation_test.go` (شامل migration tolerant و شش تست strict binding) و `internal/service/strict_binding_test.go` |
| ۲۰ | یادداشت | تکرار همان دستگاه → `already_activated` (نه خطا) |

### ۳.۲ `POST /api/v1/license/verify`
| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | اعتبارسنجی لایسنس + دریافت توکن آفلاین |
| ۲ | متد و مسیر | `POST /api/v1/license/verify` |
| ۳ | دسترسی | public |
| ۴ | احراز هویت | `license_key` |
| ۸ | هدرها | — |
| ۹ | بدنه | `{"license_key","identifier"}` |
| ۱۰ | الزامی | هر دو |
| ۱۱ | فیلدها | `identifier` ≤۲۵۶ |
| ۱۲ | نمونه | `curl -X POST .../license/verify -d '{"license_key":"KG-...","identifier":"kgid:v1:windows:5581b5a7b290e096fdea9b19916561e13d15fc983a9b57c53a9e5ad91f29f4b7"}' -H 'Content-Type: application/json'` |
| ۱۳ | پاسخ موفق | `{"status","plan_id","plan_name","valid_until?","updates_until?","features":{...},"token":"<b64>.<b64>","grace_days":7,"meta":{...},"external_customer_id?","external_workspace_id?"}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۰ `BAD_REQUEST`، ۴۰۴ `LICENSE_NOT_FOUND` (برای همهٔ حالت‌های غیرقابل‌استفاده/دستگاه فعال‌نشده **و همچنین شناسهٔ تغییرکرده در حالت strict**)، ۴۰۴ `FEATURE_NOT_AVAILABLE`، ۴۲۹ `LOCKED_OUT`، ۵۰۰ `INTERNAL_ERROR` |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"LICENSE_NOT_FOUND","message":"license not found"}}` |
| ۱۷ | اثر | `TouchActivation`؛ audit ندارد؛ webhook ندارد |
| ۱۸ | امنیت | عمداً **همهٔ** شرایط منفی (منقضی/معلق/لغو/دستگاه نامعتبر) را به یک ۴۰۴ تبدیل می‌کند تا oracle نباشد؛ در حالت strict، identifier باید **دقیقاً** با رکورد فعال برابر باشد |
| ۱۹ | تست؟ | **بله** — `internal/service/license_test.go`، `license_features_integration_test.go` |
| ۲۰ | یادداشت | توکن ویژگی‌های امضاشده (`ftr`) و `did`/`fpr` را حمل می‌کند؛ کلاینت باید `did` را با شناسهٔ فعلی دستگاه مقایسه کند |

### ۳.۳ `POST /api/v1/license/deactivate`
| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | آزادکردن سهم فعال‌سازی یک دستگاه |
| ۲ | متد و مسیر | `POST /api/v1/license/deactivate` |
| ۳ | دسترسی | public |
| ۹ | بدنه | `{"license_key","identifier"}` |
| ۱۰ | الزامی | هر دو |
| ۱۲ | نمونه | `curl -X POST .../license/deactivate -d '{"license_key":"KG-...","identifier":"kgid:v1:windows:5581b5a7b290e096fdea9b19916561e13d15fc983a9b57c53a9e5ad91f29f4b7"}'` |
| ۱۳ | پاسخ موفق | `{"status":"deactivated"}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۰ `BAD_REQUEST`، ۴۰۴ `LICENSE_NOT_FOUND` (شامل دستگاه ناموجود)، ۴۰۴ `FEATURE_NOT_AVAILABLE`، **۴۰۳ `DEACTIVATION_DISABLED`** (حالت strict؛ فقط ادمین می‌تواند binding را آزاد کند)، ۴۲۹ `LOCKED_OUT`، ۵۰۰ `INTERNAL_ERROR` |
| ۱۷ | اثر | **حذف نرم**: `status='deactivated'`، `deactivated_reason='client'`، `deactivated_at=now()`؛ audit `license/deactivated`؛ webhook `license.deactivated`. در حالت strict انجام نمی‌شود |
| ۱۸ | امنیت | «دستگاه ناموجود» هم به ۴۰۴ تبدیل می‌شود (ضد-oracle)؛ در حالت strict، مسیر آزادسازی سهم بسته است تا rebind با کلید لایسنس ممکن نباشد |
| ۱۹ | تست؟ | غیرمستقیم — `internal/store/activation_race_test.go` |

### ۳.۴ `POST /api/v1/license/entitlements`
| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | بررسی قابلیت‌های لایسنس |
| ۲ | متد و مسیر | `POST /api/v1/license/entitlements` |
| ۳ | دسترسی | public |
| ۹ | بدنه | `{"license_key","feature"}` |
| ۱۰ | الزامی | `license_key` |
| ۱۱ | فیلدها | `feature` اختیاری (اگر بیاید فقط همان بررسی می‌شود) |
| ۱۲ | نمونه | `curl -X POST .../license/entitlements -d '{"license_key":"KG-...","feature":"export"}'` |
| ۱۳ | پاسخ موفق | `{"licensed":true,"status":"active","plan_id":"...","plan_name":"Pro","features":{"export":{"enabled":true,"value_type":"bool","value":"true"},"seats":{"enabled":true,"value_type":"quota","value":"5","used":2,"limit":5,"remaining":3}}}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۰ `BAD_REQUEST`، ۴۰۴ `LICENSE_NOT_FOUND`، ۵۰۰ `INTERNAL_ERROR` |
| ۱۷ | اثر | فقط خواندن (`FeatureStatus`: `enabled`,`value_type`,`value`,`used?`,`limit?`,`remaining?`) |
| ۱۸ | امنیت | از `loadLicenseForSDK` استفاده می‌کند (ضد-oracle) |
| ۱۹ | تست؟ | **بله** — `internal/service/license_test.go` (`TestEntitlements`) |

### ۳.۵ `POST /api/v1/license/usage`
| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | ثبت مصرف یک feature |
| ۲ | متد و مسیر | `POST /api/v1/license/usage` |
| ۳ | دسترسی | public |
| ۸ | هدرها | `Idempotency-Key` (اختیاری) |
| ۹ | بدنه | `{"license_key","feature","quantity","metadata"}` |
| ۱۰ | الزامی | `license_key`, `feature` |
| ۱۱ | فیلدها | `quantity` int64 (۰ = یک واحد؛ منفی خطاست)، `metadata` map آزاد |
| ۱۲ | نمونه | `curl -X POST .../license/usage -d '{"license_key":"KG-...","feature":"api_calls","quantity":1}'` |
| ۱۳ | پاسخ موفق | `{"accepted":true,"used":101,"limit":1000,"remaining":899,"period":"monthly","period_key":"2026-10"}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۰ `BAD_REQUEST` (شامل `quantity cannot be negative`)، ۴۰۴ `LICENSE_NOT_FOUND`، ۴۰۹ `QUOTA_EXCEEDED` (در صورت عبور از سهمیه)، ۴۲۲ `IDEMPOTENCY_KEY_CONFLICT`، ۴۰۹ `IDEMPOTENCY_IN_FLIGHT`، ۵۰۰ `INTERNAL_ERROR` |
| ۱۷ | اثر | درج `usage_events` + افزایش `usage_counters`؛ احتمالی webhook `quota.warning`/`quota.exceeded`؛ احتمالی صف رویداد Metered Stripe |
| ۱۸ | امنیت | `Idempotency-Key` برای جلوگیری از دوباره‌شماری |
| ۱۹ | تست؟ | **بله** — `license_features_integration_test.go`، `metered_sync_test.go` |
| ۲۰ | یادداشت | تنها نوشتنِ سهمیه از سمت SDK |

### ۳.۶ `POST /api/v1/license/usage/status`
| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | وضعیت سهمیهٔ یک feature |
| ۲ | متد و مسیر | `POST /api/v1/license/usage/status` |
| ۳ | دسترسی | public |
| ۹ | بدنه | `{"license_key","feature"}` |
| ۱۰ | الزامی | هر دو |
| ۱۳ | پاسخ موفق | `{"used":2,"limit":5,"remaining":3,"period":"monthly","period_key":"2026-10"}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۰ `BAD_REQUEST`، ۴۰۴ `LICENSE_NOT_FOUND`، ۵۰۰ `INTERNAL_ERROR` |
| ۱۷ | اثر | فقط خواندن |
| ۱۹ | تست؟ | **بله** — `license_features_integration_test.go` |

### ۳.۷ `POST /api/v1/license/floating/checkout`
| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | گرفتن یک جلسهٔ شناور (لیز) از لایسنس |
| ۲ | متد و مسیر | `POST /api/v1/license/floating/checkout` |
| ۳ | دسترسی | public |
| ۸ | هدرها | `Idempotency-Key` (اختیاری) |
| ۹ | بدنه | `{"license_key","identifier","label"}` |
| ۱۰ | الزامی | `license_key`, `identifier` |
| ۱۳ | پاسخ موفق | `{"session_id":"...","expires_at":"2026-10-19T12:00:00Z","active_sessions":1,"max_sessions":5}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۰ `BAD_REQUEST`، ۴۰۴ `LICENSE_NOT_FOUND` (همهٔ حالات «کمک نمی‌کنم» جمع می‌شوند، شامل پلن غیرشناور)، ۴۲۹ `LOCKED_OUT`، ۴۲۲ `IDEMPOTENCY_KEY_CONFLICT`، ۴۰۹ `IDEMPOTENCY_IN_FLIGHT` |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"LICENSE_NOT_FOUND","message":"license not found"}}` |
| ۱۷ | اثر | درج/تمدید `floating_sessions` تا سقف |
| ۱۸ | امنیت | فقط برای پلن‌های `license_model=floating` |
| ۱۹ | تست؟ | **بله** — `internal/store/floating_lease_test.go`، `integration_test.go` (`TestFloatingCheckOutWithLimit_Atomic`) |

### ۳.۸ `POST /api/v1/license/floating/checkin`
| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | آزادکردن جلسهٔ شناور |
| ۲ | متد و مسیر | `POST /api/v1/license/floating/checkin` |
| ۹ | بدنه | `{"license_key","identifier"}` |
| ۱۰ | الزامی | هر دو |
| ۱۳ | پاسخ موفق | `{"status":"checked_in"}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۰ `BAD_REQUEST`، ۴۰۴ `LICENSE_NOT_FOUND` |
| ۱۷ | اثر | حذف/آزادسازی رکورد `floating_sessions` |
| ۱۹ | تست؟ | **بله** — `floating_lease_test.go` |

### ۳.۹ `POST /api/v1/license/floating/heartbeat`
| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | تمدید جلسهٔ شناور |
| ۲ | متد و مسیر | `POST /api/v1/license/floating/heartbeat` |
| ۹ | بدنه | `{"license_key","identifier"}` |
| ۱۰ | الزامی | هر دو |
| ۱۳ | پاسخ موفق | `{"status":"ok","expires_at":"...","remaining_seconds":540,"active_sessions":1,"max_sessions":5}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۰ `BAD_REQUEST`، ۴۰۴ `LICENSE_NOT_FOUND` |
| ۱۷ | اثر | به‌روزرسانی `expires_at`/`heartbeat` |
| ۱۹ | تست؟ | **بله** — `floating_lease_test.go` |

### ۳.۱۰ `POST /api/v1/license/download`
| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | دریافت URL امضاشدهٔ دانلود نسخه (محدود به لایسنس) |
| ۲ | متد و مسیر | `POST /api/v1/license/download` |
| ۳ | دسترسی | public |
| ۹ | بدنه | `{"license_key","platform","version","channel"}` |
| ۱۰ | الزامی | `license_key`, `platform` |
| ۱۱ | فیلدها | `platform` ∈ مجموعهٔ مجاز (مثل `darwin-arm64`,`windows-x64`)، `version` خالی = آخرین، `channel` ∈ `stable`/`beta`/`alpha`/`dev` (پیش‌فرض `stable`) |
| ۱۲ | نمونه | `curl -X POST .../license/download -d '{"license_key":"KG-...","platform":"windows-x64"}'` |
| ۱۳ | پاسخ موفق | `{"url":"<presigned>","expires_at":"...","version":"1.2.0","platform":"windows-x64","sha256":"...","file_size":12345678}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۰ `INVALID_PLATFORM`، ۴۰۰ `INVALID_CHANNEL`، ۴۰۴ `LICENSE_NOT_FOUND`، ۴۱۰ `RELEASE_YANKED`، ۴۰۴ `RELEASE_NOT_FOUND`، ۴۰۳ `UPDATES_EXPIRED`، ۴۰۴ `PLATFORM_NOT_AVAILABLE`، ۵۰۳ `STORAGE_DISABLED` |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"UPDATES_EXPIRED","message":"release 2.0.0 was published after this license's update period ended on 2026-01-01; renew to install it"}}` |
| ۱۷ | اثر | فقط خواندن + ساخت presigned GET |
| ۱۸ | امنیت | نگه‌داری دورهٔ نگهداری (`updates_until`) |
| ۱۹ | تست؟ | **بله** — `release_download_gate_test.go` (`GenerateDownload`)، `release_test.go` (`DownloadFilename`) |

---

## §۴. دعوت‌نامه (۱)

### ۴.۱ `POST /api/v1/invites/accept`
| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | پذیرش دعوت صندلی با توکن ایمیلی |
| ۲ | متد و مسیر | `POST /api/v1/invites/accept` |
| ۳ | دسترسی | public (توکن = اثبات مالکیت ایمیل) |
| ۶ | path | — | 
| ۷ | query | — |
| ۹ | بدنه | `{"token"}` |
| ۱۰ | الزامی | `token` |
| ۱۲ | نمونه | `curl -X POST .../invites/accept -d '{"token":"<plain-token>"}'` |
| ۱۳ | پاسخ موفق | `service.AcceptSeatInviteResult`؛ در صورت موفقیت کوکی‌های `session` و `refresh_token` هم ست می‌شوند |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۰ `BAD_REQUEST` (`token is required`) و خطاهای ارائه‌شده از `writeAppErr` (مثل توکن منقضی) |
| ۱۷ | اثر | اتصال/ساخت کاربر و پذیرش صندلی؛ ست‌کردن نشست |
| ۱۸ | امنیت | ایمیل از توکن خوانده می‌شود، نه از بدنه؛ rate-limited `invites` |
| ۱۹ | تست؟ | **تست مستقیمی برای handler یافت نشد** |

## §۵. فیدهای انتشار و کشف کلید (۹)

گروه فید با `RateLimitByIPScoped("feed", max(RateLimitAPI*4,240), 1m)`. همهٔ کانال‌ها عمومی‌اند؛
امنیت از امضای Ed25519 artifact می‌آید، نه پنهان‌کاری URL. هدر پاسخ همیشه `Vary: X-License-Key, X-License-Token`.

### ۵.۱ `GET /api/v1/releases/:product_slug/feed.xml` (Sparkle)
| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | فید Sparkle (appcast) برای macOS |
| ۲ | متد و مسیر | `GET /api/v1/releases/:product_slug/feed.xml` |
| ۳ | دسترسی | public (اگر `feed_license_required` روشن باشد، نیازمند لایسنس) |
| ۶ | path | `:product_slug` |
| ۷ | query | `platform` (الزامی)، `channel` (پیش‌فرض `stable`)، `limit` (۱–۱۰۰، پیش‌فرض ۲۰)، `license_token` |
| ۸ | هدرها | `X-License-Key` یا `X-License-Token` (جایگزین query) |
| ۱۳ | پاسخ موفق | XML (RSS appcast)؛ هر item فیلدهای `sparkle:version`, `sparkle:shortVersionString`, `enclosure[url,length,type,sparkle:edSignature]` و `sparkle:criticalUpdate` (در صورت کف نسخه) |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۰ (`platform is required`/نامعتبر، `channel must be…`، `limit must be an integer between 1 and 100`، ارسال key در URL)، ۴۰۴ (`product not found`، توکن/کلید بد)، ۴۰۱ `LICENSE_KEY_REQUIRED` |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"LICENSE_KEY_REQUIRED","message":"this product's update feed requires the license: ..."}}` |
| ۱۷ | اثر | فقط خواندن + ساخت presigned GET برای هر artifact |
| ۱۸ | امنیت | کلید لایسنس فقط از هدر؛ در URL پذیرفته نمی‌شود |
| ۱۹ | تست؟ | **بله** — `release_feed_test.go` (`TestRenderSparkle*`, `TestSanitizeCDATA*`) |

### ۵.۲ `GET /api/v1/releases/:product_slug/feed.json` (Velopack)
| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | فید Velopack (کلاینت .NET) |
| ۲ | متد و مسیر | `GET /api/v1/releases/:product_slug/feed.json` |
| ۶–۸ | path/query/هدر | مثل ۵.۱ |
| ۱۳ | پاسخ موفق | JSON (`{Assets:[...]}`)؛ امنیت بر پایهٔ `SHA-256` + HTTPS (Velopack امضا را بررسی نمی‌کند) |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | همان ۵.۱ |
| ۱۹ | تست؟ | **بله** — `release_feed_test.go` (`TestBuildVelopack*`, `TestVelopackFileNameRoundTrip`) |

### ۵.۳ `GET /api/v1/releases/:product_slug/velopack/*path`
| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | مسیر سازگاری برای دریافت بسته/ایندکس Velopack |
| ۲ | متد و مسیر | `GET /api/v1/releases/:product_slug/velopack/*path` |
| ۶ | path | `:product_slug` و `*path` (wildcard) |
| ۱۳ | پاسخ موفق | بستهٔ Velopack یا ریدایرکت (۳۰۲) به URL امضاشده |
| ۱۴ | وضعیت | ۲۰۰/۳۰۲ |
| ۱۹ | تست؟ | **بله** — `release_velopack_test.go` (`TestFeedVelopackIndexPaths`) |
| ۲۰ | یادداشت | مسیر wildcard؛ اعتبارسنجی مسیر در تست پوشش دارد |

### ۵.۴ `GET /api/v1/releases/:product_slug/upgrade.json` (Tauri)
| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | مانیفست به‌روزرسانی Tauri |
| ۲ | متد و مسیر | `GET /api/v1/releases/:product_slug/upgrade.json` |
| ۶–۸ | path/query/هدر | مثل ۵.۱ |
| ۱۳ | پاسخ موفق | JSON با `version`, `pub_date`, `url`, `signature` (minisign)، و فیلدهای کف نسخه (`minimum_supported_version`/پیام) |
| ۱۴ | وضعیت | ۲۰۰؛ اگر نسخه‌ای نباشد ممکن است ۲۰۴ باشد |
| ۱۵ | خطاها | همان ۵.۱ |
| ۱۹ | تست؟ | **بله** — `release_feed_test.go` (`TestBuildTauri*`)، `tauri_minisign_test.go` |

### ۵.۴.۱ `GET /api/v1/releases/:product_slug/signing-key`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | کشف کلیدهای عمومی امضای محصول، تا کلاینت بتواند صحت آرتیفکت‌ها و فیدها را بدون تنظیم دستی کلید بررسی کند |
| ۲ | متد و مسیر | `GET /api/v1/releases/:product_slug/signing-key` |
| ۳ | دسترسی | public (در صورت فعال‌بودن گیت فید، همان قواعد فید) |
| ۴ | احراز هویت | — (بدون کلید لایسنس) |
| ۵ | scope | — |
| ۶ | path | `:product_slug` = اسلاگ محصول (case-insensitive) |
| ۷ | query | — |
| ۸ | هدرها | `If-None-Match` پشتیبانی نمی‌شود؛ پاسخ `Cache-Control: public, max-age=300` دارد |
| ۹ | بدنه | — |
| ۱۰ | الزامی | `:product_slug` |
| ۱۱ | فیلدها | `kid` شناسهٔ کلید، `algorithm` همیشه `ed25519`، `public_key_pem`، `public_key_b64`، `tauri_pubkey` (برای کلیدهای قدیمی/غیرقابل‌تبدیل حذف می‌شود)، `status` ∈ `active`/`retired` |
| ۱۲ | نمونه | `curl -s http://localhost:9000/api/v1/releases/<slug>/signing-key` |
| ۱۳ | پاسخ موفق | `{"keys":[{"kid":"...","algorithm":"ed25519","public_key_pem":"-----BEGIN ED25519 PUBLIC KEY-----...","public_key_b64":"...","tauri_pubkey":"...","status":"active"}]}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۰ (اسلاگ خالی)، ۴۰۴ `NOT_FOUND` (محصول نیست / نوع محصول قابلیت `releases` ندارد / هیچ کلیدی ثبت نشده)، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"NOT_FOUND","message":"no signing keys found for this product"}}` |
| ۱۷ | اثر | فقط خواندن |
| ۱۸ | امنیت | فقط کلید **عمومی** برمی‌گردد؛ کلید خصوصی هرگز از سرور خارج نمی‌شود. کلیدهای `retired` هم برای بررسی آرتیفکت‌های قدیمی برگردانده می‌شوند. |
| ۱۹ | تست؟ | ندارد (منطق store در `release_signing_rotate_test.go` پوشش دارد) |
| ۲۰ | یادداشت | این route جایگزین کوپی‌کردن دستی کلید عمومی در کلاینت است؛ برای فیدهای امضاشده و مانیفست یکپارچگی (`keygate-integrity-v1`) استفاده می‌شود. |

### ۵.۵ `GET /api/v1/releases/feed.xml`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | مسیر قدیمی حذف‌شده (همیشه ۴۱۰) |
| ۲ | متد و مسیر | `GET /api/v1/releases/feed.xml` |
| ۳ | دسترسی | public |
| ۴ | احراز هویت | — |
| ۵ | scope | — |
| ۶ | path | — |
| ۷ | query | — |
| ۸ | هدرها | — |
| ۹ | بدنه | — |
| ۱۰ | الزامی | — |
| ۱۱ | فیلدها | — |
| ۱۲ | نمونه | `curl -i http://localhost:9000/api/v1/releases/feed.xml` |
| ۱۳ | پاسخ موفق | ندارد — همیشه خطا |
| ۱۴ | وضعیت | **۴۱۰ Gone** |
| ۱۵ | خطاها | ۴۱۰ `FEED_PATH_REMOVED` |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"FEED_PATH_REMOVED","message":"feed URL changed: use /api/v1/releases/{product_slug}/feed.xml|feed.json|upgrade.json. Feeds are public; trust is established via the artifact's ed25519 signature."}}` |
| ۱۷ | اثر | هیچ |
| ۱۸ | امنیت | — |
| ۱۹ | تست؟ | **تست مستقیمی یافت نشد** |
| ۲۰ | یادداشت | برای مسیر جدید از §۵.۱ استفاده کنید |

### ۵.۶ `GET /api/v1/releases/feed.json`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | مسیر قدیمی حذف‌شده (همیشه ۴۱۰) |
| ۲ | متد و مسیر | `GET /api/v1/releases/feed.json` |
| ۳ | دسترسی | public |
| ۴ | احراز هویت | — |
| ۵ | scope | — |
| ۶ | path | — |
| ۷ | query | — |
| ۸ | هدرها | — |
| ۹ | بدنه | — |
| ۱۰ | الزامی | — |
| ۱۱ | فیلدها | — |
| ۱۲ | نمونه | `curl -i http://localhost:9000/api/v1/releases/feed.json` |
| ۱۳ | پاسخ موفق | ندارد — همیشه خطا |
| ۱۴ | وضعیت | **۴۱۰ Gone** |
| ۱۵ | خطاها | ۴۱۰ `FEED_PATH_REMOVED` |
| ۱۶ | نمونهٔ خطا | همان پیام §۵.۵ |
| ۱۷ | اثر | هیچ |
| ۱۸ | امنیت | — |
| ۱۹ | تست؟ | **تست مستقیمی یافت نشد** |
| ۲۰ | یادداشت | برای مسیر جدید از §۵.۲ استفاده کنید |

### ۵.۷ `GET /api/v1/releases/upgrade.json`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | مسیر قدیمی حذف‌شده (همیشه ۴۱۰) |
| ۲ | متد و مسیر | `GET /api/v1/releases/upgrade.json` |
| ۳ | دسترسی | public |
| ۴ | احراز هویت | — |
| ۵ | scope | — |
| ۶ | path | — |
| ۷ | query | — |
| ۸ | هدرها | — |
| ۹ | بدنه | — |
| ۱۰ | الزامی | — |
| ۱۱ | فیلدها | — |
| ۱۲ | نمونه | `curl -i http://localhost:9000/api/v1/releases/upgrade.json` |
| ۱۳ | پاسخ موفق | ندارد — همیشه خطا |
| ۱۴ | وضعیت | **۴۱۰ Gone** |
| ۱۵ | خطاها | ۴۱۰ `FEED_PATH_REMOVED` |
| ۱۶ | نمونهٔ خطا | همان پیام §۵.۵ |
| ۱۷ | اثر | هیچ |
| ۱۸ | امنیت | — |
| ۱۹ | تست؟ | **تست مستقیمی یافت نشد** |
| ۲۰ | یادداشت | برای مسیر جدید از §۵.۴ استفاده کنید |

### ۵.۸ `GET /api/v1/releases/feed`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | مسیر قدیمی حذف‌شده (همیشه ۴۱۰) |
| ۲ | متد و مسیر | `GET /api/v1/releases/feed` |
| ۳ | دسترسی | public |
| ۴ | احراز هویت | — |
| ۵ | scope | — |
| ۶ | path | — |
| ۷ | query | — |
| ۸ | هدرها | — |
| ۹ | بدنه | — |
| ۱۰ | الزامی | — |
| ۱۱ | فیلدها | — |
| ۱۲ | نمونه | `curl -i http://localhost:9000/api/v1/releases/feed` |
| ۱۳ | پاسخ موفق | ندارد — همیشه خطا |
| ۱۴ | وضعیت | **۴۱۰ Gone** |
| ۱۵ | خطاها | ۴۱۰ `FEED_PATH_REMOVED` |
| ۱۶ | نمونهٔ خطا | همان پیام §۵.۵ |
| ۱۷ | اثر | هیچ |
| ۱۸ | امنیت | — |
| ۱۹ | تست؟ | **تست مستقیمی یافت نشد** |
| ۲۰ | یادداشت | برای مسیر جدید از §۵.۱–۵.۴ استفاده کنید |

---

## §۶. احراز هویت (۶)

گروه `auth` با `RateLimitByIPScoped("auth", cfg.RateLimitAuth, 1m)`. `/auth/refresh` سبد جدا دارد.

### ۶.۱ `GET /api/v1/auth/providers`
| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | روش‌های ورود فعال |
| ۲ | متد و مسیر | `GET /api/v1/auth/providers` |
| ۳ | دسترسی | public |
| ۱۳ | پاسخ موفق | `{"dev_login":true,"otp":true}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۹ | تست؟ | ندارد |

### ۶.۲ `POST /api/v1/auth/otp/send`
| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | ارسال کد یک‌بارمصرف به ایمیل |
| ۲ | متد و مسیر | `POST /api/v1/auth/otp/send` |
| ۳ | دسترسی | public |
| ۷ | query | — |
| ۸ | هدرها | — |
| ۹ | بدنه | `{"email"}` |
| ۱۰ | الزامی | `email` (باید email معتبر باشد) |
| ۱۲ | نمونه | `curl -X POST .../auth/otp/send -d '{"email":"user@example.com"}'` |
| ۱۳ | پاسخ موفق | `{"status":"sent"}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۰ `BAD_REQUEST` (`valid email is required`)، ۴۲۹ `RATE_LIMITED` (`too many code requests, try again later`؛ ≥۳ درخواست اخیر برای یک ایمیل)، ۵۰۰ `INTERNAL_ERROR` |
| ۱۷ | اثر | درج `otp_codes` (انقضای ۱۰ دقیقه)؛ ارسال ایمیل یا لاگ fallback؛ در `signup_mode=licensed_only` می‌تواند بدون ارسال، hash ثبت کند |
| ۱۸ | امنیت | سبد جداگانهٔ سخت‌گیرانه `otp_send` (پیش‌فرض ۳۰/ساعت) چون به آدرس دلخواه ایمیل می‌زند |
| ۱۹ | تست؟ | ندارد |

### ۶.۳ `POST /api/v1/auth/otp/verify`
| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | بررسی کد و ورود |
| ۲ | متد و مسیر | `POST /api/v1/auth/otp/verify` |
| ۳ | دسترسی | public |
| ۹ | بدنه | `{"email","code"}` |
| ۱۰ | الزامی | هر دو |
| ۱۳ | پاسخ موفق | `{"status":"ok","email":"...","name":"...","is_admin":false,"role":"user"}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۰ `BAD_REQUEST`، ۴۰۱ `UNAUTHORIZED` (`invalid or expired code` / `too many attempts, request a new code`)، ۵۰۰ `INTERNAL_ERROR` |
| ۱۷ | اثر | `IncrementOTPAttempts`، `MarkOTPUsed`، `UpsertUser`، احتمالاً ارتقا به admin اگر در `ADMIN_EMAILS`، `SendWelcome` برای کاربر جدید، audit `session/login` |
| ۱۸ | امنیت | کوکی‌ها `HttpOnly`+`SameSite=Lax`+`Secure` (بسته به scheme) |
| ۱۹ | تست؟ | ندارد (فقط `session_cookie_test.go` برای helper کوکی) |

### ۶.۴ `POST /api/v1/auth/dev-login`
| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | ورود توسعه‌ای بدون ایمیل |
| ۲ | متد و مسیر | `POST /api/v1/auth/dev-login` |
| ۳ | دسترسی | public (فقط در development و با `BASE_URL` شامل localhost/127.0.0.1) |
| ۹ | بدنه | `{"email","name"}` |
| ۱۰ | الزامی | `email` |
| ۱۳ | پاسخ موفق | `{"status":"ok","email","name","is_admin","role"}` + ست‌کردن کوکی‌ها |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۴ `NOT_FOUND` (وقتی مجاز نیست)، ۴۰۰ `BAD_REQUEST` (`email is required`)، ۵۰۰ `INTERNAL_ERROR` |
| ۱۷ | اثر | `UpsertUser`، احتمالاً ارتقا به admin، audit `session/login` (actor_type `dev_login`) |
| ۱۸ | امنیت | در production کاملاً غیرفعال است |
| ۱۹ | تست؟ | ندارد (config آن تست دارد) |

### ۶.۵ `POST /api/v1/auth/logout`
| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | خروج و پاک‌کردن نشست |
| ۲ | متد و مسیر | `POST /api/v1/auth/logout` |
| ۳ | دسترسی | protected (session) |
| ۴ | احراز هویت | `SessionAuth` |
| ۱۳ | پاسخ موفق | `{"status":"logged_out"}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۱ `UNAUTHORIZED` |
| ۱۷ | اثر | پاک‌کردن کوکی‌ها (`session`, `refresh_token` با MaxAge=-1)، `DeleteRefreshToken`، `DeleteUserRefreshTokens`، audit `session/logout` |
| ۱۹ | تست؟ | ندارد |

### ۶.۶ `POST /api/v1/auth/refresh`
| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | تازه‌سازی نشست با کوکی `refresh_token` |
| ۲ | متد و مسیر | `POST /api/v1/auth/refresh` |
| ۳ | دسترسی | public (سبد جدا `auth_refresh`) |
| ۸ | هدرها | کوکی `refresh_token` |
| ۱۳ | پاسخ موفق | `{"status":"refreshed"}` + کوکی `session` جدید و `refresh_token` چرخیده |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۱ `UNAUTHORIZED` (`no refresh token` / `refresh token reuse detected` / `invalid refresh token` / `user not found`)، ۵۰۰ `INTERNAL_ERROR` |
| ۱۷ | اثر | `RenewRefreshToken`؛ در تشخیص reuse، `DeleteUserRefreshTokens` (لغو همهٔ توکن‌های کاربر) |
| ۱۸ | امنیت | چرخش توکن + تشخیص reuse |
| ۱۹ | تست؟ | ندارد |

---

## §۷. کاتالوگ عمومی پلن (۱)

### ۷.۱ `GET /api/v1/products/:product_slug/plans`
| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | لیست پلن‌های فعال یک محصول برای صفحهٔ قیمت‌گذاری |
| ۲ | متد و مسیر | `GET /api/v1/products/:product_slug/plans` |
| ۳ | دسترسی | public (سبد `public_plans`) |
| ۶ | path | `:product_slug` |
| ۱۳ | پاسخ موفق | `{"plans":[{"id","name","slug","license_type","billing_interval","checkout_id","price","currency"}]}` (پلن‌های غیرفعال حذف؛ `price`/`currency` ممکن است `null` باشند) |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۰ `BAD_REQUEST` (`product_slug is required`)، ۴۰۴ `NOT_FOUND` (`product not found`)، ۵۰۰ `INTERNAL_ERROR` |
| ۱۷ | اثر | خواندن + احتمالاً lookup قیمت Stripe (cache ۵ دقیقه / retry ۳۰ ثانیه) |
| ۱۹ | تست؟ | ندارد |

---

## §۸. Stripe (۲)

### ۸.۱ `POST /api/v1/webhook/stripe`
| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | دریافت رویدادهای Stripe |
| ۲ | متد و مسیر | `POST /api/v1/webhook/stripe` |
| ۳ | دسترسی | public (سبد `stripe_webhook`, 60/min) |
| ۸ | هدرها | `Stripe-Signature` |
| ۹ | بدنه | JSON خام رویداد Stripe |
| ۱۳ | پاسخ موفق | `{"received":true}`؛ برای رویداد تکراری `{"received":true,"skipped":true}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۰ `BAD_REQUEST` (`read failed`/`webhook not configured`/`invalid signature`/`livemode mismatch`)، ۴۰۰ `UNSUPPORTED_API_VERSION`، ۵۰۰ `{"received":false,"retry":true}`، ۵۰۳ `{"received":false,"retry":true,"in_progress":true}` |
| ۱۷ | اثر | claimهای idempotency (`stripe`/`stripe_claim`)؛ fulfillment لایسنس برای رویدادهای متعدد (checkout, invoice.paid, subscription.*, charge.refunded, …) |
| ۱۸ | امنیت | بررسی امضای Stripe؛ `livemode` چک می‌شود |
| ۱۹ | تست؟ | **بله** — `internal/payment/fulfill_test.go` (`TestWebhook_APIVersionGate`, …) |

### ۸.۲ `GET /api/v1/checkout/verify`
| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | بررسی نتیجهٔ یک Checkout |
| ۲ | متد و مسیر | `GET /api/v1/checkout/verify` |
| ۳ | دسترسی | public (سبد `checkout_verify`, 60/min) |
| ۷ | query | `session_id` (الزامی) |
| ۱۳ | پاسخ موفق | `{"status":"pending"}` یا `{"status":"pending","email":"..."}` یا `{"status":"ok","email":"..."}` (و برای renewal: `"kind":"renewal"`) |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۰ `BAD_REQUEST` (`session_id is required`/`invalid session`)، ۵۰۳ `FULFILLMENT_RETRY` |
| ۱۷ | اثر | lookup در Stripe؛ احتمالاً fulfillment |
| ۱۹ | تست؟ | ندارد |

## §۹. پورتال مشتری (۱۶)

همه زیر گروه `portal` با `middleware.SessionAuth` (کوکی `session`). دو گارد مشترک:
`portalLicenseGuard` (مالک لایسنس یا دارندهٔ صندلی؛ سقف بدنه ۶۴KiB) و `portalSeatMutationGuard`
(مالک لایسنس یا صندلی **admin پذیرفته‌شده**). در نبود اجازه، پاسخ همان `404 NOT_FOUND`
(`license not found`) است تا oracle نباشد.

### ۹.۱ `GET /api/v1/portal/me`
| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | اطلاعات کاربر جاری |
| ۲ | متد و مسیر | `GET /api/v1/portal/me` |
| ۳ | دسترسی | protected (session) |
| ۱۳ | پاسخ موفق | `{"id","email","name","avatar_url","is_admin","role"}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۱ `UNAUTHORIZED`، ۴۰۴ `NOT_FOUND` (`user not found`) |
| ۱۹ | تست؟ | ندارد |

### ۹.۲ `GET /api/v1/portal/licenses`
| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | لایسنس‌های متعلق به ایمیل کاربر |
| ۲ | متد و مسیر | `GET /api/v1/portal/licenses` |
| ۳ | دسترسی | protected (session) |
| ۸ | هدرها | پاسخ `Cache-Control: no-store` |
| ۱۳ | پاسخ موفق | `{"licenses":[<License + license_key>],"renewals_enabled":<bool>}` (کلید خام عمداً ضمیمه می‌شود چون اعتبارنامهٔ خود مشتری است) |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۱ `UNAUTHORIZED`، ۵۰۰ `INTERNAL_ERROR` |
| ۱۹ | تست؟ | ندارد |

### ۹.۳ `PUT /api/v1/portal/profile`
| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | به‌روزرسانی نام پروفایل |
| ۲ | متد و مسیر | `PUT /api/v1/portal/profile` |
| ۹ | بدنه | `{"name"}` |
| ۱۰ | اختیاری | `name` (پاک‌سازی HTML و trim؛ حداکثر ۱۰۰ کاراکتر) |
| ۱۳ | پاسخ موفق | `{"id","email","name","avatar_url","role"}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۱ `UNAUTHORIZED`، ۴۰۰ `BAD_REQUEST` (`name too long (max 100 characters)`) |
| ۱۷ | اثر | `UpdateUserProfile` |
| ۱۸ | امنیت | `stripHTMLTags` برای جلوگیری از XSS |
| ۱۹ | تست؟ | ندارد |

### ۹.۴ `GET /api/v1/portal/plans`
| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | پلن‌های فعال یک محصول (برای تغییر پلن در پورتال) |
| ۲ | متد و مسیر | `GET /api/v1/portal/plans` |
| ۷ | query | `product_id` (الزامی) |
| ۱۳ | پاسخ موفق | `{"plans":[{"id","name","slug","license_type","billing_interval","stripe_price_id","checkout_id"}]}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۰ `BAD_REQUEST` (`product_id is required`) |
| ۱۹ | تست؟ | ندارد |

### ۹.۵ `POST /api/v1/portal/usage`
| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | ثبت مصرف از سمت مشتری |
| ۲ | متد و مسیر | `POST /api/v1/portal/usage` |
| ۳ | دسترسی | protected (session + `portalLicenseGuard`) |
| ۹ | بدنه | `{"license_key","feature","quantity","metadata"}` |
| ۱۰ | الزامی | `license_key`, `feature` |
| ۱۳ | پاسخ موفق | همان `RecordUsageResult` (مثل ۳.۵) |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۴ `NOT_FOUND` (لایسنس متعلق به کاربر نیست/ناموجود)، ۴۰۰ `BAD_REQUEST`، ۴۱۳ `BODY_TOO_LARGE` |
| ۱۹ | تست؟ | غیرمستقیم (همان service) |

### ۹.۶ `POST /api/v1/portal/usage/status`
| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | وضعیت سهمیه (پورتال) |
| ۲ | متد و مسیر | `POST /api/v1/portal/usage/status` |
| ۳ | دسترسی | protected (session + `portalLicenseGuard`) |
| ۹ | بدنه | `{"license_key","feature"}` |
| ۱۳ | پاسخ موفق | `{"used","limit","remaining","period","period_key"}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۴ `NOT_FOUND`، ۴۱۳ `BODY_TOO_LARGE` |
| ۱۹ | تست؟ | غیرمستقیم |

### ۹.۷ `POST /api/v1/portal/seats`
| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | لیست صندلی‌های یک لایسنس |
| ۲ | متد و مسیر | `POST /api/v1/portal/seats` |
| ۳ | دسترسی | protected (session + `portalLicenseGuard`) |
| ۹ | بدنه | `{"license_key"}` |
| ۱۰ | الزامی | `license_key` |
| ۱۳ | پاسخ موفق | `{"seats":[...]}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۴ `NOT_FOUND`، ۴۱۳ `BODY_TOO_LARGE` |
| ۱۹ | تست؟ | ندارد |

### ۹.۸ `POST /api/v1/portal/seats/add`
| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | افزودن صندلی (دعوت) |
| ۲ | متد و مسیر | `POST /api/v1/portal/seats/add` |
| ۳ | دسترسی | protected (session + `portalSeatMutationGuard`) |
| ۹ | بدنه | `{"license_key","email","role"}` |
| ۱۰ | الزامی | `license_key`, `email` |
| ۱۱ | فیلدها | `role` ∈ `admin`/`member` |
| ۱۳ | پاسخ موفق | `*model.Seat` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۴ `NOT_FOUND` (اجازه یا لایسنس)، ۴۰۰ `BAD_REQUEST`، ۴۱۳ `BODY_TOO_LARGE` |
| ۱۷ | اثر | ساخت صندلی + ارسال دعوت‌نامه، webhook `seat.added` |
| ۱۸ | امنیت | فقط مالک یا صندلی admin **پذیرفته‌شده** |
| ۱۹ | تست؟ | ندارد |

### ۹.۹ `POST /api/v1/portal/seats/remove`
| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | حذف صندلی |
| ۲ | متد و مسیر | `POST /api/v1/portal/seats/remove` |
| ۳ | دسترسی | protected (session + `portalSeatMutationGuard`) |
| ۹ | بدنه | `{"license_key","seat_id"}` |
| ۱۰ | الزامی | هر دو |
| ۱۳ | پاسخ موفق | `{"status":"removed"}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۴ `NOT_FOUND`، ۴۰۰ `BAD_REQUEST`، ۴۱۳ `BODY_TOO_LARGE` |
| ۱۹ | تست؟ | ندارد |

### ۹.۱۰ `GET /api/v1/portal/licenses/:license_key/activations`
| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | لیست فعال‌سازی‌های لایسنس (مدیریت دستگاه گم‌شده) |
| ۲ | متد و مسیر | `GET /api/v1/portal/licenses/:license_key/activations` |
| ۳ | دسترسی | protected (session) |
| ۶ | path | `:license_key` |
| ۱۳ | پاسخ موفق | `{"activations":[{"id","identifier","identifier_type","label?","ip_address?","scheme","status","deactivated_at?","deactivated_reason?","created_at","last_verified"}],"max":<int>}` — فقط ردیف‌های `status="active"` در شمارش `max` اثر دارند |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۰ `BAD_REQUEST` (`license_key is required in the URL path`)، ۴۰۱ `UNAUTHORIZED`، ۴۰۴ `NOT_FOUND` (`license not found`؛ شامل «متعلق به شما نیست»)، ۵۰۰ `INTERNAL_ERROR` |
| ۱۹ | تست؟ | ندارد |

### ۹.۱۱ `DELETE /api/v1/portal/licenses/:license_key/activations/:activation_id`
| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | حذف یک فعال‌سازی |
| ۲ | متد و مسیر | `DELETE /api/v1/portal/licenses/:license_key/activations/:activation_id` |
| ۶ | path | `:license_key`, `:activation_id` |
| ۱۳ | پاسخ موفق | `{"status":"deleted"}` (متن پاسخ از قبل هست، ولی رکورد فیزیکی پاک نمی‌شود) |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۰ `BAD_REQUEST` (`activation_id is required`)، ۴۰۱ `UNAUTHORIZED`، ۴۰۳ `DEACTIVATION_DISABLED` (حالت strict؛ فقط ادمین binding را آزاد می‌کند)، ۴۰۴ `NOT_FOUND` (`license not found`/`activation not found`)، ۵۰۰ `INTERNAL_ERROR` |
| ۱۷ | اثر | **حذف نرم** (`SoftDeactivateActivation` با دلیل `portal`)، audit `activation/deactivated` (actor_type `portal`)، webhook `license.deactivated`. در حالت strict انجام نمی‌شود |
| ۱۹ | تست؟ | **بله** — `TestStrictDeviceBinding_BlocksPortalDelete` |

### ۹.۱۲ `POST /api/v1/portal/subscription/change-plan`
| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | تغییر پلن اشتراک Stripe |
| ۲ | متد و مسیر | `POST /api/v1/portal/subscription/change-plan` |
| ۳ | دسترسی | protected (session) |
| ۹ | بدنه | `{"license_id","new_price_id","prorate"}` |
| ۱۰ | الزامی | `license_id`, `new_price_id` |
| ۱۱ | فیلدها | `prorate` *bool (پیش‌فرض true) |
| ۱۳ | پاسخ موفق | `{"status":"plan_changed","new_plan_id":"...","new_plan_name":"...","proration":"create_prorations"|"none"}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۰ `BAD_REQUEST`، ۴۰۰ `PLAN_INACTIVE` (`target plan is no longer available`)، ۴۰۰ `NOT_SUBSCRIPTION_PLAN`، ۴۰۴ `NOT_FOUND`، ۴۰۳ `FORBIDDEN` (`not your license`)، ۵۰۰ `INTERNAL_ERROR` |
| ۱۷ | اثر | فراخوانی Stripe (`subscription.Get/Update`)؛ `UpdateLicense`؛ audit `license/plan_changed`؛ webhook `plan.changed` |
| ۱۹ | تست؟ | **بله** — `internal/payment/webhook_events_test.go` (`h.ChangePlan(c)`) |

### ۹.۱۳ `POST /api/v1/portal/subscription/cancel`
| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | لغو اشتراک (فوری یا پایان دوره) |
| ۲ | متد و مسیر | `POST /api/v1/portal/subscription/cancel` |
| ۹ | بدنه | `{"license_id","immediate"}` |
| ۱۰ | الزامی | `license_id` |
| ۱۱ | فیلدها | `immediate` bool (false = پایان دوره) |
| ۱۳ | پاسخ موفق | `{"status":"canceled","immediate":<bool>}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۰ `BAD_REQUEST` (`no active subscription`)، ۴۰۴ `NOT_FOUND`، ۴۰۳ `FORBIDDEN`، ۵۰۰ `INTERNAL_ERROR` |
| ۱۷ | اثر | `subscription.Cancel` یا `Update(CancelAtPeriodEnd)`؛ نوشتن وضعیت لایسنس؛ audit `license/cancel_requested`؛ ایمیل `SendSubscriptionCanceled` |
| ۱۹ | تست؟ | ندارد |

### ۹.۱۴ `POST /api/v1/portal/subscription/billing-portal`
| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | ساخت لینک پورتال صورتحساب Stripe |
| ۲ | متد و مسیر | `POST /api/v1/portal/subscription/billing-portal` |
| ۹ | بدنه | `{"license_id","return_url"}` |
| ۱۰ | الزامی | `license_id` |
| ۱۳ | پاسخ موفق | `{"url":"..."}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۰ `BAD_REQUEST` (`no Stripe customer associated`)، ۴۰۴ `NOT_FOUND`، ۴۰۳ `FORBIDDEN` |
| ۱۹ | تست؟ | ندارد |

### ۹.۱۵ `GET /api/v1/portal/subscription/invoices`
| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | لیست فاکتورهای Stripe |
| ۲ | متد و مسیر | `GET /api/v1/portal/subscription/invoices` |
| ۷ | query | `license_id` (الزامی) |
| ۱۳ | پاسخ موفق | `{"invoices":[{"id","number","status","amount_due","amount_paid","currency","created","period_start","period_end","invoice_pdf","hosted_url"}]}` (آرایهٔ خالی اگر مشتری Stripe نباشد) |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۰ `BAD_REQUEST`، ۴۰۴ `NOT_FOUND`، ۴۰۳ `FORBIDDEN` |
| ۱۹ | تست؟ | ندارد |

### ۹.۱۶ `POST /api/v1/portal/updates/renew`
| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | تمدید دورهٔ نگهداری لایسنس دائمی |
| ۲ | متد و مسیر | `POST /api/v1/portal/updates/renew` |
| ۹ | بدنه | `{"license_id"}` |
| ۱۰ | الزامی | `license_id` |
| ۱۳ | پاسخ موفق | `{"url":"<checkout url>"}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۰ `RENEWAL_NOT_AVAILABLE`، ۴۰۰ `LICENSE_NOT_ACTIVE`، ۴۰۴ `NOT_FOUND`، ۴۰۳ `FORBIDDEN`، ۴۰۹ `MAINTENANCE_FEATURES_DISABLED`، ۵۰۳ `RENEWAL_UNAVAILABLE` |
| ۱۷ | اثر | ساخت Checkout Session با متادیتای تمدید |
| ۱۹ | تست؟ | **بله** — `internal/payment/renewal_test.go` (`TestRenewUpdates_Endpoint`) |

## §۱۰. API مدیریتی (۹۶)

سه گروه روی `/admin` که همه `SessionOrAPIKey` + `RateLimitByIPScoped("admin", cfg.RateLimitAdmin, 1m)` دارند:

* گروه `admin` → scope `admin`
* گروه `licWrite` → `admin` یا `licenses:write`
* گروه `relWrite` → `admin` یا `releases:write`

نکته: کلیدهای API محدود‌به‌محصول فقط روی همان `product_id` عمل می‌کنند؛ در غیر این صورت
`403 PRODUCT_SCOPE_MISMATCH`.

### ۱۰.۱ `GET /api/v1/admin/stats` — admin

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | آمار کلی داشبورد مدیریت |
| ۲ | متد و مسیر | `GET /api/v1/admin/stats` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key (`Authorization: Bearer kg_live_…`) |
| ۵ | scope | `admin` |
| ۶ | path | — |
| ۷ | query | — |
| ۸ | هدرها | — |
| ۹ | بدنه | — |
| ۱۰ | الزامی | — |
| ۱۱ | فیلدها | — |
| ۱۲ | نمونه | `curl -s -b cookie.txt http://localhost:9000/api/v1/admin/stats` |
| ۱۳ | پاسخ موفق | `store.Stats`: `total_licenses`، `active_licenses`، `total_activations`، `total_products`، `total_seats`، `total_usage_events`، `total_webhooks`، `by_status`، `recent_licenses[]` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۱ `UNAUTHORIZED`/`INVALID_API_KEY`، ۴۰۳ `FORBIDDEN`/`INSUFFICIENT_SCOPE`، ۴۲۹ `RATE_LIMITED`، ۵۰۰ `INTERNAL_ERROR` |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"INSUFFICIENT_SCOPE","message":"api_key is missing a required scope"}}` |
| ۱۷ | اثر | فقط خواندن (چند کوئری شمارشی) |
| ۱۸ | امنیت | شمارش‌ها به کل نصب مربوط‌اند؛ برای کلید محصول‌محور فیلتر محصول اعمال نمی‌شود |
| ۱۹ | تست؟ | ندارد |
| ۲۰ | یادداشت | `by_status` نقشهٔ `status → count` است |

### ۱۰.۲ Products (۵) — admin

### ۱۰.۲.۱ `GET /api/v1/admin/products`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | لیست محصولات با فیلتر و صفحه‌بندی |
| ۲ | متد و مسیر | `GET /api/v1/admin/products` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` |
| ۶ | path | — |
| ۷ | query | `type` (لیست کاما‌جدا)، `search`، `limit`، `offset` |
| ۸ | هدرها | — |
| ۹ | بدنه | — |
| ۱۰ | الزامی | — |
| ۱۱ | فیلدها | `type` ∈ `desktop\|saas\|hybrid`؛ مقدار نامعتبر → ۴۰۰؛ `search` روی نام/slug؛ `limit` پیش‌فرض ۲۰ |
| ۱۲ | نمونه | `curl -s -b cookie.txt 'http://localhost:9000/api/v1/admin/products?type=desktop&search=acme&limit=20'` |
| ۱۳ | پاسخ موفق | `{"products":[<Product>...],"total":n,"limit":20,"offset":0}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۰ `BAD_REQUEST` (`unknown product type: <t>`)، ۴۰۱، ۴۰۳، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"BAD_REQUEST","message":"unknown product type: mobile"}}` |
| ۱۷ | اثر | فقط خواندن |
| ۱۸ | امنیت | برای کلید محصول‌محور، فیلتر `product_id` به‌صورت خودکار اعمال می‌شود |
| ۱۹ | تست؟ | ندارد |
| ۲۰ | یادداشت | ترتیب پیش‌فرض بر اساس `created_at` نزولی است |

### ۱۰.۲.۲ `GET /api/v1/admin/products/:id`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | دریافت یک محصول |
| ۲ | متد و مسیر | `GET /api/v1/admin/products/:id` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` |
| ۶ | path | `:id` = UUID محصول |
| ۷ | query | — |
| ۸ | هدرها | — |
| ۹ | بدنه | — |
| ۱۰ | الزامی | `:id` |
| ۱۱ | فیلدها | — |
| ۱۲ | نمونه | `curl -s -b cookie.txt http://localhost:9000/api/v1/admin/products/<id>` |
| ۱۳ | پاسخ موفق | `*model.Product`: `id`,`name`,`slug`,`type`,`minimum_supported_version`,`minimum_supported_message`,`download_url`,`require_signing`,`feed_license_required`,`feed_gated_at`,`created_at` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۱، ۴۰۳، ۴۰۴ `NOT_FOUND`، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"NOT_FOUND","message":"product not found"}}` |
| ۱۷ | اثر | فقط خواندن |
| ۱۸ | امنیت | UUID نامعتبر/ناموجود هر دو ۴۰۴ می‌دهند (بدون oracle) |
| ۱۹ | تست؟ | ندارد |
| ۲۰ | یادداشت | — |

### ۱۰.۲.۳ `POST /api/v1/admin/products`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | ساخت محصول جدید |
| ۲ | متد و مسیر | `POST /api/v1/admin/products` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` |
| ۶ | path | — |
| ۷ | query | — |
| ۸ | هدرها | `Content-Type: application/json` |
| ۹ | بدنه | `{"name","slug","type","feed_license_required","download_url"}` |
| ۱۰ | الزامی | `name`, `slug`, `type` |
| ۱۱ | فیلدها | `type` ∈ `desktop\|saas\|hybrid`؛ `feed_license_required` bool (پیش‌فرض false)؛ `download_url` رشته |
| ۱۲ | نمونه | `curl -X POST -b cookie.txt -H 'Content-Type: application/json' -d '{"name":"Acme App","slug":"acme-app","type":"desktop"}' http://localhost:9000/api/v1/admin/products` |
| ۱۳ | پاسخ موفق | `*model.Product` (همان فیلدهای ۱۰.۲.۲) |
| ۱۴ | وضعیت | **۲۰۱** |
| ۱۵ | خطاها | ۴۰۰ `BAD_REQUEST`، ۴۰۱، ۴۰۳، ۴۰۹ `DUPLICATE`، ۴۰۹ `MAINTENANCE_FEATURES_DISABLED`، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"DUPLICATE","message":"slug already exists"}}` |
| ۱۷ | اثر | درج در `products`؛ audit `product/created` |
| ۱۸ | امنیت | `slug` باید یکتا و URL-safe باشد |
| ۱۹ | تست؟ | ندارد |
| ۲۰ | یادداشت | `require_signing` پیش‌فرض true است (در مدل) |

### ۱۰.۲.۴ `PUT /api/v1/admin/products/:id`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | ویرایش محصول |
| ۲ | متد و مسیر | `PUT /api/v1/admin/products/:id` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` |
| ۶ | path | `:id` = UUID محصول |
| ۷ | query | — |
| ۸ | هدرها | `Content-Type: application/json` |
| ۹ | بدنه | همه اختیاری: `name`,`slug`,`type`,`minimum_supported_version`,`minimum_supported_message`,`require_signing`,`feed_license_required`,`download_url` |
| ۱۰ | الزامی | — (حداقل یک فیلد) |
| ۱۱ | فیلدها | `type` تغییرناپذیر در برخی حالت‌ها؛ `feed_license_required` فقط برای محصول با پلن معتبر |
| ۱۲ | نمونه | `curl -X PUT -b cookie.txt -H 'Content-Type: application/json' -d '{"download_url":"https://example.com/acme.dmg"}' http://localhost:9000/api/v1/admin/products/<id>` |
| ۱۳ | پاسخ موفق | `*model.Product` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۰ `BAD_REQUEST`، ۴۰۰ `INCOMPATIBLE_PRODUCT_TYPE`، ۴۰۱، ۴۰۳، ۴۰۴، ۴۰۹ `INCOMPATIBLE_PLANS`/`FEED_NOT_GATED`/`BOUNDED_PLANS_EXIST`/`FEED_CACHE_DRAINING`/`MAINTENANCE_FEATURES_DISABLED`، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"INCOMPATIBLE_PRODUCT_TYPE","message":"product type cannot be changed"}}` |
| ۱۷ | اثر | به‌روزرسانی `products`؛ audit `product/updated` |
| ۱۸ | امنیت | تغییر `require_signing`/`feed_license_required` اثر امنیتی دارد و در audit ثبت می‌شود |
| ۱۹ | تست؟ | **بله** — `product_floor_test.go`, `maintenance_test.go` |
| ۲۰ | یادداشت | برخی تغییرات نیازمند خالی‌بودن پلن‌ها/لایسنس‌هاست |

### ۱۰.۲.۵ `DELETE /api/v1/admin/products/:id`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | حذف محصول |
| ۲ | متد و مسیر | `DELETE /api/v1/admin/products/:id` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` |
| ۶ | path | `:id` = UUID محصول |
| ۷ | query | — |
| ۸ | هدرها | — |
| ۹ | بدنه | — |
| ۱۰ | الزامی | `:id` |
| ۱۱ | فیلدها | — |
| ۱۲ | نمونه | `curl -X DELETE -b cookie.txt http://localhost:9000/api/v1/admin/products/<id>` |
| ۱۳ | پاسخ موفق | بدنه ندارد |
| ۱۴ | وضعیت | **۲۰۴** |
| ۱۵ | خطاها | ۴۰۱، ۴۰۳، ۴۰۴، ۴۰۹ `HAS_LICENSES`/`HAS_RELEASES`/`HAS_PLANS`، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"HAS_LICENSES","message":"product has licenses"}}` |
| ۱۷ | اثر | حذف محصول؛ audit `product/deleted` |
| ۱۸ | امنیت | حذف آبشاری فقط وقتی مجاز است که هیچ لایسنس/ریلیز/پلنی وابسته نباشد |
| ۱۹ | تست؟ | ندارد |
| ۲۰ | یادداشت | برای حذف نرم از `PUT` و `active` استفاده کنید (در صورت وجود) |

### ۱۰.۳ Plans و Entitlements (۸) — admin

### ۱۰.۳.۱ `GET /api/v1/admin/plans`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | لیست پلن‌ها |
| ۲ | متد و مسیر | `GET /api/v1/admin/plans` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` |
| ۶ | path | — |
| ۷ | query | `product_id`، `search`، `limit`، `offset` |
| ۸ | هدرها | — |
| ۹ | بدنه | — |
| ۱۰ | الزامی | — |
| ۱۱ | فیلدها | `product_id` فیلتر اختیاری؛ `search` روی نام/slug |
| ۱۲ | نمونه | `curl -s -b cookie.txt 'http://localhost:9000/api/v1/admin/plans?product_id=<id>'` |
| ۱۳ | پاسخ موفق | `{"plans":[<Plan>...],"total":n,"limit":20,"offset":0}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۱، ۴۰۳، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"UNAUTHORIZED","message":"unauthorized"}}` |
| ۱۷ | اثر | فقط خواندن |
| ۱۸ | امنیت | کلید محصول‌محور فقط پلن‌های محصول خودش را می‌بیند |
| ۱۹ | تست؟ | ندارد |
| ۲۰ | یادداشت | — |

### ۱۰.۳.۲ `GET /api/v1/admin/plans/:id`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | دریافت یک پلن همراه محصول و entitlementها |
| ۲ | متد و مسیر | `GET /api/v1/admin/plans/:id` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` |
| ۶ | path | `:id` = UUID پلن |
| ۷ | query | — |
| ۸ | هدرها | — |
| ۹ | بدنه | — |
| ۱۰ | الزامی | `:id` |
| ۱۱ | فیلدها | — |
| ۱۲ | نمونه | `curl -s -b cookie.txt http://localhost:9000/api/v1/admin/plans/<id>` |
| ۱۳ | پاسخ موفق | `*model.Plan`: `id`,`product_id`,`name`,`slug`,`checkout_id`,`license_type`,`billing_interval`,`max_activations`,`trial_days`,`grace_days`,`stripe_price_id`,`updates_days`,`renewal_days`,`stripe_renewal_price_id`,`license_model`,`floating_timeout`,`token_ttl_days`,`max_seats`,`active`,`sort_order`,`created_at`,`product`,`entitlements` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۱، ۴۰۳، ۴۰۴ `NOT_FOUND`، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"NOT_FOUND","message":"plan not found"}}` |
| ۱۷ | اثر | فقط خواندن |
| ۱۸ | امنیت | — |
| ۱۹ | تست؟ | ندارد |
| ۲۰ | یادداشت | `product` و `entitlements` به‌صورت eager load پر می‌شوند |

### ۱۰.۳.۳ `POST /api/v1/admin/plans`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | ساخت پلن |
| ۲ | متد و مسیر | `POST /api/v1/admin/plans` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` |
| ۶ | path | — |
| ۷ | query | — |
| ۸ | هدرها | `Content-Type: application/json` |
| ۹ | بدنه | `{"product_id","name","slug","license_type","billing_interval","max_activations","max_seats","trial_days","grace_days","stripe_price_id","updates_days","renewal_days","stripe_renewal_price_id","license_model","floating_timeout","token_ttl_days","sort_order"}` |
| ۱۰ | الزامی | `product_id`, `name`, `slug`, `license_type` |
| ۱۱ | فیلدها | `license_type` ∈ `subscription\|perpetual\|trial`؛ `billing_interval` ∈ `""\|month\|year`؛ `license_model` ∈ `standard\|floating\|seat` |
| ۱۲ | نمونه | `curl -X POST -b cookie.txt -H 'Content-Type: application/json' -d '{"product_id":"<id>","name":"Pro","slug":"pro","license_type":"subscription","billing_interval":"month","max_activations":3,"grace_days":7}' http://localhost:9000/api/v1/admin/plans` |
| ۱۳ | پاسخ موفق | `*model.Plan` |
| ۱۴ | وضعیت | **۲۰۱** |
| ۱۵ | خطاها | ۴۰۰ `BAD_REQUEST`، ۴۰۴، ۴۰۰ `INCOMPATIBLE_PRODUCT_TYPE`، ۴۰۹ `STRIPE_PRICE_IN_USE`/`MAINTENANCE_FEATURES_DISABLED`/`FEED_NOT_GATED`/`FEED_CACHE_DRAINING`/`DUPLICATE`، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"DUPLICATE","message":"plan slug already exists for this product"}}` |
| ۱۷ | اثر | درج در `plans`؛ audit `plan/created` |
| ۱۸ | امنیت | `stripe_price_id` نباید بین دو پلن مشترک باشد |
| ۱۹ | تست؟ | **بله** — `maintenance_test.go` |
| ۲۰ | یادداشت | `checkout_id` به‌صورت خودکار ساخته می‌شود |

### ۱۰.۳.۴ `PUT /api/v1/admin/plans/:id`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | ویرایش پلن |
| ۲ | متد و مسیر | `PUT /api/v1/admin/plans/:id` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` |
| ۶ | path | `:id` = UUID پلن |
| ۷ | query | — |
| ۸ | هدرها | `Content-Type: application/json` |
| ۹ | بدنه | همهٔ فیلدهای ۱۰.۳.۳ به‌صورت اختیاری + `active` |
| ۱۰ | الزامی | — |
| ۱۱ | فیلدها | فیلدهای pointer هستند؛ فقط موارد ارسال‌شده تغییر می‌کنند |
| ۱۲ | نمونه | `curl -X PUT -b cookie.txt -H 'Content-Type: application/json' -d '{"active":false}' http://localhost:9000/api/v1/admin/plans/<id>` |
| ۱۳ | پاسخ موفق | `*model.Plan` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۰ `BAD_REQUEST`، ۴۰۰ `INCOMPATIBLE_PRODUCT_TYPE`، ۴۰۱، ۴۰۳، ۴۰۴، ۴۰۹ `HAS_LICENSES`/`STRIPE_PRICE_IN_USE`/`MAINTENANCE_FEATURES_DISABLED`/`FEED_NOT_GATED`/`FEED_CACHE_DRAINING`/`DUPLICATE`، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"HAS_LICENSES","message":"plan has licenses"}}` |
| ۱۷ | اثر | به‌روزرسانی `plans`؛ audit `plan/updated` |
| ۱۸ | امنیت | تغییر `max_activations` روی لایسنس‌های موجود اثر می‌گذارد |
| ۱۹ | تست؟ | **بله** — `maintenance_test.go` |
| ۲۰ | یادداشت | تغییر `license_type` با وجود لایسنس مجاز نیست |

### ۱۰.۳.۵ `DELETE /api/v1/admin/plans/:id`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | حذف پلن |
| ۲ | متد و مسیر | `DELETE /api/v1/admin/plans/:id` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` |
| ۶ | path | `:id` = UUID پلن |
| ۷ | query | — |
| ۸ | هدرها | — |
| ۹ | بدنه | — |
| ۱۰ | الزامی | `:id` |
| ۱۱ | فیلدها | — |
| ۱۲ | نمونه | `curl -X DELETE -b cookie.txt http://localhost:9000/api/v1/admin/plans/<id>` |
| ۱۳ | پاسخ موفق | بدنه ندارد |
| ۱۴ | وضعیت | **۲۰۴** |
| ۱۵ | خطاها | ۴۰۱، ۴۰۳، ۴۰۴، ۴۰۹ `HAS_LICENSES`، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"HAS_LICENSES","message":"plan has licenses"}}` |
| ۱۷ | اثر | حذف پلن؛ audit `plan/deleted` |
| ۱۸ | امنیت | — |
| ۱۹ | تست؟ | ندارد |
| ۲۰ | یادداشت | entitlementها آبشاری حذف می‌شوند |

### ۱۰.۳.۶ `POST /api/v1/admin/entitlements`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | افزودن entitlement (ویژگی/سهمیه) به پلن |
| ۲ | متد و مسیر | `POST /api/v1/admin/entitlements` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` |
| ۶ | path | — |
| ۷ | query | — |
| ۸ | هدرها | `Content-Type: application/json` |
| ۹ | بدنه | `{"plan_id","feature","value_type","value","quota_period","quota_unit","stripe_meter_event_name"}` |
| ۱۰ | الزامی | `plan_id`, `feature`, `value_type`, `value` |
| ۱۱ | فیلدها | `value_type` ∈ `bool\|int\|string\|quota\|flag`؛ `quota_period` ∈ `hourly\|daily\|monthly\|yearly`؛ `stripe_meter_event_name` فقط برای `quota` |
| ۱۲ | نمونه | `curl -X POST -b cookie.txt -H 'Content-Type: application/json' -d '{"plan_id":"<id>","feature":"api_calls","value_type":"quota","value":"1000","quota_period":"monthly"}' http://localhost:9000/api/v1/admin/entitlements` |
| ۱۳ | پاسخ موفق | `*model.Entitlement` |
| ۱۴ | وضعیت | **۲۰۱** |
| ۱۵ | خطاها | ۴۰۰ `BAD_REQUEST`، ۴۰۱، ۴۰۳، ۴۰۴، ۴۰۹ `DUPLICATE`، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"DUPLICATE","message":"feature already exists for this plan"}}` |
| ۱۷ | اثر | درج در `entitlements`؛ audit `entitlement/created` |
| ۱۸ | امنیت | — |
| ۱۹ | تست؟ | ندارد |
| ۲۰ | یادداشت | `feature` یکتا در هر پلن است |

### ۱۰.۳.۷ `PUT /api/v1/admin/entitlements/:id`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | ویرایش entitlement |
| ۲ | متد و مسیر | `PUT /api/v1/admin/entitlements/:id` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` |
| ۶ | path | `:id` = UUID entitlement |
| ۷ | query | — |
| ۸ | هدرها | `Content-Type: application/json` |
| ۹ | بدنه | `value`، `quota_period`، `quota_unit`، `stripe_meter_event_name` (همه اختیاری) |
| ۱۰ | الزامی | — |
| ۱۱ | فیلدها | `value_type` و `feature` معمولاً تغییرناپذیرند |
| ۱۲ | نمونه | `curl -X PUT -b cookie.txt -H 'Content-Type: application/json' -d '{"value":"2000"}' http://localhost:9000/api/v1/admin/entitlements/<id>` |
| ۱۳ | پاسخ موفق | `*model.Entitlement` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۰ `BAD_REQUEST`، ۴۰۱، ۴۰۳، ۴۰۴، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"NOT_FOUND","message":"entitlement not found"}}` |
| ۱۷ | اثر | به‌روزرسانی؛ audit `entitlement/updated` |
| ۱۸ | امنیت | — |
| ۱۹ | تست؟ | ندارد |
| ۲۰ | یادداشت | — |

### ۱۰.۳.۸ `DELETE /api/v1/admin/entitlements/:id`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | حذف entitlement |
| ۲ | متد و مسیر | `DELETE /api/v1/admin/entitlements/:id` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` |
| ۶ | path | `:id` = UUID entitlement |
| ۷ | query | — |
| ۸ | هدرها | — |
| ۹ | بدنه | — |
| ۱۰ | الزامی | `:id` |
| ۱۱ | فیلدها | — |
| ۱۲ | نمونه | `curl -X DELETE -b cookie.txt http://localhost:9000/api/v1/admin/entitlements/<id>` |
| ۱۳ | پاسخ موفق | بدنه ندارد |
| ۱۴ | وضعیت | **۲۰۴** |
| ۱۵ | خطاها | ۴۰۱، ۴۰۳، ۴۰۴، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"NOT_FOUND","message":"entitlement not found"}}` |
| ۱۷ | اثر | حذف؛ audit `entitlement/deleted` |
| ۱۸ | امنیت | — |
| ۱۹ | تست؟ | ندارد |
| ۲۰ | یادداشت | — |

### ۱۰.۴ Licenses و Activations (۲۶) — گروه `licWrite`

### ۱۰.۴.۱ `GET /api/v1/admin/licenses`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | لیست لایسنس‌ها با فیلتر، جستجو و مرتب‌سازی |
| ۲ | متد و مسیر | `GET /api/v1/admin/licenses` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` یا `licenses:write` |
| ۶ | path | — |
| ۷ | query | `product_id`، `status`، `search`، `external_customer_id`، `external_workspace_id`، `limit`، `offset`، `sort`، `order` |
| ۸ | هدرها | — |
| ۹ | بدنه | — |
| ۱۰ | الزامی | — |
| ۱۱ | فیلدها | `sort` ∈ `created_at`(پیش‌فرض)/`valid_until`/`email`/`status`/`product`/`plan`؛ `order` ∈ `asc\|desc` |
| ۱۲ | نمونه | `curl -s -b cookie.txt 'http://localhost:9000/api/v1/admin/licenses?status=active&sort=email&order=asc'` |
| ۱۳ | پاسخ موفق | `{"licenses":[<License>...],"total":n,"limit":20,"offset":0,"license_key_hints":{"<id>":"A1B2"}}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۳ `PRODUCT_SCOPE_MISMATCH`، ۴۰۰ `INVALID_SORT`/`INVALID_ORDER`، ۴۰۱، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"INVALID_SORT","message":"invalid sort field"}}` |
| ۱۷ | اثر | فقط خواندن |
| ۱۸ | امنیت | `license_key_hints` فقط ۴ کاراکتر آخر را می‌دهد؛ کلید کامل در ۱۰.۴.۴ |
| ۱۹ | تست؟ | **بله** (سطح store) |
| ۲۰ | یادداشت | کلید محصول‌محور، `product_id` را فورس می‌کند |

### ۱۰.۴.۲ `GET /api/v1/admin/licenses/export`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | خروجی CSV/JSON از لایسنس‌ها |
| ۲ | متد و مسیر | `GET /api/v1/admin/licenses/export` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` یا `licenses:write` |
| ۶ | path | — |
| ۷ | query | `format` (`csv` پیش‌فرض / `json`)، `product_id`، `status` |
| ۸ | هدرها | `Accept` (بی‌اثر؛ `format` تعیین‌کننده است) |
| ۹ | بدنه | — |
| ۱۰ | الزامی | — |
| ۱۱ | فیلدها | خروجی CSV شامل `id,email,product,plan,status,license_key,valid_from,valid_until,updates_until,created_at` |
| ۱۲ | نمونه | `curl -s -b cookie.txt 'http://localhost:9000/api/v1/admin/licenses/export?format=csv' -o licenses.csv` |
| ۱۳ | پاسخ موفق | **بدون envelope** — CSV با `Content-Type: text/csv` یا آرایهٔ JSON |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۱، ۴۰۳، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | در حالت خطا پاسخ JSON استاندارد برمی‌گردد |
| ۱۷ | اثر | audit `license/keys_exported` (شامل کلید کامل در خروجی) |
| ۱۸ | امنیت | خروجی شامل **کلید خام** است؛ آن را امن نگه دارید و در لاگ ننویسید |
| ۱۹ | تست؟ | ندارد |
| ۲۰ | یادداشت | برای حجم بالا، `limit` ندارد و کل نتیجه را برمی‌گرداند |

### ۱۰.۴.۳ `GET /api/v1/admin/licenses/:id`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | دریافت یک لایسنس با راهنمای کلید |
| ۲ | متد و مسیر | `GET /api/v1/admin/licenses/:id` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` یا `licenses:write` |
| ۶ | path | `:id` = UUID لایسنس |
| ۷ | query | — |
| ۸ | هدرها | — |
| ۹ | بدنه | — |
| ۱۰ | الزامی | `:id` |
| ۱۱ | فیلدها | — |
| ۱۲ | نمونه | `curl -s -b cookie.txt http://localhost:9000/api/v1/admin/licenses/<id>` |
| ۱۳ | پاسخ موفق | `licenseWithHint`: فیلدهای License + `license_key_hint`، `key_usable`، `key_unusable_reason?` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۱، ۴۰۳، ۴۰۴ `NOT_FOUND`، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"NOT_FOUND","message":"license not found"}}` |
| ۱۷ | اثر | فقط خواندن |
| ۱۸ | امنیت | `key_usable=false` یعنی کلید قابل بازیابی نیست (مثلاً hash-only) |
| ۱۹ | تست؟ | ندارد |
| ۲۰ | یادداشت | — |

### ۱۰.۴.۴ `GET /api/v1/admin/licenses/:id/key`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | نمایش کلید کامل لایسنس |
| ۲ | متد و مسیر | `GET /api/v1/admin/licenses/:id/key` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` یا `licenses:write` |
| ۶ | path | `:id` = UUID لایسنس |
| ۷ | query | — |
| ۸ | هدرها | پاسخ دارای `Cache-Control: no-store` |
| ۹ | بدنه | — |
| ۱۰ | الزامی | `:id` |
| ۱۱ | فیلدها | — |
| ۱۲ | نمونه | `curl -s -b cookie.txt http://localhost:9000/api/v1/admin/licenses/<id>/key` |
| ۱۳ | پاسخ موفق | `{"license_key":"KG-XXXX-XXXX-XXXX-XXXX"}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۱، ۴۰۳، ۴۰۴، **۵۰۳ `LICENSE_KEY_UNAVAILABLE`**، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"LICENSE_KEY_UNAVAILABLE","message":"license key cannot be recovered"}}` |
| ۱۷ | اثر | audit `license/key_revealed` (IP ثبت می‌شود؛ کلید لاگ نمی‌شود) |
| ۱۸ | امنیت | حساس‌ترین endpoint ادمین؛ دسترسی را محدود کنید |
| ۱۹ | تست؟ | ندارد |
| ۲۰ | یادداشت | برای نصب‌های hash-only همیشه ۵۰۳ می‌دهد |

### ۱۰.۴.۵ `POST /api/v1/admin/licenses/:id/resend-email`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | ارسال مجدد ایمیل حاوی کلید لایسنس |
| ۲ | متد و مسیر | `POST /api/v1/admin/licenses/:id/resend-email` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` یا `licenses:write` |
| ۶ | path | `:id` = UUID لایسنس |
| ۷ | query | — |
| ۸ | هدرها | — |
| ۹ | بدنه | — (گیرنده همان `license.email` است) |
| ۱۰ | الزامی | `:id` |
| ۱۱ | فیلدها | — |
| ۱۲ | نمونه | `curl -X POST -b cookie.txt http://localhost:9000/api/v1/admin/licenses/<id>/resend-email` |
| ۱۳ | پاسخ موفق | `{"queued":true,"email":"customer@example.com"}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۱، ۴۰۳، ۴۰۴، ۴۰۹ `NO_EMAIL`/`LICENSE_NOT_USABLE`/`SMTP_NOT_CONFIGURED`، ۵۰۳ `LICENSE_KEY_UNAVAILABLE`، ۵۰۰ `EMAIL_QUEUE_FAILED`، ۴۲۹ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"NO_EMAIL","message":"license has no email address"}}` |
| ۱۷ | اثر | audit `license/key_emailed` + صف ایمیل |
| ۱۸ | امنیت | گیرنده قابل انتخاب توسط فراخوان نیست (جلوگیری از exfiltration) |
| ۱۹ | تست؟ | ندارد |
| ۲۰ | یادداشت | — |

### ۱۰.۴.۶ `POST /api/v1/admin/licenses`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | صدور لایسنس جدید |
| ۲ | متد و مسیر | `POST /api/v1/admin/licenses` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` یا `licenses:write` |
| ۶ | path | — |
| ۷ | query | — |
| ۸ | هدرها | `Content-Type: application/json` |
| ۹ | بدنه | `{"product_id","plan_id","email","notes","external_customer_id","external_workspace_id","valid_until"}` |
| ۱۰ | الزامی | `product_id`, `plan_id`, `email` |
| ۱۱ | فیلدها | `valid_until` RFC3339 و در آینده؛ `external_*` برای همگام‌سازی با سیستم بیرونی |
| ۱۲ | نمونه | `curl -X POST -b cookie.txt -H 'Content-Type: application/json' -d '{"product_id":"<id>","plan_id":"<id>","email":"customer@example.com"}' http://localhost:9000/api/v1/admin/licenses` |
| ۱۳ | پاسخ موفق | `licenseWithKey`: فیلدهای License + `license_key` خام (**فقط همین یک‌بار**) |
| ۱۴ | وضعیت | **۲۰۱** + `Cache-Control: no-store` |
| ۱۵ | خطاها | ۴۰۰ `BAD_REQUEST`، ۴۰۴، ۴۰۰ `PLAN_PRODUCT_MISMATCH`، ۴۰۳ `PRODUCT_SCOPE_MISMATCH`، ۴۰۹ `MAINTENANCE_FEATURES_DISABLED`/`FEED_NOT_GATED`/`PLAN_CHANGED`، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"PLAN_PRODUCT_MISMATCH","message":"plan does not belong to product"}}` |
| ۱۷ | اثر | درج در `licenses`؛ audit؛ webhook `license.created`؛ ایمیل کلید |
| ۱۸ | امنیت | کلید خام فقط یک‌بار برمی‌گردد؛ آن را ذخیره/ارسال امن کنید |
| ۱۹ | تست؟ | **بله** — `TestLicenseIssuance_MaintenanceGate` |
| ۲۰ | یادداشت | برای محصول دارای پلن bounded، `updates_until` خودکار تنظیم می‌شود |

### ۱۰.۴.۷ `POST /api/v1/admin/licenses/:id/refund`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | بازپرداخت و لغو اشتراک Stripe |
| ۲ | متد و مسیر | `POST /api/v1/admin/licenses/:id/refund` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` یا `licenses:write` |
| ۶ | path | `:id` = UUID لایسنس |
| ۷ | query | — |
| ۸ | هدرها | — |
| ۹ | بدنه | — |
| ۱۰ | الزامی | `:id` |
| ۱۱ | فیلدها | — |
| ۱۲ | نمونه | `curl -X POST -b cookie.txt http://localhost:9000/api/v1/admin/licenses/<id>/refund` |
| ۱۳ | پاسخ موفق | `{"status":"refunded","provider_result":"no_active_subscription\|stripe_cancel_failed\|stripe_subscription_canceled"}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۰ (`license has no payment subscription to refund`)، ۴۰۱، ۴۰۳، ۴۰۴، ۵۰۲ `STRIPE_UNAVAILABLE`، ۵۰۳ `STRIPE_NOT_CONFIGURED`، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"BAD_REQUEST","message":"license has no payment subscription to refund"}}` |
| ۱۷ | اثر | **Stripe cancel** + وضعیت `revoked`؛ audit؛ webhook `license.revoked` (`reason:"refund"`) |
| ۱۸ | امنیت | بازپرداخت مالی است؛ audit با IP ثبت می‌شود |
| ۱۹ | تست؟ | **بله** (سطح payment) |
| ۲۰ | یادداشت | اگر Stripe در دسترس نباشد `provider_result` مقدار خطا را نشان می‌دهد |

### ۱۰.۴.۸ `POST /api/v1/admin/licenses/:id/revoke`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | لغو دائمی لایسنس |
| ۲ | متد و مسیر | `POST /api/v1/admin/licenses/:id/revoke` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` یا `licenses:write` |
| ۶ | path | `:id` = UUID لایسنس |
| ۷ | query | — |
| ۸ | هدرها | — |
| ۹ | بدنه | — |
| ۱۰ | الزامی | `:id` |
| ۱۱ | فیلدها | — |
| ۱۲ | نمونه | `curl -X POST -b cookie.txt http://localhost:9000/api/v1/admin/licenses/<id>/revoke` |
| ۱۳ | پاسخ موفق | `{"status":"revoked"}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۱، ۴۰۳، ۴۰۴، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"NOT_FOUND","message":"license not found"}}` |
| ۱۷ | اثر | وضعیت `revoked`؛ audit؛ webhook `license.revoked` |
| ۱۸ | امنیت | برگشت‌ناپذیر؛ برای تعلیق موقت از `suspend` استفاده کنید |
| ۱۹ | تست؟ | **بله** (سطح store) |
| ۲۰ | یادداشت | — |

### ۱۰.۴.۹ `POST /api/v1/admin/licenses/:id/suspend`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | تعلیق موقت لایسنس |
| ۲ | متد و مسیر | `POST /api/v1/admin/licenses/:id/suspend` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` یا `licenses:write` |
| ۶ | path | `:id` = UUID لایسنس |
| ۷ | query | — |
| ۸ | هدرها | — |
| ۹ | بدنه | — |
| ۱۰ | الزامی | `:id` |
| ۱۱ | فیلدها | — |
| ۱۲ | نمونه | `curl -X POST -b cookie.txt http://localhost:9000/api/v1/admin/licenses/<id>/suspend` |
| ۱۳ | پاسخ موفق | `{"status":"suspended"}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۱، ۴۰۳، ۴۰۴، ۴۰۹ `LICENSE_NOT_SUSPENDABLE`، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"LICENSE_NOT_SUSPENDABLE","message":"license cannot be suspended in its current state"}}` |
| ۱۷ | اثر | وضعیت `suspended`؛ audit؛ webhook `license.suspended`؛ ایمیل |
| ۱۸ | امنیت | — |
| ۱۹ | تست؟ | **بله** (سطح store) |
| ۲۰ | یادداشت | `reinstate` وضعیت قبلی را برمی‌گرداند |

### ۱۰.۴.۱۰ `POST /api/v1/admin/licenses/:id/reinstate`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | بازگرداندن لایسنس معلق/لغوشده به وضعیت معتبر |
| ۲ | متد و مسیر | `POST /api/v1/admin/licenses/:id/reinstate` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` یا `licenses:write` |
| ۶ | path | `:id` = UUID لایسنس |
| ۷ | query | — |
| ۸ | هدرها | — |
| ۹ | بدنه | — |
| ۱۰ | الزامی | `:id` |
| ۱۱ | فیلدها | — |
| ۱۲ | نمونه | `curl -X POST -b cookie.txt http://localhost:9000/api/v1/admin/licenses/<id>/reinstate` |
| ۱۳ | پاسخ موفق | `{"status":"<new status>"}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۰، ۴۰۱، ۴۰۳، ۴۰۴، ۴۰۹ `EXPIRY_PASSED`، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"EXPIRY_PASSED","message":"set a new expiry date instead"}}` |
| ۱۷ | اثر | تغییر وضعیت؛ audit؛ webhook `license.reinstated` |
| ۱۸ | امنیت | برای لایسنس منقضی باید `valid-until` جدید تنظیم شود |
| ۱۹ | تست؟ | **بله** (سطح store) |
| ۲۰ | یادداشت | `EXPIRY_PASSED` راهنمای اقدام می‌دهد |

### ۱۰.۴.۱۱ `POST /api/v1/admin/licenses/:id/valid-until`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | تنظیم/پاک‌کردن تاریخ انقضای لایسنس |
| ۲ | متد و مسیر | `POST /api/v1/admin/licenses/:id/valid-until` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` یا `licenses:write` |
| ۶ | path | `:id` = UUID لایسنس |
| ۷ | query | — |
| ۸ | هدرها | `Content-Type: application/json` |
| ۹ | بدنه | `{"valid_until":"2027-01-01T00:00:00Z"}` یا `{"valid_until":null}` |
| ۱۰ | الزامی | `valid_until` (می‌تواند null باشد) |
| ۱۱ | فیلدها | خالی/null = دائمی؛ وگرنه RFC3339 در آینده |
| ۱۲ | نمونه | `curl -X POST -b cookie.txt -H 'Content-Type: application/json' -d '{"valid_until":"2027-01-01T00:00:00Z"}' http://localhost:9000/api/v1/admin/licenses/<id>/valid-until` |
| ۱۳ | پاسخ موفق | `*model.License` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۰ `BAD_REQUEST`، ۴۰۳، ۴۰۴، ۴۰۹ `STRIPE_MANAGED`، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"STRIPE_MANAGED","message":"expiry is managed by Stripe"}}` |
| ۱۷ | اثر | audit `valid_until_changed`؛ webhook `license.expiry_changed` |
| ۱۸ | امنیت | تاریخ گذشته لایسنس را فوراً منقضی می‌کند |
| ۱۹ | تست؟ | **بله** (سطح store) |
| ۲۰ | یادداشت | برای لایسنس متصل به Stripe مجاز نیست |

### ۱۰.۴.۱۲ `POST /api/v1/admin/licenses/:id/updates-until`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | تنظیم/پاک‌کردن پایان دورهٔ به‌روزرسانی (maintenance) |
| ۲ | متد و مسیر | `POST /api/v1/admin/licenses/:id/updates-until` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` یا `licenses:write` |
| ۶ | path | `:id` = UUID لایسنس |
| ۷ | query | — |
| ۸ | هدرها | `Content-Type: application/json` |
| ۹ | بدنه | `{"updates_until":"2027-01-01T00:00:00Z"}` یا `{"updates_until":null}` |
| ۱۰ | الزامی | `updates_until` |
| ۱۱ | فیلدها | null = پاک‌کردن; وگرنه RFC3339 |
| ۱۲ | نمونه | `curl -X POST -b cookie.txt -H 'Content-Type: application/json' -d '{"updates_until":"2027-01-01T00:00:00Z"}' http://localhost:9000/api/v1/admin/licenses/<id>/updates-until` |
| ۱۳ | پاسخ موفق | `*model.License` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۰ `NOT_PERPETUAL`، ۴۰۳، ۴۰۴، ۴۰۹ `MAINTENANCE_FEATURES_DISABLED`/`FEED_NOT_GATED`/`FEED_CACHE_DRAINING`/`LICENSE_CHANGED`، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"NOT_PERPETUAL","message":"license is not perpetual"}}` |
| ۱۷ | اثر | audit `updates_until_changed`؛ webhook `license.updates_changed` |
| ۱۸ | امنیت | — |
| ۱۹ | تست؟ | **بله** — `maintenance_test.go` |
| ۲۰ | یادداشت | فقط برای لایسنس perpetual معنا دارد |

### ۱۰.۴.۱۳ `POST /api/v1/admin/licenses/:id/change-plan`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | تغییر پلن لایسنس |
| ۲ | متد و مسیر | `POST /api/v1/admin/licenses/:id/change-plan` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` یا `licenses:write` |
| ۶ | path | `:id` = UUID لایسنس |
| ۷ | query | — |
| ۸ | هدرها | `Content-Type: application/json` |
| ۹ | بدنه | `{"plan_id":"<uuid>"}` |
| ۱۰ | الزامی | `plan_id` |
| ۱۱ | فیلدها | پلن باید متعلق به همان محصول باشد |
| ۱۲ | نمونه | `curl -X POST -b cookie.txt -H 'Content-Type: application/json' -d '{"plan_id":"<id>"}' http://localhost:9000/api/v1/admin/licenses/<id>/change-plan` |
| ۱۳ | پاسخ موفق | `{"status":"plan_changed","plan_id":"..."}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۰، ۴۰۴، ۴۰۹ `STRIPE_MANAGED`/`MAINTENANCE_FEATURES_DISABLED`/`FEED_NOT_GATED`/`FEED_CACHE_DRAINING`/`LICENSE_CHANGED`، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"STRIPE_MANAGED","message":"plan is managed by Stripe"}}` |
| ۱۷ | اثر | audit؛ webhook `plan.changed`؛ ایمیل |
| ۱۸ | امنیت | — |
| ۱۹ | تست؟ | **بله** |
| ۲۰ | یادداشت | محدودیت‌های پلن جدید (max_activations) اعمال می‌شود |

### ۱۰.۴.۱۴ `POST /api/v1/admin/licenses/:id/stripe/unlink`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | جداکردن لایسنس از اشتراک Stripe پایان‌یافته |
| ۲ | متد و مسیر | `POST /api/v1/admin/licenses/:id/stripe/unlink` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` یا `licenses:write` |
| ۶ | path | `:id` = UUID لایسنس |
| ۷ | query | — |
| ۸ | هدرها | — |
| ۹ | بدنه | — |
| ۱۰ | الزامی | `:id` |
| ۱۱ | فیلدها | — |
| ۱۲ | نمونه | `curl -X POST -b cookie.txt http://localhost:9000/api/v1/admin/licenses/<id>/stripe/unlink` |
| ۱۳ | پاسخ موفق | `{"status":"not_linked"}` یا `{"status":"unlinked"}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۳، ۴۰۴، ۵۰۳ `STRIPE_NOT_CONFIGURED`، ۵۰۲ `STRIPE_UNAVAILABLE`، ۴۰۹ `SUBSCRIPTION_LIVE`/`SUBSCRIPTION_CHANGED`، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"SUBSCRIPTION_LIVE","message":"subscription is still active"}}` |
| ۱۷ | اثر | فراخوانی Stripe + `ClearStripeSubscription`؛ audit `stripe_unlinked` |
| ۱۸ | امنیت | فقط اشتراک غیرفعال/پایان‌یافته قابل جداشدن است |
| ۱۹ | تست؟ | **بله** (سطح payment) |
| ۲۰ | یادداشت | پس از unlink، لایسنس محلی قابل مدیریت است |

### ۱۰.۴.۱۵ `GET /api/v1/admin/licenses/:id/usage`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | مشاهدهٔ رویدادها و شمارنده‌های مصرف |
| ۲ | متد و مسیر | `GET /api/v1/admin/licenses/:id/usage` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` یا `licenses:write` |
| ۶ | path | `:id` = UUID لایسنس |
| ۷ | query | `feature`، `limit`، `offset` |
| ۸ | هدرها | — |
| ۹ | بدنه | — |
| ۱۰ | الزامی | `:id` |
| ۱۱ | فیلدها | `feature` فیلتر اختیاری |
| ۱۲ | نمونه | `curl -s -b cookie.txt 'http://localhost:9000/api/v1/admin/licenses/<id>/usage?feature=api_calls'` |
| ۱۳ | پاسخ موفق | `{"events":[...],"counters":[...],"total":n,"limit":20,"offset":0}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۱، ۴۰۳، ۴۰۴، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"NOT_FOUND","message":"license not found"}}` |
| ۱۷ | اثر | فقط خواندن |
| ۱۸ | امنیت | — |
| ۱۹ | تست؟ | ندارد |
| ۲۰ | یادداشت | — |

### ۱۰.۴.۱۶ `POST /api/v1/admin/licenses/:id/usage/reset`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | صفرکردن شمارندهٔ مصرف یک feature |
| ۲ | متد و مسیر | `POST /api/v1/admin/licenses/:id/usage/reset` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` یا `licenses:write` |
| ۶ | path | `:id` = UUID لایسنس |
| ۷ | query | — |
| ۸ | هدرها | `Content-Type: application/json` |
| ۹ | بدنه | `{"feature","period","period_key"}` |
| ۱۰ | الزامی | `feature` |
| ۱۱ | فیلدها | `period` پیش‌فرض `monthly`؛ `period_key` پیش‌فرض `CurrentPeriodKey` |
| ۱۲ | نمونه | `curl -X POST -b cookie.txt -H 'Content-Type: application/json' -d '{"feature":"api_calls"}' http://localhost:9000/api/v1/admin/licenses/<id>/usage/reset` |
| ۱۳ | پاسخ موفق | `{"status":"reset"}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۰، ۴۰۱، ۴۰۳، ۴۰۴، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"BAD_REQUEST","message":"feature is required"}}` |
| ۱۷ | اثر | audit `usage_reset` |
| ۱۸ | امنیت | — |
| ۱۹ | تست؟ | ندارد |
| ۲۰ | یادداشت | — |

### ۱۰.۴.۱۷ `GET /api/v1/admin/licenses/:id/seats`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | لیست صندلی‌های لایسنس |
| ۲ | متد و مسیر | `GET /api/v1/admin/licenses/:id/seats` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` یا `licenses:write` |
| ۶ | path | `:id` = UUID لایسنس |
| ۷ | query | `limit`، `offset` |
| ۸ | هدرها | — |
| ۹ | بدنه | — |
| ۱۰ | الزامی | `:id` |
| ۱۱ | فیلدها | — |
| ۱۲ | نمونه | `curl -s -b cookie.txt 'http://localhost:9000/api/v1/admin/licenses/<id>/seats'` |
| ۱۳ | پاسخ موفق | `{"seats":[...],"active_count":n,"total":n,"limit":20,"offset":0}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۱، ۴۰۳، ۴۰۴، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"NOT_FOUND","message":"license not found"}}` |
| ۱۷ | اثر | فقط خواندن |
| ۱۸ | امنیت | — |
| ۱۹ | تست؟ | ندارد |
| ۲۰ | یادداشت | — |

### ۱۰.۴.۱۸ `GET /api/v1/admin/licenses/:id/addons`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | لیست addonهای لایسنس |
| ۲ | متد و مسیر | `GET /api/v1/admin/licenses/:id/addons` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` یا `licenses:write` |
| ۶ | path | `:id` = UUID لایسنس |
| ۷ | query | `limit`، `offset` |
| ۸ | هدرها | — |
| ۹ | بدنه | — |
| ۱۰ | الزامی | `:id` |
| ۱۱ | فیلدها | — |
| ۱۲ | نمونه | `curl -s -b cookie.txt 'http://localhost:9000/api/v1/admin/licenses/<id>/addons'` |
| ۱۳ | پاسخ موفق | `{"addons":[...],"total":n,"limit":20,"offset":0}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۱، ۴۰۳، ۴۰۴، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"NOT_FOUND","message":"license not found"}}` |
| ۱۷ | اثر | فقط خواندن |
| ۱۸ | امنیت | — |
| ۱۹ | تست؟ | ندارد |
| ۲۰ | یادداشت | — |

### ۱۰.۴.۱۹ `POST /api/v1/admin/licenses/:id/addons`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | افزودن addon به لایسنس |
| ۲ | متد و مسیر | `POST /api/v1/admin/licenses/:id/addons` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` یا `licenses:write` |
| ۶ | path | `:id` = UUID لایسنس |
| ۷ | query | — |
| ۸ | هدرها | `Content-Type: application/json` |
| ۹ | بدنه | `{"addon_id":"<uuid>"}` |
| ۱۰ | الزامی | `addon_id` |
| ۱۱ | فیلدها | addon باید متعلق به همان محصول باشد |
| ۱۲ | نمونه | `curl -X POST -b cookie.txt -H 'Content-Type: application/json' -d '{"addon_id":"<id>"}' http://localhost:9000/api/v1/admin/licenses/<id>/addons` |
| ۱۳ | پاسخ موفق | `*model.LicenseAddon` |
| ۱۴ | وضعیت | **۲۰۱** |
| ۱۵ | خطاها | ۴۰۰، ۴۰۱، ۴۰۳، ۴۰۴، ۴۰۹ `DUPLICATE`، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"DUPLICATE","message":"addon already attached"}}` |
| ۱۷ | اثر | درج؛ audit `addon_added` |
| ۱۸ | امنیت | — |
| ۱۹ | تست؟ | ندارد |
| ۲۰ | یادداشت | افزودن addon ممکن است entitlement اضافه کند |

### ۱۰.۴.۲۰ `DELETE /api/v1/admin/licenses/:id/addons/:addon_id`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | حذف addon از لایسنس |
| ۲ | متد و مسیر | `DELETE /api/v1/admin/licenses/:id/addons/:addon_id` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` یا `licenses:write` |
| ۶ | path | `:id` = UUID لایسنس، `:addon_id` = UUID addon |
| ۷ | query | — |
| ۸ | هدرها | — |
| ۹ | بدنه | — |
| ۱۰ | الزامی | هر دو پارامتر |
| ۱۱ | فیلدها | — |
| ۱۲ | نمونه | `curl -X DELETE -b cookie.txt http://localhost:9000/api/v1/admin/licenses/<id>/addons/<addon_id>` |
| ۱۳ | پاسخ موفق | بدنه ندارد |
| ۱۴ | وضعیت | **۲۰۴** |
| ۱۵ | خطاها | ۴۰۱، ۴۰۳، ۴۰۴، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"NOT_FOUND","message":"addon not found"}}` |
| ۱۷ | اثر | حذف؛ audit `addon_removed` |
| ۱۸ | امنیت | — |
| ۱۹ | تست؟ | ندارد |
| ۲۰ | یادداشت | — |

### ۱۰.۴.۲۱ `GET /api/v1/admin/licenses/:id/floating`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | لیست نشست‌های floating لایسنس |
| ۲ | متد و مسیر | `GET /api/v1/admin/licenses/:id/floating` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` یا `licenses:write` |
| ۶ | path | `:id` = UUID لایسنس |
| ۷ | query | `limit`، `offset` |
| ۸ | هدرها | — |
| ۹ | بدنه | — |
| ۱۰ | الزامی | `:id` |
| ۱۱ | فیلدها | — |
| ۱۲ | نمونه | `curl -s -b cookie.txt 'http://localhost:9000/api/v1/admin/licenses/<id>/floating'` |
| ۱۳ | پاسخ موفق | `{"sessions":[...],"active":n,"total":n,"limit":20,"offset":0}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۱، ۴۰۳، ۴۰۴، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"NOT_FOUND","message":"license not found"}}` |
| ۱۷ | اثر | فقط خواندن |
| ۱۸ | امنیت | — |
| ۱۹ | تست؟ | ندارد |
| ۲۰ | یادداشت | فقط برای پلن با `license_model=floating` پر می‌شود |

### ۱۰.۴.۲۲ `DELETE /api/v1/admin/activations/:id`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | آزادکردن سهم دستگاه با **حذف نرم** (activation به tombstone تبدیل می‌شود، نه پاک‌شدن فیزیکی) |
| ۲ | متد و مسیر | `DELETE /api/v1/admin/activations/:id` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` یا `licenses:write` |
| ۶ | path | `:id` = UUID activation |
| ۷ | query | — |
| ۸ | هدرها | — |
| ۹ | بدنه | — |
| ۱۰ | الزامی | `:id` |
| ۱۱ | فیلدها | — |
| ۱۲ | نمونه | `curl -X DELETE -b cookie.txt http://localhost:9000/api/v1/admin/activations/<id>` |
| ۱۳ | پاسخ موفق | بدنه ندارد |
| ۱۴ | وضعیت | **۲۰۴** |
| ۱۵ | خطاها | ۴۰۱، ۴۰۳، ۴۰۴، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"NOT_FOUND","message":"activation not found"}}` |
| ۱۷ | اثر | `status='deactivated'`، `deactivated_at=now()`، `deactivated_reason='admin'`؛ audit `activation/deactivated`؛ webhook `license.deactivated` |
| ۱۸ | امنیت | سهم دستگاه فوراً آزاد می‌شود؛ رکورد برای بازرسی باقی می‌ماند (برخلاف نسخه‌های قدیمی که hard delete بود) |
| ۱۹ | تست؟ | **بله** — `internal/store/activation_lifecycle_test.go` (۳ سناریو) |
| ۲۰ | یادداشت | مسیر عمداً زیر `/activations` است نه `/licenses/:id/...`؛ همین رفتار با `POST .../deactivate` هم در دسترس است |

### ۱۰.۴.۲۳ `POST /api/v1/admin/licenses/:id/transfer`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | انتقال لایسنس به ایمیل جدید (تغییر مالک) |
| ۲ | متد و مسیر | `POST /api/v1/admin/licenses/:id/transfer` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` یا `licenses:write` |
| ۶ | path | `:id` = UUID لایسنس |
| ۷ | query | — |
| ۸ | هدرها | `Content-Type: application/json` |
| ۹ | بدنه | `{"new_email":"new@example.com","release_activations":true,"reason":"انتقال به تیم پشتیبانی"}` |
| ۱۰ | الزامی | `new_email`؛ `release_activations` و `reason` اختیاری |
| ۱۱ | فیلدها | `new_email` ایمیل معتبر (trim می‌شود)، `release_activations` bool (پیش‌فرض `true`)، `reason` ≤۲۵۶ کاراکتر |
| ۱۲ | نمونه | `curl -X POST -b cookie.txt -H 'Content-Type: application/json' -d '{"new_email":"new@example.com"}' http://localhost:9000/api/v1/admin/licenses/<id>/transfer` |
| ۱۳ | پاسخ موفق | `{"status":"transferred","license_id":"...","email":"new@example.com","activations_released":<int>}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۰ (`new_email is required` / `INVALID_INPUT` / reason طولانی)، ۴۰۱، ۴۰۳ `PRODUCT_SCOPE_MISMATCH`، ۴۰۴ `NOT_FOUND`، ۴۰۹ `IDENTICAL_EMAIL`، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"IDENTICAL_EMAIL","message":"new_email is identical to current license email"}}` |
| ۱۷ | اثر | در یک تراکنش با قفل ردیف لایسنس: `email` عوض می‌شود و `user_id` خالی می‌شود؛ اگر `release_activations=true` باشد همهٔ activationهای `active` به `deactivated` با دلیل `transfer` می‌روند؛ audit `license/transferred`؛ webhook `license.transferred` |
| ۱۸ | امنیت | مقایسهٔ ایمیل case-insensitive است؛ کلید لایسنس تغییر نمی‌کند، پس دستگاه‌های قدیمی تا وقتی فعال‌سازی‌شان آزاد نشده معتبر می‌مانند — پیش‌فرض `true` برای همین است |
| ۱۹ | تست؟ | **بله** — `internal/handler/license_activation_test.go` (`TestLicenseTransfer_AdminWorkflow`) و `internal/store/activation_lifecycle_test.go` |
| ۲۰ | یادداشت | `activations_released` تعداد ردیف‌هایی است که واقعاً آزاد شدند |

### ۱۰.۴.۲۴ `POST /api/v1/admin/licenses/:id/offline-token`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | صدور توکن آفلاین امضاشده برای یک دستگاه روی این لایسنس (بدون نیاز به تعامل کلاینت با `/license/activate`) |
| ۲ | متد و مسیر | `POST /api/v1/admin/licenses/:id/offline-token` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` یا `licenses:write` |
| ۶ | path | `:id` = UUID لایسنس |
| ۷ | query | — |
| ۸ | هدرها | `Content-Type: application/json` |
| ۹ | بدنه | `{"identifier":"uuid:...","label":"PC-Office"}` |
| ۱۰ | الزامی | `identifier`؛ `label` اختیاری |
| ۱۱ | فیلدها | `identifier` ≤۲۵۶ کاراکتر و بدون فاصلهٔ ابتدا/انتها؛ قالب‌های مجاز `uuid:…`، `sha256:<64hex>`، `kgid:v1:<platform>:<64hex>` یا مقدار opaque؛ `label` نام نمایشی |
| ۱۲ | نمونه | `curl -X POST -b cookie.txt -H 'Content-Type: application/json' -d '{"identifier":"kgid:v1:windows:5581b5a7b290e096fdea9b19916561e13d15fc983a9b57c53a9e5ad91f29f4b7","label":"PC-Office"}' http://localhost:9000/api/v1/admin/licenses/<id>/offline-token` |
| ۱۳ | پاسخ موفق | `{"token":"<base64url(payload)>.<base64url(sig)>","status":"activated","license_id":"...","identifier":"uuid:..."}` (مقدار `status` مستقیماً از نتیجهٔ activate می‌آید؛ برای دستگاه تکراری `already_activated`) |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۰ (`INVALID_INPUT` برای شناسهٔ بدشکل)، ۴۰۱، ۴۰۳ `PRODUCT_SCOPE_MISMATCH`، ۴۰۴ `NOT_FOUND`، ۴۰۹ `ACTIVATION_LIMIT` (با `details.max/current`)، ۴۰۳ `LICENSE_REVOKED`/`LICENSE_SUSPENDED`/`LICENSE_EXPIRED`، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"ACTIVATION_LIMIT","message":"maximum activations reached","details":{"max":3,"current":3}}}` |
| ۱۷ | اثر | **یک فعال‌سازی واقعی انجام می‌دهد** (سهم دستگاه مصرف می‌شود و در لیست activationها ظاهر می‌شود) و توکن حاصل از همان نتیجه برگردانده می‌شود؛ audit `license/offline_token_issued` |
| ۱۸ | امنیت | کلید خصوصی امضا هرگز به کلاینت نمی‌رسد؛ توکن به دستگاه/شناسهٔ مشخصی گره می‌خورد و TTL دارد؛ شناسهٔ دارای فاصله عمداً رد می‌شود تا یک دستگاه دو سهم نگیرد |
| ۱۹ | تست؟ | **بله** — `internal/handler/license_activation_test.go`، `internal/license/offline_test.go` |
| ۲۰ | یادداشت | اگر فقط می‌خواهید توکن موجود را ببینید، از `GET .../licenses/:id` استفاده کنید؛ این endpoint وضعیت را تغییر می‌دهد |

### ۱۰.۴.۲۴.۱ `POST /api/v1/admin/licenses/:id/reset-device-binding`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | آزادکردن binding سخت‌گیرانهٔ دستگاه تا لایسنس بتواند روی سخت‌افزار جدید فعال شود (تنها راه مجاز در حالت strict) |
| ۲ | متد و مسیر | `POST /api/v1/admin/licenses/:id/reset-device-binding` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` یا `licenses:write` |
| ۶ | path | `:id` = UUID لایسنس |
| ۷ | query | — |
| ۸ | هدرها | — |
| ۹ | بدنه | — |
| ۱۰ | الزامی | `:id` |
| ۱۱ | فیلدها | — |
| ۱۲ | نمونه | `curl -X POST -b cookie.txt http://localhost:9000/api/v1/admin/licenses/<id>/reset-device-binding` |
| ۱۳ | پاسخ موفق | `{"status":"reset","license_id":"...","activations_released":1}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۱، ۴۰۳ `PRODUCT_SCOPE_MISMATCH`، ۴۰۴ `NOT_FOUND`، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"NOT_FOUND","message":"license not found"}}` |
| ۱۷ | اثر | همهٔ activationهای `active` این لایسنس به `deactivated` با `deactivated_reason='admin'` تبدیل می‌شوند؛ audit `license/device_binding_reset`؛ webhook `license.device_reset`. پس از آن اولین activate با شناسهٔ جدید (ترجیحاً `kgid:v1`) موفق می‌شود |
| ۱۸ | امنیت | این endpoint جایگزین امن self-service است؛ فقط ادمین/API Key با scope لازم می‌تواند قفل را باز کند. تعداد آزادشده در پاسخ و audit ثبت می‌شود |
| ۱۹ | تست؟ | **بله** — `TestStrictDeviceBinding_AdminResetAllowsRebind` |
| ۲۰ | یادداشت | در حالت `STRICT_DEVICE_BINDING=false` لازم نیست، ولی بی‌خطر است و همان tombstoneهای ادمین را می‌سازد |

### ۱۰.۴.۲۵ `POST /api/v1/admin/activations/:id/deactivate`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | آزادکردن نرم سهم یک دستگاه (بدون حذف رکورد) |
| ۲ | متد و مسیر | `POST /api/v1/admin/activations/:id/deactivate` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` یا `licenses:write` |
| ۶ | path | `:id` = UUID activation |
| ۷ | query | — |
| ۸ | هدرها | — |
| ۹ | بدنه | — (اگر بدنه‌ای بفرستید نادیده گرفته می‌شود) |
| ۱۰ | الزامی | `:id` |
| ۱۱ | فیلدها | — |
| ۱۲ | نمونه | `curl -X POST -b cookie.txt http://localhost:9000/api/v1/admin/activations/<id>/deactivate` |
| ۱۳ | پاسخ موفق | `{"status":"deactivated","id":"..."}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۱، ۴۰۳ `PRODUCT_SCOPE_MISMATCH`، ۴۰۴ `NOT_FOUND`، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"NOT_FOUND","message":"activation not found"}}` |
| ۱۷ | اثر | `status='deactivated'`، `deactivated_at=now()`، `deactivated_reason='admin'`؛ audit `activation/deactivated`؛ webhook `license.deactivated` |
| ۱۸ | امنیت | idempotent است: اگر قبلاً غیرفعال شده باشد، پاسخ موفق برمی‌گردد و سهم دوباره آزاد نمی‌شود |
| ۱۹ | تست؟ | **بله** — `internal/handler/license_activation_test.go` (`TestAdminActivation_DeactivateAndReactivate`) |
| ۲۰ | یادداشت | معادل `DELETE /admin/activations/:id` ولی با بدنهٔ پاسخ و بدون ابهام REST |

### ۱۰.۴.۲۶ `POST /api/v1/admin/activations/:id/reactivate`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | بازگرداندن یک activation غیرفعال‌شده به وضعیت فعال (مصرف دوبارهٔ سهم) |
| ۲ | متد و مسیر | `POST /api/v1/admin/activations/:id/reactivate` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` یا `licenses:write` |
| ۶ | path | `:id` = UUID activation |
| ۷ | query | — |
| ۸ | هدرها | — |
| ۹ | بدنه | — |
| ۱۰ | الزامی | `:id` |
| ۱۱ | فیلدها | — |
| ۱۲ | نمونه | `curl -X POST -b cookie.txt http://localhost:9000/api/v1/admin/activations/<id>/reactivate` |
| ۱۳ | پاسخ موفق | `{"status":"reactivated","id":"..."}` |
| ۱۴ | وضعیت | ۲۰۰ (اگر از قبل active باشد هم ۲۰۰ و بدون تغییر) |
| ۱۵ | خطاها | ۴۰۱، ۴۰۳ `PRODUCT_SCOPE_MISMATCH`، ۴۰۴ `NOT_FOUND`، ۴۰۹ `DEVICE_CHANGED` (حالت strict و وجود دستگاه فعال دیگر)، ۴۰۹ `ACTIVATION_LIMIT` (فقط tolerant؛ با `details.max/current`)، ۴۰۳ `LICENSE_REVOKED`/`LICENSE_SUSPENDED`/`LICENSE_EXPIRED`، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"DEVICE_CHANGED","message":"strict device binding allows one active device; deactivate the current device first","details":{"reason":"different_device","active":1}}}` |
| ۱۷ | اثر | `status='active'`، پاک‌شدن `deactivated_at`/`deactivated_reason`؛ audit `activation/reactivated`؛ webhook `license.activated`. در حالت strict فقط وقتی مجاز است که هیچ دستگاه فعال دیگری نباشد |
| ۱۸ | امنیت | شمارش سهم فقط ردیف‌های `status='active'` را می‌شمارد و زیر قفل ردیف لایسنس انجام می‌شود تا با فعال‌سازی هم‌زمان مسابقه نداشته باشد؛ در حالت strict سقف به ۱ کاهش می‌یابد |
| ۱۹ | تست؟ | **بله** — `internal/handler/license_activation_test.go` (شامل `TestStrictDeviceBinding_AdminResetAllowsRebind`) و `internal/store/activation_lifecycle_test.go` |
| ۲۰ | یادداشت | برای دستگاه گم‌شده/تعویض‌شده ابتدا دستگاه فعال را deactivate یا از `reset-device-binding` استفاده کنید؛ در حالت strict reactivation دستگاه دوم `DEVICE_CHANGED` می‌دهد |

### ۱۰.۵ API Keys (۴) — admin

### ۱۰.۵.۱ `GET /api/v1/admin/api-keys`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | لیست کلیدهای API |
| ۲ | متد و مسیر | `GET /api/v1/admin/api-keys` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` |
| ۶ | path | — |
| ۷ | query | `product_id`، `search`، `limit`، `offset` |
| ۸ | هدرها | — |
| ۹ | بدنه | — |
| ۱۰ | الزامی | — |
| ۱۱ | فیلدها | `key_hash` با `json:"-"` مخفی است؛ فقط `prefix` دیده می‌شود |
| ۱۲ | نمونه | `curl -s -b cookie.txt 'http://localhost:9000/api/v1/admin/api-keys'` |
| ۱۳ | پاسخ موفق | `{"api_keys":[{"id","product_id","name","prefix","scopes","last_used_at","created_at"}...],"total":n,"limit":20,"offset":0}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۱، ۴۰۳، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"UNAUTHORIZED","message":"unauthorized"}}` |
| ۱۷ | اثر | فقط خواندن |
| ۱۸ | امنیت | کلید کامل هرگز در لیست برنمی‌گردد |
| ۱۹ | تست؟ | ندارد |
| ۲۰ | یادداشت | — |

### ۱۰.۵.۲ `POST /api/v1/admin/api-keys`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | ساخت کلید API جدید |
| ۲ | متد و مسیر | `POST /api/v1/admin/api-keys` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` |
| ۶ | path | — |
| ۷ | query | — |
| ۸ | هدرها | `Content-Type: application/json` |
| ۹ | بدنه | `{"product_id","name","scopes":["..."]}` |
| ۱۰ | الزامی | `name` (حداقل یک scope) |
| ۱۱ | فیلدها | `product_id` اختیاری (محدودکردن به یک محصول)؛ `scopes[]` از `model.AllScopes()` |
| ۱۲ | نمونه | `curl -X POST -b cookie.txt -H 'Content-Type: application/json' -d '{"name":"CI","scopes":["releases:write"]}' http://localhost:9000/api/v1/admin/api-keys` |
| ۱۳ | پاسخ موفق | `{"id","product_id","name","key","prefix","scopes","created_at"}` — **`key` فقط همین یک‌بار** |
| ۱۴ | وضعیت | **۲۰۱** + `Cache-Control: no-store` |
| ۱۵ | خطاها | ۴۰۰ (`name is required`/scope ناشناخته)، ۴۰۱، ۴۰۳، ۴۰۴، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"BAD_REQUEST","message":"name is required"}}` |
| ۱۷ | اثر | درج در `api_keys`؛ audit `api_key/created` |
| ۱۸ | امنیت | کلید خام فقط یک‌بار نمایش داده می‌شود؛ آن را امن ذخیره کنید |
| ۱۹ | تست؟ | ندارد |
| ۲۰ | یادداشت | `admin` wildcard همهٔ مسیرها را باز می‌کند |

### ۱۰.۵.۳ `POST /api/v1/admin/api-keys/:id/rotate`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | چرخش کلید API |
| ۲ | متد و مسیر | `POST /api/v1/admin/api-keys/:id/rotate` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` |
| ۶ | path | `:id` = UUID کلید |
| ۷ | query | — |
| ۸ | هدرها | — |
| ۹ | بدنه | — |
| ۱۰ | الزامی | `:id` |
| ۱۱ | فیلدها | — |
| ۱۲ | نمونه | `curl -X POST -b cookie.txt http://localhost:9000/api/v1/admin/api-keys/<id>/rotate` |
| ۱۳ | پاسخ موفق | `{"id","name","key","prefix","scopes"}` (کلید جدید، یک‌بار) |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۱، ۴۰۳، ۴۰۴، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"NOT_FOUND","message":"api key not found"}}` |
| ۱۷ | اثر | کلید قدیمی فوراً بی‌اعتبار می‌شود؛ audit `api_key/rotated` |
| ۱۸ | امنیت | تمام سرویس‌های وابسته باید کلید جدید را دریافت کنند |
| ۱۹ | تست؟ | ندارد |
| ۲۰ | یادداشت | برای چرخش دوره‌ای کلیدهای CI توصیه می‌شود |

### ۱۰.۵.۴ `DELETE /api/v1/admin/api-keys/:id`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | حذف کلید API |
| ۲ | متد و مسیر | `DELETE /api/v1/admin/api-keys/:id` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` |
| ۶ | path | `:id` = UUID کلید |
| ۷ | query | — |
| ۸ | هدرها | — |
| ۹ | بدنه | — |
| ۱۰ | الزامی | `:id` |
| ۱۱ | فیلدها | — |
| ۱۲ | نمونه | `curl -X DELETE -b cookie.txt http://localhost:9000/api/v1/admin/api-keys/<id>` |
| ۱۳ | پاسخ موفق | بدنه ندارد |
| ۱۴ | وضعیت | **۲۰۴** |
| ۱۵ | خطاها | ۴۰۱، ۴۰۳، ۴۰۴، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"NOT_FOUND","message":"api key not found"}}` |
| ۱۷ | اثر | حذف؛ audit `api_key/deleted` |
| ۱۸ | امنیت | کلید فوراً از کار می‌افتد |
| ۱۹ | تست؟ | ندارد |
| ۲۰ | یادداشت | — |

### ۱۰.۶ Webhooks (۸) — admin

### ۱۰.۶.۱ `GET /api/v1/admin/webhooks`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | لیست webhookها |
| ۲ | متد و مسیر | `GET /api/v1/admin/webhooks` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` |
| ۶ | path | — |
| ۷ | query | `product_id`، `search`، `limit`، `offset` |
| ۸ | هدرها | — |
| ۹ | بدنه | — |
| ۱۰ | الزامی | — |
| ۱۱ | فیلدها | `secret` مخفی است |
| ۱۲ | نمونه | `curl -s -b cookie.txt 'http://localhost:9000/api/v1/admin/webhooks'` |
| ۱۳ | پاسخ موفق | `{"webhooks":[...],"total":n,"limit":20,"offset":0}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۱، ۴۰۳ `PRODUCT_SCOPE_MISMATCH`، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"PRODUCT_SCOPE_MISMATCH","message":"api key is scoped to another product"}}` |
| ۱۷ | اثر | فقط خواندن |
| ۱۸ | امنیت | — |
| ۱۹ | تست؟ | ندارد |
| ۲۰ | یادداشت | — |

### ۱۰.۶.۲ `POST /api/v1/admin/webhooks`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | ثبت webhook جدید |
| ۲ | متد و مسیر | `POST /api/v1/admin/webhooks` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` |
| ۶ | path | — |
| ۷ | query | — |
| ۸ | هدرها | `Content-Type: application/json` |
| ۹ | بدنه | `{"product_id","url","events":["..."],"secret"}` |
| ۱۰ | الزامی | `product_id`, `url`, `events` (حداقل یکی) |
| ۱۱ | فیلدها | `secret` خالی → خودکار تولید می‌شود؛ URL باید معتبر و غیر-SSRF باشد |
| ۱۲ | نمونه | `curl -X POST -b cookie.txt -H 'Content-Type: application/json' -d '{"product_id":"<id>","url":"https://example.com/hook","events":["license.created"]}' http://localhost:9000/api/v1/admin/webhooks` |
| ۱۳ | پاسخ موفق | `{"id","product_id","url","secret","events","active","created_at"}` |
| ۱۴ | وضعیت | **۲۰۱** |
| ۱۵ | خطاها | ۴۰۰ (`invalid URL`/SSRF/`at least one event type is required`)، ۴۰۱، ۴۰۳، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"BAD_REQUEST","message":"at least one event type is required"}}` |
| ۱۷ | اثر | درج؛ audit `webhook/created` |
| ۱۸ | امنیت | محافظت SSRF روی URL اعمال می‌شود |
| ۱۹ | تست؟ | **بله** (سطح store) |
| ۲۰ | یادداشت | `secret` فقط در پاسخ ساخت/چرخش کامل برمی‌گردد |

### ۱۰.۶.۳ `PUT /api/v1/admin/webhooks/:id`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | ویرایش webhook |
| ۲ | متد و مسیر | `PUT /api/v1/admin/webhooks/:id` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` |
| ۶ | path | `:id` = UUID webhook |
| ۷ | query | — |
| ۸ | هدرها | `Content-Type: application/json` |
| ۹ | بدنه | `{"url","events","active"}` (همه اختیاری) |
| ۱۰ | الزامی | — |
| ۱۱ | فیلدها | — |
| ۱۲ | نمونه | `curl -X PUT -b cookie.txt -H 'Content-Type: application/json' -d '{"active":false}' http://localhost:9000/api/v1/admin/webhooks/<id>` |
| ۱۳ | پاسخ موفق | `*model.Webhook` (`secret` با `json:"-"`) |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۰، ۴۰۱، ۴۰۳، ۴۰۴، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"NOT_FOUND","message":"webhook not found"}}` |
| ۱۷ | اثر | به‌روزرسانی؛ audit `webhook/updated` |
| ۱۸ | امنیت | — |
| ۱۹ | تست؟ | ندارد |
| ۲۰ | یادداشت | — |

### ۱۰.۶.۴ `DELETE /api/v1/admin/webhooks/:id`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | حذف webhook |
| ۲ | متد و مسیر | `DELETE /api/v1/admin/webhooks/:id` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` |
| ۶ | path | `:id` = UUID webhook |
| ۷ | query | — |
| ۸ | هدرها | — |
| ۹ | بدنه | — |
| ۱۰ | الزامی | `:id` |
| ۱۱ | فیلدها | — |
| ۱۲ | نمونه | `curl -X DELETE -b cookie.txt http://localhost:9000/api/v1/admin/webhooks/<id>` |
| ۱۳ | پاسخ موفق | بدنه ندارد |
| ۱۴ | وضعیت | **۲۰۴** |
| ۱۵ | خطاها | ۴۰۱، ۴۰۳، ۴۰۴، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"NOT_FOUND","message":"webhook not found"}}` |
| ۱۷ | اثر | حذف؛ audit `webhook/deleted` |
| ۱۸ | امنیت | — |
| ۱۹ | تست؟ | ندارد |
| ۲۰ | یادداشت | deliveryهای historic حذف می‌شوند |

### ۱۰.۶.۵ `GET /api/v1/admin/webhooks/:id/deliveries`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | لیست تحویل‌های webhook |
| ۲ | متد و مسیر | `GET /api/v1/admin/webhooks/:id/deliveries` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` |
| ۶ | path | `:id` = UUID webhook |
| ۷ | query | `status` (`pending\|delivered\|failed`)، `event`، `limit`، `offset` |
| ۸ | هدرها | — |
| ۹ | بدنه | — |
| ۱۰ | الزامی | `:id` |
| ۱۱ | فیلدها | — |
| ۱۲ | نمونه | `curl -s -b cookie.txt 'http://localhost:9000/api/v1/admin/webhooks/<id>/deliveries?status=failed'` |
| ۱۳ | پاسخ موفق | `{"deliveries":[...],"total":n,"limit":20,"offset":0}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۰، ۴۰۱، ۴۰۳، ۴۰۴، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"BAD_REQUEST","message":"invalid status"}}` |
| ۱۷ | اثر | فقط خواندن |
| ۱۸ | امنیت | payload ممکن است داده حساس داشته باشد |
| ۱۹ | تست؟ | ندارد |
| ۲۰ | یادداشت | — |

### ۱۰.۶.۶ `GET /api/v1/admin/webhooks/:id/deliveries/:delivery_id`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | جزئیات یک تحویل |
| ۲ | متد و مسیر | `GET /api/v1/admin/webhooks/:id/deliveries/:delivery_id` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` |
| ۶ | path | `:id` = UUID webhook، `:delivery_id` = UUID تحویل |
| ۷ | query | — |
| ۸ | هدرها | — |
| ۹ | بدنه | — |
| ۱۰ | الزامی | هر دو پارامتر |
| ۱۱ | فیلدها | — |
| ۱۲ | نمونه | `curl -s -b cookie.txt http://localhost:9000/api/v1/admin/webhooks/<id>/deliveries/<delivery_id>` |
| ۱۳ | پاسخ موفق | `*model.WebhookDelivery`: `id,webhook_id,event,payload,response_code,response_body,attempts,next_retry,status,created_at,delivered_at` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۱، ۴۰۳، ۴۰۴ (شامل mismatch webhook/delivery)، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"NOT_FOUND","message":"delivery not found for this webhook"}}` |
| ۱۷ | اثر | فقط خواندن |
| ۱۸ | امنیت | — |
| ۱۹ | تست؟ | ندارد |
| ۲۰ | یادداشت | — |

### ۱۰.۶.۷ `POST /api/v1/admin/webhooks/:id/deliveries/:delivery_id/resend`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | ارسال مجدد یک تحویل |
| ۲ | متد و مسیر | `POST /api/v1/admin/webhooks/:id/deliveries/:delivery_id/resend` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` |
| ۶ | path | `:id` = UUID webhook، `:delivery_id` = UUID تحویل |
| ۷ | query | — |
| ۸ | هدرها | — |
| ۹ | بدنه | — |
| ۱۰ | الزامی | هر دو پارامتر |
| ۱۱ | فیلدها | — |
| ۱۲ | نمونه | `curl -X POST -b cookie.txt http://localhost:9000/api/v1/admin/webhooks/<id>/deliveries/<delivery_id>/resend` |
| ۱۳ | پاسخ موفق | `*model.WebhookDelivery` جدید |
| ۱۴ | وضعیت | **۲۰۱** |
| ۱۵ | خطاها | ۴۰۱، ۴۰۳، ۴۰۴، ۴۰۹ `NOT_RESENDABLE`، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"NOT_RESENDABLE","message":"delivery cannot be resent"}}` |
| ۱۷ | اثر | redispatch + audit |
| ۱۸ | امنیت | — |
| ۱۹ | تست؟ | ندارد |
| ۲۰ | یادداشت | برای تحویل‌های `delivered` مجاز نیست |

### ۱۰.۶.۸ `POST /api/v1/admin/webhooks/:id/test`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | ارسال یک رویداد تستی |
| ۲ | متد و مسیر | `POST /api/v1/admin/webhooks/:id/test` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` |
| ۶ | path | `:id` = UUID webhook |
| ۷ | query | — |
| ۸ | هدرها | — |
| ۹ | بدنه | — |
| ۱۰ | الزامی | `:id` |
| ۱۱ | فیلدها | — |
| ۱۲ | نمونه | `curl -X POST -b cookie.txt http://localhost:9000/api/v1/admin/webhooks/<id>/test` |
| ۱۳ | پاسخ موفق | `{"status":"delivered\|pending\|failed","delivery_id":"...","response_code":200,"response_body":"..."}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۱، ۴۰۳، ۴۰۴، ۴۰۹ `WEBHOOK_INACTIVE`، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"WEBHOOK_INACTIVE","message":"webhook is inactive"}}` |
| ۱۷ | اثر | ارسال HTTP + ثبت delivery |
| ۱۸ | امنیت | — |
| ۱۹ | تست؟ | ندارد |
| ۲۰ | یادداشت | وضعیت `pending` یعنی در صف retry قرار گرفت |

### ۱۰.۷ Addons (۴) — admin

### ۱۰.۷.۱ `GET /api/v1/admin/addons`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | لیست addonهای محصول |
| ۲ | متد و مسیر | `GET /api/v1/admin/addons` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` |
| ۶ | path | — |
| ۷ | query | `product_id`، `search`، `limit`، `offset` |
| ۸ | هدرها | — |
| ۹ | بدنه | — |
| ۱۰ | الزامی | — |
| ۱۱ | فیلدها | — |
| ۱۲ | نمونه | `curl -s -b cookie.txt 'http://localhost:9000/api/v1/admin/addons?product_id=<id>'` |
| ۱۳ | پاسخ موفق | `{"addons":[...],"total":n,"limit":20,"offset":0}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۱، ۴۰۳، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"UNAUTHORIZED","message":"unauthorized"}}` |
| ۱۷ | اثر | فقط خواندن |
| ۱۸ | امنیت | — |
| ۱۹ | تست؟ | ندارد |
| ۲۰ | یادداشت | — |

### ۱۰.۷.۲ `POST /api/v1/admin/addons`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | ساخت addon |
| ۲ | متد و مسیر | `POST /api/v1/admin/addons` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` |
| ۶ | path | — |
| ۷ | query | — |
| ۸ | هدرها | `Content-Type: application/json` |
| ۹ | بدنه | `{"product_id","name","slug","description","feature","value_type","value","quota_period","quota_unit","sort_order"}` |
| ۱۰ | الزامی | `product_id`, `name`, `slug`, `feature`, `value_type`, `value` |
| ۱۱ | فیلدها | `value_type` ∈ `bool\|int\|string\|quota`؛ `quota_period` فقط برای quota |
| ۱۲ | نمونه | `curl -X POST -b cookie.txt -H 'Content-Type: application/json' -d '{"product_id":"<id>","name":"Extra Seats","slug":"extra-seats","feature":"seats","value_type":"int","value":"5"}' http://localhost:9000/api/v1/admin/addons` |
| ۱۳ | پاسخ موفق | `*model.Addon` |
| ۱۴ | وضعیت | **۲۰۱** |
| ۱۵ | خطاها | ۴۰۰، ۴۰۱، ۴۰۳، ۴۰۹ `DUPLICATE`، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"DUPLICATE","message":"addon slug already exists"}}` |
| ۱۷ | اثر | درج؛ audit `addon/created` |
| ۱۸ | امنیت | — |
| ۱۹ | تست؟ | ندارد |
| ۲۰ | یادداشت | — |

### ۱۰.۷.۳ `PUT /api/v1/admin/addons/:id`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | ویرایش addon |
| ۲ | متد و مسیر | `PUT /api/v1/admin/addons/:id` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` |
| ۶ | path | `:id` = UUID addon |
| ۷ | query | — |
| ۸ | هدرها | `Content-Type: application/json` |
| ۹ | بدنه | `{"name","slug","description","feature","value_type","value","quota_period","quota_unit","active","sort_order"}` (همه اختیاری) |
| ۱۰ | الزامی | — |
| ۱۱ | فیلدها | فیلدها pointer هستند |
| ۱۲ | نمونه | `curl -X PUT -b cookie.txt -H 'Content-Type: application/json' -d '{"active":false}' http://localhost:9000/api/v1/admin/addons/<id>` |
| ۱۳ | پاسخ موفق | `*model.Addon` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۰، ۴۰۱، ۴۰۳، ۴۰۴، ۴۰۹ `DUPLICATE`، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"NOT_FOUND","message":"addon not found"}}` |
| ۱۷ | اثر | به‌روزرسانی؛ audit `addon/updated` |
| ۱۸ | امنیت | — |
| ۱۹ | تست؟ | ندارد |
| ۲۰ | یادداشت | — |

### ۱۰.۷.۴ `DELETE /api/v1/admin/addons/:id`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | حذف addon |
| ۲ | متد و مسیر | `DELETE /api/v1/admin/addons/:id` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` |
| ۶ | path | `:id` = UUID addon |
| ۷ | query | — |
| ۸ | هدرها | — |
| ۹ | بدنه | — |
| ۱۰ | الزامی | `:id` |
| ۱۱ | فیلدها | — |
| ۱۲ | نمونه | `curl -X DELETE -b cookie.txt http://localhost:9000/api/v1/admin/addons/<id>` |
| ۱۳ | پاسخ موفق | بدنه ندارد |
| ۱۴ | وضعیت | **۲۰۴** |
| ۱۵ | خطاها | ۴۰۱، ۴۰۳، ۴۰۴، ۴۰۹ (اگر به لایسنس متصل باشد)، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"CONFLICT","message":"addon is in use"}}` |
| ۱۷ | اثر | حذف؛ audit `addon/deleted` |
| ۱۸ | امنیت | — |
| ۱۹ | تست؟ | ندارد |
| ۲۰ | یادداشت | — |

### ۱۰.۸ Settings (۴) — admin

### ۱۰.۸.۱ `GET /api/v1/admin/settings`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | خواندن تنظیمات سایت |
| ۲ | متد و مسیر | `GET /api/v1/admin/settings` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` |
| ۶ | path | — |
| ۷ | query | — |
| ۸ | هدرها | — |
| ۹ | بدنه | — |
| ۱۰ | الزامی | — |
| ۱۱ | فیلدها | رازها فقط به‌صورت «تنظیم‌شده/نشده» برمی‌گردند |
| ۱۲ | نمونه | `curl -s -b cookie.txt http://localhost:9000/api/v1/admin/settings` |
| ۱۳ | پاسخ موفق | `{"settings":{key:value},"secrets_set":{key:bool},"email":{"configured","provider","source","host","from"}}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۱، ۴۰۳، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"UNAUTHORIZED","message":"unauthorized"}}` |
| ۱۷ | اثر | فقط خواندن |
| ۱۸ | امنیت | هیچ راز خامی برنمی‌گردد |
| ۱۹ | تست؟ | **جزئی** — `settings_test.go` |
| ۲۰ | یادداشت | — |

### ۱۰.۸.۲ `PUT /api/v1/admin/settings`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | ذخیرهٔ تنظیمات |
| ۲ | متد و مسیر | `PUT /api/v1/admin/settings` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` |
| ۶ | path | — |
| ۷ | query | — |
| ۸ | هدرها | `Content-Type: application/json` |
| ۹ | بدنه | `{"settings":{"<key>":"<value>",...}}` |
| ۱۰ | الزامی | `settings` |
| ۱۱ | فیلدها | فقط کلیدهای مجاز؛ `signup_mode` ∈ `open\|licensed_only`؛ `email_provider` ∈ `smtp\|cloudflare`؛ toggleها `true/false`؛ قالب‌های ایمیل باید رندر شوند؛ راز خالی = بدون تغییر |
| ۱۲ | نمونه | `curl -X PUT -b cookie.txt -H 'Content-Type: application/json' -d '{"settings":{"signup_mode":"open"}}' http://localhost:9000/api/v1/admin/settings` |
| ۱۳ | پاسخ موفق | `{"status":"saved"}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۰ (`unknown setting: <key>`/`invalid value for <key>: <val>`)، ۴۰۱، ۴۰۳، ۵۰۳ `ENCRYPTION_NOT_CONFIGURED`، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"BAD_REQUEST","message":"unknown setting: foo"}}` |
| ۱۷ | اثر | audit `settings/updated` |
| ۱۸ | امنیت | رازها رمزنگاری‌شده ذخیره می‌شوند؛ `ENCRYPTION_NOT_CONFIGURED` یعنی کلید رمزنگاری تنظیم نیست |
| ۱۹ | تست؟ | **بله** — `TestFeedURLTTLBoundIsValidated` |
| ۲۰ | یادداشت | — |

### ۱۰.۸.۳ `DELETE /api/v1/admin/settings/secrets/:key`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | پاک‌کردن یک راز ذخیره‌شده |
| ۲ | متد و مسیر | `DELETE /api/v1/admin/settings/secrets/:key` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` |
| ۶ | path | `:key` ∈ `cloudflare_api_token`/`smtp_password` |
| ۷ | query | — |
| ۸ | هدرها | — |
| ۹ | بدنه | — |
| ۱۰ | الزامی | `:key` |
| ۱۱ | فیلدها | — |
| ۱۲ | نمونه | `curl -X DELETE -b cookie.txt http://localhost:9000/api/v1/admin/settings/secrets/smtp_password` |
| ۱۳ | پاسخ موفق | `{"status":"cleared","key":"smtp_password"}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۱، ۴۰۳، ۴۰۴، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"NOT_FOUND","message":"unknown secret"}}` |
| ۱۷ | اثر | حذف راز؛ audit `settings/secret_cleared` |
| ۱۸ | امنیت | پس از پاک‌شدن، سرویس ایمیل ممکن است از کار بیفتد |
| ۱۹ | تست؟ | ندارد |
| ۲۰ | یادداشت | — |

### ۱۰.۸.۴ `POST /api/v1/admin/settings/test-email`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | ارسال ایمیل تستی |
| ۲ | متد و مسیر | `POST /api/v1/admin/settings/test-email` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` |
| ۶ | path | — |
| ۷ | query | — |
| ۸ | هدرها | `Content-Type: application/json` |
| ۹ | بدنه | اختیاری: `{"to":"you@example.com"}` |
| ۱۰ | الزامی | — |
| ۱۱ | فیلدها | `to` پیش‌فرض ایمیل کاربر جاری است |
| ۱۲ | نمونه | `curl -X POST -b cookie.txt -H 'Content-Type: application/json' -d '{"to":"ops@example.com"}' http://localhost:9000/api/v1/admin/settings/test-email` |
| ۱۳ | پاسخ موفق | `{"status":"sent","to":"ops@example.com"}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۰، ۴۰۱، ۴۰۳، ۵۰۳ `EMAIL_NOT_CONFIGURED`، ۵۰۲ `EMAIL_SEND_FAILED`، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"EMAIL_NOT_CONFIGURED","message":"email is not configured"}}` |
| ۱۷ | اثر | ارسال واقعی ایمیل |
| ۱۸ | امنیت | — |
| ۱۹ | تست؟ | ندارد |
| ۲۰ | یادداشت | — |

### ۱۰.۹ System (۴) — admin

### ۱۰.۹.۱ `POST /api/v1/admin/system/run-expiry-checks`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | اجرای دستی چرخهٔ انقضا/dunning/تمدید |
| ۲ | متد و مسیر | `POST /api/v1/admin/system/run-expiry-checks` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` |
| ۶ | path | — |
| ۷ | query | — |
| ۸ | هدرها | — |
| ۹ | بدنه | — |
| ۱۰ | الزامی | — |
| ۱۱ | فیلدها | — |
| ۱۲ | نمونه | `curl -X POST -b cookie.txt http://localhost:9000/api/v1/admin/system/run-expiry-checks` |
| ۱۳ | پاسخ موفق | `{"status":"ran"}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۱، ۴۰۳، ۵۰۳ `EXPIRY_NOT_AVAILABLE`، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"EXPIRY_NOT_AVAILABLE","message":"expiry service is not available"}}` |
| ۱۷ | اثر | تغییر وضعیت لایسنس‌های منقضی؛ ارسال ایمیل/تمدید |
| ۱۸ | امنیت | عملیات انبوه؛ در ساعات کم‌ترافیک اجرا شود |
| ۱۹ | تست؟ | **بله** (سطح service) |
| ۲۰ | یادداشت | معمولاً به‌صورت دوره‌ای هم اجرا می‌شود |

### ۱۰.۹.۲ `POST /api/v1/admin/system/run-metered-sync`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | تخلیهٔ صف مصرف Metered در Stripe |
| ۲ | متد و مسیر | `POST /api/v1/admin/system/run-metered-sync` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` |
| ۶ | path | — |
| ۷ | query | — |
| ۸ | هدرها | — |
| ۹ | بدنه | — |
| ۱۰ | الزامی | — |
| ۱۱ | فیلدها | — |
| ۱۲ | نمونه | `curl -X POST -b cookie.txt http://localhost:9000/api/v1/admin/system/run-metered-sync` |
| ۱۳ | پاسخ موفق | `{"pushed":<int>}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۱، ۴۰۳، ۵۰۳ `METERED_SYNC_NOT_AVAILABLE`، ۵۰۲ `STRIPE_UNAVAILABLE`، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"METERED_SYNC_NOT_AVAILABLE","message":"metered sync is not configured"}}` |
| ۱۷ | اثر | `Metered.RunOnce` با محدودیت ۲۰۰ رکورد |
| ۱۸ | امنیت | — |
| ۱۹ | تست؟ | **بله** (سطح payment) |
| ۲۰ | یادداشت | خروجی `pushed` تعداد رکوردهای ارسال‌شده است |

### ۱۰.۹.۳ `GET /api/v1/admin/system/update-check`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | بررسی وجود نسخهٔ جدید Keygate |
| ۲ | متد و مسیر | `GET /api/v1/admin/system/update-check` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` |
| ۶ | path | — |
| ۷ | query | — |
| ۸ | هدرها | — |
| ۹ | بدنه | — |
| ۱۰ | الزامی | — |
| ۱۱ | فیلدها | — |
| ۱۲ | نمونه | `curl -s -b cookie.txt http://localhost:9000/api/v1/admin/system/update-check` |
| ۱۳ | پاسخ موفق | `updateInfo`: `available,latest_version,current_version,release_url?,release_date?,changelog?,update_command?,checked_at,error?` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۱، ۴۰۳، ۴۲۹، ۵۰۰ (خطای شبکه در `error` گزارش می‌شود، نه به‌صورت HTTP) |
| ۱۶ | نمونهٔ خطا | `{"success":true,"data":{"available":false,"error":"github: rate limited"}}` |
| ۱۷ | اثر | فراخوانی GitHub API؛ cache یک‌ساعته |
| ۱۸ | امنیت | — |
| ۱۹ | تست؟ | ندارد |
| ۲۰ | یادداشت | نبود شبکه باعث خطای ۵۰۰ نمی‌شود |

### ۱۰.۹.۴ `GET /api/v1/admin/system/migrations`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | وضعیت migrationهای دیتابیس |
| ۲ | متد و مسیر | `GET /api/v1/admin/system/migrations` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` |
| ۶ | path | — |
| ۷ | query | — |
| ۸ | هدرها | — |
| ۹ | بدنه | — |
| ۱۰ | الزامی | — |
| ۱۱ | فیلدها | — |
| ۱۲ | نمونه | `curl -s -b cookie.txt http://localhost:9000/api/v1/admin/system/migrations` |
| ۱۳ | پاسخ موفق | `{"migrations":[{"version","applied_at"}...]}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۱، ۴۰۳، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"INTERNAL_ERROR","message":"failed to read migrations"}}` |
| ۱۷ | اثر | فقط خواندن |
| ۱۸ | امنیت | — |
| ۱۹ | تست؟ | ندارد |
| ۲۰ | یادداشت | — |

### ۱۰.۱۰ Email Templates (۱) — admin

### ۱۰.۱۰.۱ `GET /api/v1/admin/email-templates`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | خواندن قالب‌های ایمیل (سفارشی و پیش‌فرض) |
| ۲ | متد و مسیر | `GET /api/v1/admin/email-templates` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` |
| ۶ | path | — |
| ۷ | query | — |
| ۸ | هدرها | — |
| ۹ | بدنه | — |
| ۱۰ | الزامی | — |
| ۱۱ | فیلدها | — |
| ۱۲ | نمونه | `curl -s -b cookie.txt http://localhost:9000/api/v1/admin/email-templates` |
| ۱۳ | پاسخ موفق | `{"templates":{"<key>":{"custom":"<str>","default":"<str>"}}}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۱، ۴۰۳، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"UNAUTHORIZED","message":"unauthorized"}}` |
| ۱۷ | اثر | فقط خواندن |
| ۱۸ | امنیت | — |
| ۱۹ | تست؟ | ندارد |
| ۲۰ | یادداشت | ویرایش قالب‌ها از طریق `PUT /admin/settings` انجام می‌شود |

### ۱۰.۱۱ Team (۳) — admin

### ۱۰.۱۱.۱ `GET /api/v1/admin/team`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | لیست اعضای تیم ادمین |
| ۲ | متد و مسیر | `GET /api/v1/admin/team` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` |
| ۶ | path | — |
| ۷ | query | — |
| ۸ | هدرها | — |
| ۹ | بدنه | — |
| ۱۰ | الزامی | — |
| ۱۱ | فیلدها | — |
| ۱۲ | نمونه | `curl -s -b cookie.txt http://localhost:9000/api/v1/admin/team` |
| ۱۳ | پاسخ موفق | `{"members":[...],"total":n}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۱، ۴۰۳، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"UNAUTHORIZED","message":"unauthorized"}}` |
| ۱۷ | اثر | فقط خواندن |
| ۱۸ | امنیت | — |
| ۱۹ | تست؟ | ندارد |
| ۲۰ | یادداشت | — |

### ۱۰.۱۱.۲ `POST /api/v1/admin/team`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | دعوت عضو جدید به تیم |
| ۲ | متد و مسیر | `POST /api/v1/admin/team` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` |
| ۶ | path | — |
| ۷ | query | — |
| ۸ | هدرها | `Content-Type: application/json` |
| ۹ | بدنه | `{"email":"...","role":"admin"}` |
| ۱۰ | الزامی | `email` |
| ۱۱ | فیلدها | `role` ∈ `admin\|owner` (پیش‌فرض `admin`)؛ فقط actor با نقش `owner` مجاز است |
| ۱۲ | نمونه | `curl -X POST -b cookie.txt -H 'Content-Type: application/json' -d '{"email":"ops@example.com","role":"admin"}' http://localhost:9000/api/v1/admin/team` |
| ۱۳ | پاسخ موفق | `*model.User` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۰، ۴۰۱، ۴۰۳ (`only the owner can manage team members`)، ۴۰۹ `DUPLICATE`، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"FORBIDDEN","message":"only the owner can manage team members"}}` |
| ۱۷ | اثر | audit `team/member_invited` + ایمیل `SendAdminInvite` |
| ۱۸ | امنیت | نمی‌توان خود را دعوت کرد؛ نقش `owner` قدرت کامل می‌دهد |
| ۱۹ | تست؟ | ندارد |
| ۲۰ | یادداشت | دعوت تا زمان accept معتبر است |

### ۱۰.۱۱.۳ `DELETE /api/v1/admin/team/:id`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | حذف عضو تیم |
| ۲ | متد و مسیر | `DELETE /api/v1/admin/team/:id` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` |
| ۶ | path | `:id` = UUID کاربر |
| ۷ | query | — |
| ۸ | هدرها | — |
| ۹ | بدنه | — |
| ۱۰ | الزامی | `:id` |
| ۱۱ | فیلدها | — |
| ۱۲ | نمونه | `curl -X DELETE -b cookie.txt http://localhost:9000/api/v1/admin/team/<id>` |
| ۱۳ | پاسخ موفق | `{"status":"removed"}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۰ (`cannot remove the last owner`)، ۴۰۱، ۴۰۳، ۴۰۴، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"BAD_REQUEST","message":"cannot remove the last owner"}}` |
| ۱۷ | اثر | حذف کاربر؛ audit `team/member_removed` |
| ۱۸ | امنیت | آخرین owner محافظت می‌شود؛ حذف خود مجاز نیست |
| ۱۹ | تست؟ | ندارد |
| ۲۰ | یادداشت | — |

### ۱۰.۱۲ Analytics (۶) — admin

### ۱۰.۱۲.۱ `GET /api/v1/admin/analytics`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | سری زمانی snapshotهای تحلیلی |
| ۲ | متد و مسیر | `GET /api/v1/admin/analytics` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` |
| ۶ | path | — |
| ۷ | query | `product_id`، `granularity` (`weekly\|monthly`)، `from`، `to` (`2006-01-02`) |
| ۸ | هدرها | — |
| ۹ | بدنه | — |
| ۱۰ | الزامی | — |
| ۱۱ | فیلدها | بازهٔ پیش‌فرض: یک ماه اخیر |
| ۱۲ | نمونه | `curl -s -b cookie.txt 'http://localhost:9000/api/v1/admin/analytics?granularity=monthly'` |
| ۱۳ | پاسخ موفق | `{"snapshots":[...],"granularity":"monthly"}` یا `{"snapshots":[...]}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۰ (`invalid granularity`/`invalid date`)، ۴۰۱، ۴۰۳، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"BAD_REQUEST","message":"invalid granularity"}}` |
| ۱۷ | اثر | فقط خواندن |
| ۱۸ | امنیت | — |
| ۱۹ | تست؟ | ندارد |
| ۲۰ | یادداشت | — |

### ۱۰.۱۲.۲ `GET /api/v1/admin/analytics/summary`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | خلاصهٔ آماری لایسنس‌ها |
| ۲ | متد و مسیر | `GET /api/v1/admin/analytics/summary` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` |
| ۶ | path | — |
| ۷ | query | `product_id`، `plan_id`، `license_type`، `status`، `from`، `to` |
| ۸ | هدرها | — |
| ۹ | بدنه | — |
| ۱۰ | الزامی | — |
| ۱۱ | فیلدها | — |
| ۱۲ | نمونه | `curl -s -b cookie.txt 'http://localhost:9000/api/v1/admin/analytics/summary?product_id=<id>'` |
| ۱۳ | پاسخ موفق | `store.AnalyticsSummary`: `total_licenses`,`active_licenses`,`trialing_licenses`,`expired_licenses`,`canceled_licenses`,`suspended_licenses`,`revoked_licenses`,`past_due_licenses`,`total_activations`,`total_seats`,`avg_activations_per_license` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۰، ۴۰۱، ۴۰۳، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"BAD_REQUEST","message":"invalid date"}}` |
| ۱۷ | اثر | فقط خواندن |
| ۱۸ | امنیت | — |
| ۱۹ | تست؟ | ندارد |
| ۲۰ | یادداشت | — |

### ۱۰.۱۲.۳ `GET /api/v1/admin/analytics/breakdown`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | تفکیک تعداد بر اساس یک بُعد |
| ۲ | متد و مسیر | `GET /api/v1/admin/analytics/breakdown` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` |
| ۶ | path | — |
| ۷ | query | `dimension` (`status\|plan\|license_type`) + فیلترها |
| ۸ | هدرها | — |
| ۹ | بدنه | — |
| ۱۰ | الزامی | `dimension` |
| ۱۱ | فیلدها | — |
| ۱۲ | نمونه | `curl -s -b cookie.txt 'http://localhost:9000/api/v1/admin/analytics/breakdown?dimension=status'` |
| ۱۳ | پاسخ موفق | `{"items":[{"key","label","count"}...]}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۰ (`dimension must be status, plan, or license_type`)، ۴۰۱، ۴۰۳، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"BAD_REQUEST","message":"dimension must be status, plan, or license_type"}}` |
| ۱۷ | اثر | فقط خواندن |
| ۱۸ | امنیت | — |
| ۱۹ | تست؟ | ندارد |
| ۲۰ | یادداشت | — |

### ۱۰.۱۲.۴ `GET /api/v1/admin/analytics/usage-top`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | پراستفاده‌ترین featureها |
| ۲ | متد و مسیر | `GET /api/v1/admin/analytics/usage-top` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` |
| ۶ | path | — |
| ۷ | query | `product_id`، `from`، `to`، `limit` (پیش‌فرض ۱۰) |
| ۸ | هدرها | — |
| ۹ | بدنه | — |
| ۱۰ | الزامی | — |
| ۱۱ | فیلدها | — |
| ۱۲ | نمونه | `curl -s -b cookie.txt 'http://localhost:9000/api/v1/admin/analytics/usage-top?limit=10'` |
| ۱۳ | پاسخ موفق | `{"features":[{"feature","total_usage","unique_users"}...]}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۰، ۴۰۱، ۴۰۳، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"BAD_REQUEST","message":"invalid date"}}` |
| ۱۷ | اثر | فقط خواندن |
| ۱۸ | امنیت | — |
| ۱۹ | تست؟ | ندارد |
| ۲۰ | یادداشت | — |

### ۱۰.۱۲.۵ `GET /api/v1/admin/analytics/activation-trend`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | روند فعال‌سازی در بازهٔ زمانی |
| ۲ | متد و مسیر | `GET /api/v1/admin/analytics/activation-trend` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` |
| ۶ | path | — |
| ۷ | query | `product_id`، `from`، `to` |
| ۸ | هدرها | — |
| ۹ | بدنه | — |
| ۱۰ | الزامی | — |
| ۱۱ | فیلدها | — |
| ۱۲ | نمونه | `curl -s -b cookie.txt 'http://localhost:9000/api/v1/admin/analytics/activation-trend'` |
| ۱۳ | پاسخ موفق | `{"trend":[{"date","count"}...]}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۰، ۴۰۱، ۴۰۳، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"BAD_REQUEST","message":"invalid date"}}` |
| ۱۷ | اثر | فقط خواندن |
| ۱۸ | امنیت | — |
| ۱۹ | تست؟ | ندارد |
| ۲۰ | یادداشت | — |

### ۱۰.۱۲.۶ `GET /api/v1/admin/analytics/insights`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | بینش‌های تحلیلی (رشد، سن، کاربران برتر، نگهداشت) |
| ۲ | متد و مسیر | `GET /api/v1/admin/analytics/insights` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` |
| ۶ | path | — |
| ۷ | query | `product_id`، `top_limit` (۱۰)، `months` (۶)، `activity_limit` (۲۰) + فیلترها |
| ۸ | هدرها | — |
| ۹ | بدنه | — |
| ۱۰ | الزامی | — |
| ۱۱ | فیلدها | — |
| ۱۲ | نمونه | `curl -s -b cookie.txt 'http://localhost:9000/api/v1/admin/analytics/insights'` |
| ۱۳ | پاسخ موفق | `{"growth":{...},"age_distribution":[...],"top_users":[...],"retention":[...],"recent_activity":[...]}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۰، ۴۰۱، ۴۰۳، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"BAD_REQUEST","message":"invalid date"}}` |
| ۱۷ | اثر | فقط خواندن |
| ۱۸ | امنیت | — |
| ۱۹ | تست؟ | ندارد |
| ۲۰ | یادداشت | — |

### ۱۰.۱۳ Audit Logs (۱) — admin

### ۱۰.۱۳.۱ `GET /api/v1/admin/audit-logs`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | لیست لاگ‌های audit |
| ۲ | متد و مسیر | `GET /api/v1/admin/audit-logs` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` |
| ۶ | path | — |
| ۷ | query | `entity`، `entity_id`، `product_id`، `limit`، `offset` |
| ۸ | هدرها | — |
| ۹ | بدنه | — |
| ۱۰ | الزامی | — |
| ۱۱ | فیلدها | — |
| ۱۲ | نمونه | `curl -s -b cookie.txt 'http://localhost:9000/api/v1/admin/audit-logs?entity=license'` |
| ۱۳ | پاسخ موفق | `{"audit_logs":[{"id","actor","action","entity","entity_id","metadata","ip","created_at"}...],"total":n,"limit":50,"offset":0}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۱، ۴۰۳، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"UNAUTHORIZED","message":"unauthorized"}}` |
| ۱۷ | اثر | فقط خواندن |
| ۱۸ | امنیت | لاگ‌ها ممکن است شامل IP و متادیتای حساس باشند |
| ۱۹ | تست؟ | **بله** (سطح store) |
| ۲۰ | یادداشت | — |

### ۱۰.۱۴ Users / Customers (۲) — admin

### ۱۰.۱۴.۱ `GET /api/v1/admin/users`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | لیست کاربران/مشتریان |
| ۲ | متد و مسیر | `GET /api/v1/admin/users` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` |
| ۶ | path | — |
| ۷ | query | `search`، `limit`، `offset` |
| ۸ | هدرها | — |
| ۹ | بدنه | — |
| ۱۰ | الزامی | — |
| ۱۱ | فیلدها | — |
| ۱۲ | نمونه | `curl -s -b cookie.txt 'http://localhost:9000/api/v1/admin/users?search=acme'` |
| ۱۳ | پاسخ موفق | `{"users":[...],"total":n,"limit":20,"offset":0}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۱، ۴۰۳، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"UNAUTHORIZED","message":"unauthorized"}}` |
| ۱۷ | اثر | فقط خواندن |
| ۱۸ | امنیت | — |
| ۱۹ | تست؟ | ندارد |
| ۲۰ | یادداشت | — |

### ۱۰.۱۴.۲ `GET /api/v1/admin/users/:id`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | جزئیات کامل یک کاربر |
| ۲ | متد و مسیر | `GET /api/v1/admin/users/:id` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` |
| ۶ | path | `:id` = UUID کاربر |
| ۷ | query | — |
| ۸ | هدرها | — |
| ۹ | بدنه | — |
| ۱۰ | الزامی | `:id` |
| ۱۱ | فیلدها | — |
| ۱۲ | نمونه | `curl -s -b cookie.txt http://localhost:9000/api/v1/admin/users/<id>` |
| ۱۳ | پاسخ موفق | `store.UserDetail`: `user`,`licenses`,`subscriptions`,`total_usage`,`active_seats`,`activations`,`recent_audit_logs` + `license_key_hints` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۱، ۴۰۳، ۴۰۴، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"NOT_FOUND","message":"user not found"}}` |
| ۱۷ | اثر | فقط خواندن |
| ۱۸ | امنیت | `license_key_hints` فقط ۴ کاراکتر آخر را نشان می‌دهد |
| ۱۹ | تست؟ | ندارد |
| ۲۰ | یادداشت | — |

### ۱۰.۱۵ Releases (۱۲) — گروه `relWrite`

### ۱۰.۱۵.۱ `GET /api/v1/admin/releases`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | لیست ریلیزها |
| ۲ | متد و مسیر | `GET /api/v1/admin/releases` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` یا `releases:write` |
| ۶ | path | — |
| ۷ | query | `channel` (`stable\|beta\|alpha\|dev`)، `status` (`draft\|published\|yanked`)، `product_id`، `limit`، `offset` |
| ۸ | هدرها | — |
| ۹ | بدنه | — |
| ۱۰ | الزامی | — |
| ۱۱ | فیلدها | مقدار نامعتبر channel/status → ۴۰۰ |
| ۱۲ | نمونه | `curl -s -b cookie.txt 'http://localhost:9000/api/v1/admin/releases?channel=stable'` |
| ۱۳ | پاسخ موفق | `{"releases":[...],"total":n,"limit":20,"offset":0}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۰ (`channel must be…`/`status must be…`)، ۴۰۱، ۴۰۳، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"BAD_REQUEST","message":"channel must be stable, beta, alpha, or dev"}}` |
| ۱۷ | اثر | فقط خواندن |
| ۱۸ | امنیت | — |
| ۱۹ | تست؟ | **بله** (سطح store/service) |
| ۲۰ | یادداشت | — |

### ۱۰.۱۵.۲ `GET /api/v1/admin/releases/:id`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | دریافت یک ریلیز با artifactها |
| ۲ | متد و مسیر | `GET /api/v1/admin/releases/:id` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` یا `releases:write` |
| ۶ | path | `:id` = UUID ریلیز |
| ۷ | query | — |
| ۸ | هدرها | — |
| ۹ | بدنه | — |
| ۱۰ | الزامی | `:id` |
| ۱۱ | فیلدها | — |
| ۱۲ | نمونه | `curl -s -b cookie.txt http://localhost:9000/api/v1/admin/releases/<id>` |
| ۱۳ | پاسخ موفق | `model.Release`: `id,product_id,version,channel,name,release_notes,status,yanked_reason,published_at,yanked_at,created_at,updated_at,product,artifacts` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۱، ۴۰۳، ۴۰۴، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"NOT_FOUND","message":"release not found"}}` |
| ۱۷ | اثر | فقط خواندن |
| ۱۸ | امنیت | — |
| ۱۹ | تست؟ | **بله** (سطح store) |
| ۲۰ | یادداشت | — |

### ۱۰.۱۵.۳ `POST /api/v1/admin/releases`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | ساخت ریلیز draft |
| ۲ | متد و مسیر | `POST /api/v1/admin/releases` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` یا `releases:write` |
| ۶ | path | — |
| ۷ | query | — |
| ۸ | هدرها | `Content-Type: application/json` |
| ۹ | بدنه | `{"product_id","version","channel","name","release_notes"}` |
| ۱۰ | الزامی | `product_id`, `version` |
| ۱۱ | فیلدها | `version` semver؛ `channel` پیش‌فرض `stable`؛ `name`/`release_notes` با محدودیت طول |
| ۱۲ | نمونه | `curl -X POST -b cookie.txt -H 'Content-Type: application/json' -d '{"product_id":"<id>","version":"1.2.0","channel":"stable","name":"1.2.0"}' http://localhost:9000/api/v1/admin/releases` |
| ۱۳ | پاسخ موفق | `model.Release` |
| ۱۴ | وضعیت | **۲۰۱** |
| ۱۵ | خطاها | ۴۰۰ (`product_id and version are required`/`name too long`/`release_notes too long`)، ۴۰۴ (`product not found`)، ۴۰۰ `INCOMPATIBLE_PRODUCT_TYPE`، ۴۰۳، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"BAD_REQUEST","message":"product_id and version are required"}}` |
| ۱۷ | اثر | audit `release/draft_created` |
| ۱۸ | امنیت | — |
| ۱۹ | تست؟ | **بله** (سطح store) |
| ۲۰ | یادداشت | ریلیز در وضعیت `draft` ساخته می‌شود |

### ۱۰.۱۵.۴ `PATCH /api/v1/admin/releases/:id`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | ویرایش نام/یادداشت ریلیز |
| ۲ | متد و مسیر | `PATCH /api/v1/admin/releases/:id` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` یا `releases:write` |
| ۶ | path | `:id` = UUID ریلیز |
| ۷ | query | — |
| ۸ | هدرها | `Content-Type: application/json` |
| ۹ | بدنه | `{"name","release_notes"}` |
| ۱۰ | الزامی | حداقل یکی از `name`، `release_notes` |
| ۱۱ | فیلدها | — |
| ۱۲ | نمونه | `curl -X PATCH -b cookie.txt -H 'Content-Type: application/json' -d '{"release_notes":"Fixes"}' http://localhost:9000/api/v1/admin/releases/<id>` |
| ۱۳ | پاسخ موفق | `model.Release` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۰ (`at least one of name, release_notes must be provided`)، ۴۰۱، ۴۰۳، ۴۰۴، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"BAD_REQUEST","message":"at least one of name, release_notes must be provided"}}` |
| ۱۷ | اثر | audit `release/updated` |
| ۱۸ | امنیت | — |
| ۱۹ | تست؟ | **بله** (سطح store) |
| ۲۰ | یادداشت | — |

### ۱۰.۱۵.۵ `PATCH /api/v1/admin/releases/:id/notes`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | شیم سازگاری برای ویرایش یادداشت |
| ۲ | متد و مسیر | `PATCH /api/v1/admin/releases/:id/notes` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` یا `releases:write` |
| ۶ | path | `:id` = UUID ریلیز |
| ۷ | query | — |
| ۸ | هدرها | `Content-Type: application/json` |
| ۹ | بدنه | `{"name","release_notes"}` |
| ۱۰ | الزامی | حداقل یکی |
| ۱۱ | فیلدها | همان handler مسیر ۱۰.۱۵.۴ |
| ۱۲ | نمونه | `curl -X PATCH -b cookie.txt -H 'Content-Type: application/json' -d '{"release_notes":"Fixes"}' http://localhost:9000/api/v1/admin/releases/<id>/notes` |
| ۱۳ | پاسخ موفق | `model.Release` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | مانند ۱۰.۱۵.۴ |
| ۱۶ | نمونهٔ خطا | مانند ۱۰.۱۵.۴ |
| ۱۷ | اثر | audit `release/updated` |
| ۱۸ | امنیت | — |
| ۱۹ | تست؟ | **بله** (سطح store) |
| ۲۰ | یادداشت | **منسوخ**؛ پس از یک چرخه حذف می‌شود |

### ۱۰.۱۵.۶ `DELETE /api/v1/admin/releases/:id`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | حذف ریلیز draft |
| ۲ | متد و مسیر | `DELETE /api/v1/admin/releases/:id` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` یا `releases:write` |
| ۶ | path | `:id` = UUID ریلیز |
| ۷ | query | — |
| ۸ | هدرها | — |
| ۹ | بدنه | — |
| ۱۰ | الزامی | `:id` |
| ۱۱ | فیلدها | فقط ریلیز `draft` قابل حذف است |
| ۱۲ | نمونه | `curl -X DELETE -b cookie.txt http://localhost:9000/api/v1/admin/releases/<id>` |
| ۱۳ | پاسخ موفق | `{"status":"deleted"}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۱، ۴۰۳، ۴۰۴، ۴۰۹ (ریلیز منتشرشده)، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"CONFLICT","message":"only drafts can be deleted"}}` |
| ۱۷ | اثر | حذف آبشاری artifactها؛ audit `release/deleted` |
| ۱۸ | امنیت | — |
| ۱۹ | تست؟ | **بله** (سطح store) |
| ۲۰ | یادداشت | برای حذف ریلیز منتشرشده از `yank` استفاده کنید |

### ۱۰.۱۵.۷ `POST /api/v1/admin/releases/:id/artifacts`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | ثبت artifact و گرفتن URL آپلود |
| ۲ | متد و مسیر | `POST /api/v1/admin/releases/:id/artifacts` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` یا `releases:write` |
| ۶ | path | `:id` = UUID ریلیز |
| ۷ | query | — |
| ۸ | هدرها | `Content-Type: application/json` |
| ۹ | بدنه | `{"platform","content_type","expected_size","filename"}` |
| ۱۰ | الزامی | `platform` |
| ۱۱ | فیلدها | `platform` مثل `darwin-arm64`؛ `expected_size` int64 با محدودهٔ مجاز |
| ۱۲ | نمونه | `curl -X POST -b cookie.txt -H 'Content-Type: application/json' -d '{"platform":"darwin-arm64","expected_size":12345678,"filename":"Acme.dmg"}' http://localhost:9000/api/v1/admin/releases/<id>/artifacts` |
| ۱۳ | پاسخ موفق | `{"artifact":{...},"upload_url":"<presigned PUT>","expires_at":"..."}` |
| ۱۴ | وضعیت | **۲۰۱** |
| ۱۵ | خطاها | ۴۰۰ (`platform is required`/`expected_size out of range`/…)، ۴۰۱، ۴۰۳، ۴۰۴، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"BAD_REQUEST","message":"platform is required"}}` |
| ۱۷ | اثر | رکورد artifact + presigned PUT؛ audit `release_artifact/added` |
| ۱۸ | امنیت | `upload_url` کوتاه‌عمر است؛ آن را لاگ نکنید |
| ۱۹ | تست؟ | **بله** (سطح store/service) |
| ۲۰ | یادداشت | آپلود با PUT مستقیم به storage انجام می‌شود |

### ۱۰.۱۵.۸ `POST /api/v1/admin/releases/:id/artifacts/:aid/finalize`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | نهایی‌سازی آپلود و محاسبهٔ هش/امضا |
| ۲ | متد و مسیر | `POST /api/v1/admin/releases/:id/artifacts/:aid/finalize` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` یا `releases:write` |
| ۶ | path | `:id` = UUID ریلیز، `:aid` = UUID artifact |
| ۷ | query | — |
| ۸ | هدرها | `Content-Type: application/json` |
| ۹ | بدنه | اختیاری: `{"expected_sha256":"..."}` (یا alias `sha256`) |
| ۱۰ | الزامی | هر دو پارامتر path |
| ۱۱ | فیلدها | `expected_sha256` در صورت ارسال باید مطابق باشد |
| ۱۲ | نمونه | `curl -X POST -b cookie.txt -H 'Content-Type: application/json' -d '{"expected_sha256":"<hex>"}' http://localhost:9000/api/v1/admin/releases/<id>/artifacts/<aid>/finalize` |
| ۱۳ | پاسخ موفق | `model.ReleaseArtifact` با `sha256`,`ed25519_sig`,`signing_key_id` (`tauri_signature` مخفی) |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۱، ۴۰۳، ۴۰۴، ۴۰۹ `SHA256_MISMATCH`، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"SHA256_MISMATCH","message":"uploaded file hash does not match"}}` |
| ۱۷ | اثر | خواندن بایت‌ها + محاسبهٔ sha256 سرور؛ audit `release_artifact/uploaded` |
| ۱۸ | امنیت | هش سمت سرور محاسبه می‌شود، نه از کلاینت |
| ۱۹ | تست؟ | **بله** (سطح service) |
| ۲۰ | یادداشت | اگر امضا تنظیم نشده باشد، فقط هش ثبت می‌شود |

### ۱۰.۱۵.۹ `DELETE /api/v1/admin/releases/:id/artifacts/:aid`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | حذف artifact |
| ۲ | متد و مسیر | `DELETE /api/v1/admin/releases/:id/artifacts/:aid` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` یا `releases:write` |
| ۶ | path | `:id` و `:aid` |
| ۷ | query | — |
| ۸ | هدرها | — |
| ۹ | بدنه | — |
| ۱۰ | الزامی | هر دو پارامتر |
| ۱۱ | فیلدها | — |
| ۱۲ | نمونه | `curl -X DELETE -b cookie.txt http://localhost:9000/api/v1/admin/releases/<id>/artifacts/<aid>` |
| ۱۳ | پاسخ موفق | `{"status":"deleted"}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۱، ۴۰۳، ۴۰۴، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"NOT_FOUND","message":"artifact not found"}}` |
| ۱۷ | اثر | حذف رکورد + فایل storage؛ audit |
| ۱۸ | امنیت | — |
| ۱۹ | تست؟ | **بله** (سطح store) |
| ۲۰ | یادداشت | — |

### ۱۰.۱۵.۱۰ `POST /api/v1/admin/releases/:id/actions/publish`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | انتشار ریلیز و امضای artifactها |
| ۲ | متد و مسیر | `POST /api/v1/admin/releases/:id/actions/publish` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` یا `releases:write` |
| ۶ | path | `:id` = UUID ریلیز |
| ۷ | query | — |
| ۸ | هدرها | — |
| ۹ | بدنه | — |
| ۱۰ | الزامی | `:id` |
| ۱۱ | فیلدها | — |
| ۱۲ | نمونه | `curl -X POST -b cookie.txt http://localhost:9000/api/v1/admin/releases/<id>/actions/publish` |
| ۱۳ | پاسخ موفق | `model.Release` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۱، ۴۰۳، ۴۰۴، ۵۰۳ `SIGNING_NOT_CONFIGURED`، ۴۰۹ (artifact نامعتبر)، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"SIGNING_NOT_CONFIGURED","message":"no active signing key"}}` |
| ۱۷ | اثر | امضای artifactها؛ `status=published`؛ audit `release/published` |
| ۱۸ | امنیت | کلید خصوصی امضا هرگز خارج نمی‌شود |
| ۱۹ | تست؟ | **بله** — `release_publish_*_test.go` |
| ۲۰ | یادداشت | پس از انتشار، فیدها نسخه را نشان می‌دهند |

### ۱۰.۱۵.۱۱ `POST /api/v1/admin/releases/:id/actions/yank`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | خارج‌کردن ریلیز از فید |
| ۲ | متد و مسیر | `POST /api/v1/admin/releases/:id/actions/yank` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` یا `releases:write` |
| ۶ | path | `:id` = UUID ریلیز |
| ۷ | query | — |
| ۸ | هدرها | `Content-Type: application/json` |
| ۹ | بدنه | `{"reason":"..."}` |
| ۱۰ | الزامی | `reason` (≤۱۰۲۴) |
| ۱۱ | فیلدها | — |
| ۱۲ | نمونه | `curl -X POST -b cookie.txt -H 'Content-Type: application/json' -d '{"reason":"security issue"}' http://localhost:9000/api/v1/admin/releases/<id>/actions/yank` |
| ۱۳ | پاسخ موفق | `model.Release` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۰، ۴۰۱، ۴۰۳، ۴۰۴، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"BAD_REQUEST","message":"reason is required"}}` |
| ۱۷ | اثر | `status=yanked`؛ audit `release/yanked` |
| ۱۸ | امنیت | برای پاسخ امنیتی سریع طراحی شده است |
| ۱۹ | تست؟ | **بله** (سطح service) |
| ۲۰ | یادداشت | فید بلافاصله نسخه را حذف می‌کند |

### ۱۰.۱۵.۱۲ `POST /api/v1/admin/releases/:id/actions/unyank`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | بازگرداندن ریلیز yankشده به فید |
| ۲ | متد و مسیر | `POST /api/v1/admin/releases/:id/actions/unyank` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` یا `releases:write` |
| ۶ | path | `:id` = UUID ریلیز |
| ۷ | query | — |
| ۸ | هدرها | — |
| ۹ | بدنه | — |
| ۱۰ | الزامی | `:id` |
| ۱۱ | فیلدها | — |
| ۱۲ | نمونه | `curl -X POST -b cookie.txt http://localhost:9000/api/v1/admin/releases/<id>/actions/unyank` |
| ۱۳ | پاسخ موفق | `model.Release` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۱، ۴۰۳، ۴۰۴، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"NOT_FOUND","message":"release not found"}}` |
| ۱۷ | اثر | `status=published`؛ audit `release/unyanked` |
| ۱۸ | امنیت | — |
| ۱۹ | تست؟ | **بله** (سطح service) |
| ۲۰ | یادداشت | — |

> تست‌های releases در لایهٔ store/service هستند: `release_publish_key_pin_test.go`،
> `release_publish_race_test.go`، `release_signing_rotate_test.go`، `release_velopack_test.go`
> و سایر `release_*_test.go` (نه در سطح handler).

### ۱۰.۱۶ Release Signing Keys (۶) — فقط گروه `admin`

### ۱۰.۱۶.۱ `POST /api/v1/admin/products/:id/signing-key`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | تولید کلید امضای ریلیز برای محصول |
| ۲ | متد و مسیر | `POST /api/v1/admin/products/:id/signing-key` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` (نه `releases:write`) |
| ۶ | path | `:id` = UUID محصول |
| ۷ | query | — |
| ۸ | هدرها | — |
| ۹ | بدنه | — |
| ۱۰ | الزامی | `:id` |
| ۱۱ | فیلدها | — |
| ۱۲ | نمونه | `curl -X POST -b cookie.txt http://localhost:9000/api/v1/admin/products/<id>/signing-key` |
| ۱۳ | پاسخ موفق | `model.ReleaseSigningKey`: `id,product_id,public_key,active,note,created_at,rotated_at` (کلید خصوصی مخفی) |
| ۱۴ | وضعیت | **۲۰۱** |
| ۱۵ | خطاها | ۴۰۱، ۴۰۳، ۴۰۴، ۵۰۳ `SIGNING_DISABLED`، ۴۰۹ `KEY_ALREADY_ACTIVE`، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"KEY_ALREADY_ACTIVE","message":"product already has an active signing key"}}` |
| ۱۷ | اثر | تولید جفت‌کلید Ed25519؛ audit `release_signing_key/generated` |
| ۱۸ | امنیت | کلید خصوصی رمزنگاری‌شده ذخیره می‌شود و هرگز برنمی‌گردد |
| ۱۹ | تست؟ | **بله** — `release_signing_rotate_test.go` |
| ۲۰ | یادداشت | برای چرخش از `/rotate` استفاده کنید |

### ۱۰.۱۶.۲ `POST /api/v1/admin/products/:id/signing-key/rotate`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | چرخش کلید امضا |
| ۲ | متد و مسیر | `POST /api/v1/admin/products/:id/signing-key/rotate` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` |
| ۶ | path | `:id` = UUID محصول |
| ۷ | query | — |
| ۸ | هدرها | `Content-Type: application/json` |
| ۹ | بدنه | اختیاری: `{"note":"..."}` |
| ۱۰ | الزامی | `:id` |
| ۱۱ | فیلدها | `note` ≤۲۵۶ کاراکتر |
| ۱۲ | نمونه | `curl -X POST -b cookie.txt -H 'Content-Type: application/json' -d '{"note":"2027 rotation"}' http://localhost:9000/api/v1/admin/products/<id>/signing-key/rotate` |
| ۱۳ | پاسخ موفق | `model.ReleaseSigningKey` جدید |
| ۱۴ | وضعیت | **۲۰۱** |
| ۱۵ | خطاها | ۴۰۰ (`note too long (max 256 chars)`)، ۴۰۱، ۴۰۳، ۴۰۴، ۵۰۳ `SIGNING_DISABLED`، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"BAD_REQUEST","message":"note too long (max 256 chars)"}}` |
| ۱۷ | اثر | کلید قبلی غیرفعال + کلید جدید فعال؛ audit `…/rotated` |
| ۱۸ | امنیت | کلاینت‌های قدیمی باید کلید عمومی جدید را دریافت کنند |
| ۱۹ | تست؟ | **بله** — `release_signing_rotate_test.go` |
| ۲۰ | یادداشت | برای سازگاری، کلید قبلی در تاریخچه می‌ماند |

### ۱۰.۱۶.۳ `GET /api/v1/admin/products/:id/signing-keys`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | لیست کلیدهای امضای محصول |
| ۲ | متد و مسیر | `GET /api/v1/admin/products/:id/signing-keys` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` |
| ۶ | path | `:id` = UUID محصول |
| ۷ | query | — |
| ۸ | هدرها | — |
| ۹ | بدنه | — |
| ۱۰ | الزامی | `:id` |
| ۱۱ | فیلدها | — |
| ۱۲ | نمونه | `curl -s -b cookie.txt http://localhost:9000/api/v1/admin/products/<id>/signing-keys` |
| ۱۳ | پاسخ موفق | `{"keys":[...]}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۱، ۴۰۳، ۴۰۴، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"NOT_FOUND","message":"product not found"}}` |
| ۱۷ | اثر | فقط خواندن |
| ۱۸ | امنیت | فقط کلید عمومی برگردانده می‌شود |
| ۱۹ | تست؟ | ندارد |
| ۲۰ | یادداشت | — |

### ۱۰.۱۶.۴ `GET /api/v1/admin/products/:id/signing-key/public.pem`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | دانلود کلید عمومی فعال به‌صورت PEM |
| ۲ | متد و مسیر | `GET /api/v1/admin/products/:id/signing-key/public.pem` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` |
| ۶ | path | `:id` = UUID محصول |
| ۷ | query | — |
| ۸ | هدرها | `Content-Type: application/x-pem-file`، `Content-Disposition: attachment; filename="public_key.pem"` |
| ۹ | بدنه | — |
| ۱۰ | الزامی | `:id` |
| ۱۱ | فیلدها | — |
| ۱۲ | نمونه | `curl -s -b cookie.txt http://localhost:9000/api/v1/admin/products/<id>/signing-key/public.pem -o public_key.pem` |
| ۱۳ | پاسخ موفق | **خام PEM** (نه envelope) |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۱، ۴۰۳، ۴۰۴ (`no active signing key for this product — generate one first`)، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"NOT_FOUND","message":"no active signing key for this product — generate one first"}}` |
| ۱۷ | اثر | فقط خواندن |
| ۱۸ | امنیت | این همان کلیدی است که کلاینت برای تأیید امضا استفاده می‌کند |
| ۱۹ | تست؟ | ندارد |
| ۲۰ | یادداشت | در صورت وجود چند کلید، فقط فعال برمی‌گردد |

### ۱۰.۱۶.۵ `GET /api/v1/admin/products/:id/signing-key/tauri-pubkey`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | دریافت کلید عمومی Tauri/minisign |
| ۲ | متد و مسیر | `GET /api/v1/admin/products/:id/signing-key/tauri-pubkey` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` |
| ۶ | path | `:id` = UUID محصول |
| ۷ | query | — |
| ۸ | هدرها | — |
| ۹ | بدنه | — |
| ۱۰ | الزامی | `:id` |
| ۱۱ | فیلدها | — |
| ۱۲ | نمونه | `curl -s -b cookie.txt http://localhost:9000/api/v1/admin/products/<id>/signing-key/tauri-pubkey` |
| ۱۳ | پاسخ موفق | `{"pubkey":"<minisign pubkey>"}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۱، ۴۰۳، ۴۰۴، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"NOT_FOUND","message":"no active signing key"}}` |
| ۱۷ | اثر | فقط خواندن |
| ۱۸ | امنیت | — |
| ۱۹ | تست؟ | ندارد |
| ۲۰ | یادداشت | قالب مناسب `tauri.conf.json` |

### ۱۰.۱۶.۶ `DELETE /api/v1/admin/products/:id/signing-key`

| # | مورد | مقدار |
|---|---|---|
| ۱ | هدف | غیرفعال‌کردن کلید امضای فعال |
| ۲ | متد و مسیر | `DELETE /api/v1/admin/products/:id/signing-key` |
| ۳ | دسترسی | protected |
| ۴ | احراز هویت | session یا API Key |
| ۵ | scope | `admin` |
| ۶ | path | `:id` = UUID محصول |
| ۷ | query | — |
| ۸ | هدرها | `Content-Type: application/json` |
| ۹ | بدنه | اختیاری: `{"note":"..."}` |
| ۱۰ | الزامی | `:id` |
| ۱۱ | فیلدها | — |
| ۱۲ | نمونه | `curl -X DELETE -b cookie.txt -H 'Content-Type: application/json' -d '{"note":"retired"}' http://localhost:9000/api/v1/admin/products/<id>/signing-key` |
| ۱۳ | پاسخ موفق | `{"status":"deactivated"}` |
| ۱۴ | وضعیت | ۲۰۰ |
| ۱۵ | خطاها | ۴۰۰، ۴۰۱، ۴۰۳، ۴۰۴، ۴۲۹، ۵۰۰ |
| ۱۶ | نمونهٔ خطا | `{"success":false,"error":{"code":"BAD_REQUEST","message":"invalid note"}}` |
| ۱۷ | اثر | غیرفعال‌سازی؛ audit `release_signing_key/deactivated` |
| ۱۸ | امنیت | پس از غیرفعال‌سازی، انتشار ریلیز جدید ممکن است ۵۰۳ بدهد |
| ۱۹ | تست؟ | ندارد |
| ۲۰ | یادداشت | این شش مسیر عمداً `admin`-only هستند و با `releases:write` باز نمی‌شوند |

---

## پیوست: endpointهایی که در کد وجود دارند ولی route ثبت‌شده ندارند

* `payment.StripeHandler.CreateCheckoutSession` (در `internal/payment/stripe.go`) — **در `main.go` ثبت نشده**
  و از بیرون قابل فراخوانی نیست (بدنه‌اش `price_id`*،`email`,`success_url`,`cancel_url` است).
* `SystemHandler.StartAutoCheck` — یک حلقهٔ پس‌زمینه است، نه endpoint.






