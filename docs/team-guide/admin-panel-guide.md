# راهنمای پنل مدیریت

این سند تمام صفحه‌های واقعی پنل ادمین را بر اساس کد `web/src/pages/admin/*.tsx`، ثبت مسیرها در `web/src/main.tsx` و endpointهای واقعی در `cmd/server/main.go` مستند می‌کند. «وضعیت» هر صفحه بر اساس مسیر واقعی اجرای عملیات تعیین شده، نه فقط وجود دکمه.

---

## ۱. قواعد عمومی پنل

| موضوع | واقعیت در کد |
| --- | --- |
| مسیر پایه | همه‌ی صفحه‌های ادمین زیر `/admin/*` و داخل `AdminLayout` هستند |
| نگهبان دسترسی | `web/src/components/layout.tsx`: اگر `loading` باشد صفحه‌ی بارگذاری، اگر کاربر نباشد هدایت به `/login`، و اگر `user.is_admin` نباشد هدایت به `/portal` |
| احراز هویت هر درخواست | کوکی نشست (۲۰ ثانیه تا ۲۴ ساعت) با تمدید خودکار؛ در `401` یک‌بار `POST /auth/refresh` و تکرار درخواست |
| پوشش خطا | `MutationCache.onError` در `main.tsx` هر خطای تغییرداده را به Toast تبدیل می‌کند؛ خطاها مرور `error.message` سرور را نشان می‌دهند |
| وضعیت بارگذاری | همه‌ی صفحه‌ها از TanStack Query استفاده می‌کنند و حالت `isLoading`/`isPending` را نمایش می‌دهند |
| صفحه‌بندی | سرور مقادیر `limit`/`offset` را برمی‌گرداند؛ پیش‌فرض ۵۰ و سقف ۲۰۰ ردیف |
| سطح دسترسی API | صفحه‌های ادمین به نشست ادمین یا API key با scope `admin` نیاز دارند، جز صفحه‌های لایسنس و نسخه‌ها که با scopeهای محدودتر هم کار می‌کنند |
| اطلاعات محرمانه | کلید لایسنس در هیچ لیست یا جزئیاتی نیست؛ فقط چهار کاراکتر آخر (`license_key_hints`) می‌آید. کلید فقط با `GET /admin/licenses/:id/key` می‌آید و هر بار حسابرسی می‌شود |
| اطلاعات حساس در مرورگر | رمزهای SMTP و توکن Cloudflare هرگز از سرور خوانده نمی‌شوند؛ فقط وضعیت «تنظیم‌شده/نشده» می‌آید. کلید خصوصی امضا هم هرگز به مرورگر نمی‌رود |

## ۲. جدول صفحه‌ها

| صفحه | مسیر | فایل | وضعیت |
| --- | --- | --- | --- |
| داشبورد | `/admin` | `dashboard.tsx` | پیاده‌سازی‌شده |
| محصولات | `/admin/products` | `products.tsx` | پیاده‌سازی‌شده |
| طرح‌ها و قابلیت‌ها | `/admin/plans` | `plans.tsx` | پیاده‌سازی‌شده |
| نسخه‌ها | `/admin/releases` | `releases.tsx` | پیاده‌سازی‌شده |
| لایسنس‌ها | `/admin/licenses` | `licenses.tsx` | پیاده‌سازی‌شده |
| دستگاه‌ها | `/admin/devices` | `devices.tsx` | پیاده‌سازی‌شده اما ناقص (بدون endpoint اختصاصی) |
| کلیدهای API | `/admin/api-keys` | `api-keys.tsx` | پیاده‌سازی‌شده |
| Webhookها | `/admin/webhooks` | `webhooks.tsx` | پیاده‌سازی‌شده |
| یکپارچه‌سازی | `/admin/integration` | `integration.tsx` | پیاده‌سازی‌شده اما نمونه‌کدهایش با API واقعی ناسازگار است |
| افزودنی‌ها | `/admin/addons` | `addons.tsx` | پیاده‌سازی‌شده |
| تحلیل‌ها | `/admin/analytics` | `analytics.tsx` | پیاده‌سازی‌شده |
| گزارش حسابرسی | `/admin/audit` | `audit.tsx` | پیاده‌سازی‌شده |
| مشتریان | `/admin/customers` | `customers.tsx` | پیاده‌سازی‌شده |
| تنظیمات | `/admin/settings` | `settings.tsx` | پیاده‌سازی‌شده |
| قالب‌های ایمیل | `/admin/email-templates` | `email-templates.tsx` | صفحه وجود دارد اما از ناوبری اصلی لینک نشده |

نکته: `email-templates.tsx` کامپوننت دارد و از `admin.getEmailTemplates` و `admin.updateSettings` استفاده می‌کند، اما در `main.tsx` مسیری برای آن ثبت **نشده** است؛ کارکرد واقعی آن از داخل تب «قالب‌های ایمیل» در صفحه‌ی تنظیمات انجام می‌شود.

---

## ۳. داشبورد — `/admin`

* **کاربرد:** نمای کلی وضعیت سامانه و نسخه‌ی سرور.
* **داده‌ها:** `total_licenses`, `active_licenses`, `total_activations`, `total_products`, `total_seats`, `total_usage_events`, `total_webhooks`, توزیع وضعیت‌ها (`by_status`) و ۵ لایسنس آخر.
* **عملیات:** بررسی نسخه (`GET /version`)، بررسی وجود نسخه‌ی جدید (`GET /admin/system/update-check`، کش یک‌ساعته در سرور)، مشاهده‌ی آخرین رویدادهای حسابرسی.
* **API:** `GET /admin/stats`, `GET /version`, `GET /admin/system/update-check`, `GET /admin/audit-logs`.
* **دسترسی:** ادمین (scope `admin`).
* **موفقیت/خطا:** بارگذاری جدول‌ها با حالت خالی و اسکلت؛ خطا در Toast.
* **محدودیت:** `update-check` به GitHub رجیستری `tabloy/keygate` نگاه می‌کند؛ در محیط بدون اینترنت ممکن است فیلد `error` برگردد (که در UI نمایش داده نمی‌شود).

## ۴. محصولات — `/admin/products`

* **کاربرد:** ساخت و ویرایش محصول، سقف نسخه‌ی مجاز، و روشن/خاموش‌کردن الزام امضا.
* **داده‌ها:** `name`, `slug`, `type` (`desktop` | `saas` | `hybrid`), `download_url`, `feed_license_required`, `require_signing`, `minimum_supported_version`, `minimum_supported_message`.
* **عملیات:** `POST /admin/products`, `PUT /admin/products/:id`, `DELETE /admin/products/:id`, مشاهده‌ی طرح‌های هر محصول.
* **خطاهای شایع:** `400` برای slug نامعتبر (فقط `a-z0-9-`، حداقل ۲ کاراکتر)، `400` برای `download_url` غیر http(s)، خطای یکتایی slug.
* **نکات امنیتی:** `DELETE` محصول به‌دلیل کلیدهای خارجی، لایسنس‌های وابسته را هم درگیر می‌کند؛ پیش از حذف، از دیتابیس پشتیبان بگیرید.
* **محدودیت واقعی:** گزینه‌ی روشن‌کردن `feed_license_required` پس از اینکه محصول فید منتشر کرده، تا زمانی که لینک‌های امضاشده‌ی قدیمی در کش/CDN منقضی نشوند، انتشار لایسنس با دوره‌ی آپدیت را رد می‌کند (`409 FEED_NOT_GATED`).

## ۵. طرح‌ها و قابلیت‌ها — `/admin/plans`

* **کاربرد:** تعریف طرح (`Plan`) و قابلیت‌های مجاز (`Entitlement`) هر طرح.
* **داده‌های کلیدی طرح:** `license_type` (`subscription` | `perpetual` | `trial`), `billing_interval` (`''` | `month` | `year`), `max_activations`, `max_seats`, `trial_days`, `grace_days`, `token_ttl_days`, `license_model` (`standard` | `floating`), `floating_timeout`, `updates_days`, `renewal_days`, `stripe_price_id`, `stripe_renewal_price_id`.
* **داده‌های قابلیت:** `feature`, `value_type` (`bool` | `quota`), `value`, `quota_period` (`hourly`/`daily`/`monthly`/`yearly`), `quota_unit`, `stripe_meter_event_name`.
* **عملیات:** CRUD طرح (`/admin/plans`)، CRUD قابلیت (`POST/PUT/DELETE /admin/entitlements`).
* **خطاهای قابل انتظار:** `PLAN_PRODUCT_MISMATCH`، تضاد قیمت Stripe، قواعد `billing_interval`/`updates_days` (مثلاً طرح perpetual نباید بازه‌ی تکراری داشته باشد).
* **نکته:** `max_activations` هم سقف دستگاه‌های استاندارد است و هم سقف نشست‌های هم‌زمان floating.

## ۶. نسخه‌ها — `/admin/releases`

* **کاربرد:** انتشار نسخه‌ی نرم‌افزار، آپلود artifact هر پلتفرم، امضا و مدیریت چرخه‌ی انتشار.
* **داده‌ها:** `version` (SemVer)، `channel` (`stable`/`beta`/`alpha`/`dev`)، `status` (`draft`/`published`/`yanked`)، پلتفرم‌ها، `sha256`, `file_size`, `ed25519_sig`, `tauri_signature`, `signing_key_id`.
* **عملیات:** `POST /admin/releases`، `PATCH /admin/releases/:id`، `DELETE /admin/releases/:id`، `POST .../artifacts` (برگرداندن presigned PUT)، `POST .../artifacts/:aid/finalize`، `DELETE .../artifacts/:aid`، `POST .../actions/publish|yank|unyank`.
* **دسترسی:** scope `releases:write` هم کافی است (برای CI/CD).
* **خطاهای قابل انتظار:** `SIGNING_KEY_MISSING` (وقتی `require_signing=true` و کلید فعالی نیست)، `ARTIFACT_NOT_UPLOADED`، `SHA256_MISMATCH`، `RELEASE_ARTIFACTS_NOT_SIGNED`.
* **ابزار داخل صفحه:** تب «بازرس مانیفست صحت» (`IntegrityManifestInspector`) که یک پوش مانیفست را **تجزیه و نمایش** می‌دهد. این ابزار امضا را رمزنگاری‌شده بررسی **نمی‌کند** و به سرور هم وصل نیست.
* **نکته‌ی مهم:** انتشار، امضا را «فریز» می‌کند؛ امضای نسخه‌های منتشرشده پس از چرخش کلید بازنویسی نمی‌شود.

## ۷. لایسنس‌ها — `/admin/licenses`

مهم‌ترین و کامل‌ترین صفحه (حدود ۱۹۵۰ خط). عملیات واقعی:

| عملیات | API | اثر |
| --- | --- | --- |
| فهرست/جستجو/فیلتر | `GET /admin/licenses` | فیلتر بر `product_id`, `status`, `search`, `external_customer_id`, `external_workspace_id`؛ مرتب‌سازی مجاز: `created_at`, `valid_until`, `email`, `status`, `product`, `plan` |
| جزئیات | `GET /admin/licenses/:id` | همراه `license_key_hint`, `key_usable`, `key_unusable_reason` |
| نمایش کلید | `GET /admin/licenses/:id/key` | عملیاتی که حسابرسی می‌شود (`key_revealed`) |
| ارسال مجدد ایمیل کلید | `POST /admin/licenses/:id/resend-email` | گیرنده از خود لایسنس خوانده می‌شود، نه از بدنه |
| ساخت لایسنس | `POST /admin/licenses` | `product_id`, `plan_id`, `email` اجباری؛ `valid_until` اگر بگذرید باید در آینده باشد |
| تغییر تاریخ اعتبار | `POST /admin/licenses/:id/valid-until` | مقدار خالی = همیشگی |
| تغییر سقف آپدیت | `POST /admin/licenses/:id/updates-until` | مقدار خالی = آپدیت مادام‌العمر |
| تغییر طرح | `POST /admin/licenses/:id/change-plan` | اثر روی entitlementها و حساب Stripe |
| لغو/تعلیق/بازگردانی | `revoke` / `suspend` / `reinstate` | فوراً روی `verify` اثر می‌گذارد (پرتاب به ۴۰۴) |
| بازپرداخت | `POST /admin/licenses/:id/refund` | مسیر Stripe |
| جداسازی از Stripe | `POST /admin/licenses/:id/stripe/unlink` | فقط وقتی Stripe کار اشتراک را تمام کرده |
| مصرف | `GET /admin/licenses/:id/usage`, `POST .../usage/reset` | رویدادها و شمارنده‌ها |
| صندلی‌ها | `GET /admin/licenses/:id/seats` | فقط مشاهده (افزودن/حذف صندلی در پورتال است) |
| افزودنی‌ها | `GET/POST/DELETE /admin/licenses/:id/addons` | افزودن/حذف افزودنی |
| نشست‌های floating | `GET /admin/licenses/:id/floating` | مشاهده‌ی نشست‌های فعال |
| انتقال لایسنس | `POST /admin/licenses/:id/transfer` | `new_email` اجباری، `release_activations` (پیش‌فرض true)، `reason` |
| صدور توکن آفلاین | `POST /admin/licenses/:id/offline-token` | `identifier` اجباری؛ در واقع یک **فعال‌سازی** روی آن شناسه انجام می‌دهد و توکن را برمی‌گرداند |
| دستگاه‌ها | `POST /admin/activations/:id/deactivate`, `POST .../reactivate`, `DELETE /admin/activations/:id` | آزادسازی/بازگردانی/حذف نرم |
| گزارش لایسنس | `GET /admin/audit-logs?entity=license&entity_id=…` | تاریخچه‌ی تغییرات همان لایسنس |

**نکات امنیتی:** کلید لایسنس هرگز در لیست نمی‌آید؛ نمایش کلید یک رویداد حسابرسی می‌سازد؛ انتقال لایسنس رویداد `license.transferred` را در webhook می‌فرستد؛ توکن آفلاین صادرشده در صفحه با «بازرس توکن آفلاین» قابل تجزیه است (باز هم بدون بررسی رمزنگاری‌شده).

**خطاهای شایع:** `ACTIVATION_LIMIT` با جزئیات `{max, current}`، `IDENTICAL_EMAIL`، `LICENSE_KEY_UNAVAILABLE` (وقتی کلید رمزنگاری‌شده قابل رمزگشایی نیست)، `FEED_NOT_GATED`.

## ۸. دستگاه‌ها — `/admin/devices`

* **کاربرد ظاهری:** فهرست فعال‌سازی‌ها با فیلتر محصول/وضعیت/Scheme و جستجو.
* **واقعیت پیاده‌سازی:** endpoint اختصاصی دستگاه وجود ندارد. صفحه `GET /admin/licenses?limit=100` را صدا می‌زند و از داخل هر لایسنس، `activations` را جمع می‌کند.
* **محدودیت واقعی:** فقط **۱۰۰ لایسنس اول** دیده می‌شود (سقف سخت‌کدشده در `queryFn`) و صفحه‌بندی روی دستگاه‌ها نیست. برای نصب‌های بزرگ، این صفحه همه‌ی دستگاه‌ها را نشان نمی‌دهد. این یک نقص واقعی است، نه یک ویژگی.
* **سطح دسترسی:** ادمین (و API key با `admin`).
* **عملیات:** غیرفعال‌سازی، فعال‌سازی مجدد و حذف فعال‌سازی (همان endpointهای بخش ۷).
* **وضعیت تست:** تست خودکار برای این صفحه وجود ندارد.

## ۹. کلیدهای API — `/admin/api-keys`

* **کاربرد:** ساخت کلید سرور‌به‌سرور برای CI/CD یا بک‌اند مشتری.
* **داده‌ها:** `name`, `product_id` (اختیاری؛ خالی = سیستم‌گستر), `scopes` (`admin` | `licenses:write` | `releases:write`), `prefix`, `last_used`, `last_used_ip`.
* **عملیات:** `GET/POST /admin/api-keys`, `POST /admin/api-keys/:id/rotate`, `DELETE /admin/api-keys/:id`.
* **نکته‌ی امنیتی مهم:** کلید خام **فقط یک بار** در پاسخ ساخت/چرخش برمی‌گردد؛ در دیتابیس فقط SHA-256 آن ذخیره می‌شود.
* **محدودیت:** `scopes` خالی یعنی کلید «fail-closed» (به هیچ مسیر مدیریتی نمی‌رسد).

## ۱۰. Webhookها — `/admin/webhooks`

* **کاربرد:** ارسال رویداد به سرور مشتری (HMAC-SHA256).
* **رویدادهای استاندارد:** `license.created`, `license.activated`, `license.deactivated`, `license.canceled`, `license.suspended`, `license.reinstated`, `license.revoked`, `license.transferred`, `seat.added`, `seat.removed`, `quota.warning`, `quota.exceeded`, `plan.changed`, `release.published`, `release.yanked`, `release.unyanked`.
* **داده‌ها/عملیات:** CRUD روی `/admin/webhooks` و مشاهده‌ی تحویل‌ها (`/deliveries`, `/deliveries/:delivery_id`)، ارسال مجدد (`.../resend`) و تست (`POST /admin/webhooks/:id/test`).
* **قالب درخواست:** `Content-Type: application/json`، هدرهای `X-Keygate-Event`, `X-Keygate-Delivery`, `X-Keygate-Signature: sha256=<hex HMAC>`.
* **احراز اصالت در سمت گیرنده:** HMAC-SHA256 روی **بدنه‌ی JSON خام** با `secret` همان webhook.
* **امنیت:** آدرس‌های loopback/خصوصی/link-local به‌صورت پیش‌فرض رد می‌شوند (`WEBHOOK_ALLOW_PRIVATE=false`) و ریدایرکت‌ها هم دوباره بررسی می‌شوند (ضد SSRF).
* **تست خودکار:** `internal/service/webhook_ssrf_test.go` و `internal/payment/webhook_events_test.go` وجود دارند.

## ۱۱. یکپارچه‌سازی — `/admin/integration`

* **کاربرد:** نمایش کلید عمومی امضای توکن آفلاین، بازرس توکن و نمونه‌کدها.
* **API واقعی:** فقط `GET /license/pubkey`.
* **مشکلات واقعی این صفحه (مهم):**
  1. برچسب «Public Key (Base64)» غلط است؛ سرور کلید را **hex** برمی‌گرداند (`format: "hex"`).
  2. نمونه‌ی C# پوشش پاسخ (`success/data`) را باز نمی‌کند و `token` را در ریشه می‌خواند؛ بنابراین با سرور واقعی `token` خالی می‌گیرد.
  3. نمونه‌ی Python هم `data["data"]["token"]` را نمی‌خواند و کلید عمومی hex را با `base64.b64decode` رمزگشایی می‌کند.
  4. نمونه‌ی cURL و Python فیلد `scheme` می‌فرستند که سرور آن را **نادیده** می‌گیرد (Scheme از شکل شناسه محاسبه می‌شود).
  5. در تب «Release Integrity» آدرس `http://localhost:8080/api/v1/updates/{{target}}/{{current_version}}` آمده که **چنین مسیری وجود ندارد**؛ مسیر واقعی Tauri برابر `/api/v1/releases/{product_slug}/upgrade.json` است.
  6. بازرس توکن، «جزئیات بررسی امضا» را نشان می‌دهد اما هیچ بررسی رمزنگاری‌شده‌ای انجام نمی‌دهد؛ فقط داده را تجزیه می‌کند و انقضا را با ساعت مرورگر می‌سنجد.
* برای کد درست به [csharp-dotnet-integration.md](csharp-dotnet-integration.md) و [python-and-typescript-examples.md](python-and-typescript-examples.md) مراجعه کنید.

## ۱۲. افزودنی‌ها — `/admin/addons`

* **کاربرد:** تعریف افزودنی (`Addon`) که به یک طرح افزوده می‌شود و می‌تواند قابلیت جدید بدهد.
* **API:** `GET/POST/PUT/DELETE /admin/addons`، و افزودن/حذف به لایسنس در صفحه‌ی لایسنس.
* **منطق مهم:** اگر افزودنی و طرح یک `feature` مشترک داشته باشند، **افزودنی برنده است** و سهمیه‌ی آن جایگزین سهمیه‌ی طرح می‌شود (`licenseFeatures` + `UsageService.quotaFor`).

## ۱۳. تحلیل‌ها — `/admin/analytics`

* **کاربرد:** روند لایسنس/فعال‌سازی، توزیع وضعیت، پرکاربردترین قابلیت‌ها، بینش‌های رشد و نگهداشت.
* **API:** `GET /admin/analytics`, `/analytics/summary`, `/analytics/breakdown` (پارامتر اجباری `dimension`), `/analytics/usage-top`, `/analytics/activation-trend`, `/analytics/insights`.
* **داده‌ها:** از جدول `analytics_snapshots` که روزانه در گوروتین پس‌زمینه ساخته می‌شود، به‌علاوه پرس‌وجوهای زنده.
* **پارامترها:** `product_id`, `plan_id`, `license_type`, `status`, `from`, `to`, `granularity`.
* **محدودیت:** بازه‌های تاریخ با `from`/`to` و اعتبارسنجی تاریخ در `analytics_dates_test.go` تست شده‌اند؛ اگر بازه‌ی انتخابی خالی برگردد معمولاً یعنی اسنپ‌شات آن بازه ساخته نشده است (نه خطا).

## ۱۴. گزارش حسابرسی — `/admin/audit`

* **کاربرد:** جستجوی رویدادهای سیستم.
* **API:** `GET /admin/audit-logs` با فیلترهای `entity`, `entity_id`, `product_id` و صفحه‌بندی.
* **فیلدهای هر رویداد:** `entity`, `entity_id`, `action`, `actor_type` (`admin` | `api_key`? در عمل `admin` | `sdk` | `portal` | `portal_user` | `dev_login`), `actor_id`, `ip_address`, `changes` (JSON), `created_at`.
* **نمونه Actionها:** `created`, `activated`, `activation_failed`, `deactivated`, `revoked`, `suspended`, `reinstated`, `transferred`, `key_revealed`, `migrated`, `offline_token_issued`, `seat_invited`, `seat_removed`, `draft_created`, `published`, `yanked`, `unyanked`, `uploaded`, `added`.
* **محدودیت:** حذف/ویرایش رویداد وجود ندارد (فقط درج). برای نگهداشت طولانی‌مدت باید خودتان پارتیشن/آرشیو بگذارید.

## ۱۵. مشتریان — `/admin/customers`

* **کاربرد:** فهرست کاربران ثبت‌شده و جزئیات هر کاربر.
* **API:** `GET /admin/users`, `GET /admin/users/:id`.
* **داده‌های جزئیات:** کاربر، لایسنس‌ها، اشتراک‌ها، مجموع مصرف، صندلی‌های فعال، تعداد فعال‌سازی‌ها، آخرین رویدادهای حسابرسی، و `license_key_hints`.
* **نکته:** این صفحه «مشتری» را با ایمیل کاربر تعریف می‌کند؛ لایسنسی که ادمین برای ایمیلی ساخته و کاربر هنوز ثبت‌نام نکرده، در این فهرست دیده نمی‌شود (در صفحه‌ی لایسنس‌ها دیده می‌شود).

## ۱۶. تنظیمات — `/admin/settings`

شش تب واقعی: `general`, `team`, `email`, `templates`, `security`, `system`.

* **عمومی:** `site_name`, `timezone`, `language`, `brand_color`, `logo_url`. مقدار `fa` در فایل i18n وجود ندارد؛ زبان‌های موجود `en` و `zh` هستند.
* **تیم:** `GET/POST /admin/team`, `DELETE /admin/team/:id` — دعوت ادمین جدید و حذف. تنها owner می‌تواند آخرین owner را حذف کند (قواعد در `internal/store` و `team_test.go`).
* **ایمیل:** انتخاب provider (`smtp` یا `cloudflare`)، فیلدهای هر provider، ارسال ایمیل تست (`POST /admin/settings/test-email`)، و نیز کلیدهای سرور‌مالکیتی مثل `stripe_webhook_secret` که نه خوانده و نه نوشته می‌شوند. رمزها (`smtp_password`, `cloudflare_api_token`) فقط «تنظیم‌شده/نشده» برمی‌گردند و پاک‌کردن آن‌ها یک فراخوانی جداگانه دارد: `DELETE /admin/settings/secrets/:key`.
* **قالب‌های ایمیل:** `GET /admin/email-templates` + نوشتن در `email_template_*`. قالب نامعتبر با `400` رد می‌شود (هم parse و هم render آزمایشی انجام می‌شود).
* **امنیت:** `signup_mode` (`open` یا `licensed_only`)، سهمیه‌ها، تنظیمات webhook، شکاف‌های زمانی یادآوری، `maintenance_features_enabled`.
* **سیستم:** نسخه، بررسی آپدیت، وضعیت مهاجرت‌ها (`GET /admin/system/migrations`)، کلید عمومی لایسنس، و اجرای دستی کارهای پس‌زمینه (`POST /admin/system/run-expiry-checks`, `POST /admin/system/run-metered-sync`).
* **محدودیت‌ها:** کلیدهای ناشناخته با `400 unknown setting` رد می‌شوند؛ فیلد خالی روی یک secret یعنی «تغییر نده» (نه «پاک کن»).

## ۱۷. پورتال مشتری (برای مرجع)

| مسیر | کارکرد | API |
| --- | --- | --- |
| `/portal` | لایسنس‌های من، دستگاه‌های فعال، تیم، صورتحساب، تمدید آپدیت | `GET /portal/licenses`, `POST /portal/seats`, `/portal/seats/add`, `/portal/seats/remove`, `GET/DELETE /portal/licenses/:key/activations[...]`, `POST /portal/subscription/*`, `GET /portal/subscription/invoices`, `POST /portal/updates/renew` |
| `/portal/account` | نمایش/ویرایش نام پروفایل | `GET /portal/me`, `PUT /portal/profile` |
| `/accept-invite` | پذیرش دعوت صندلی و ورود خودکار | `POST /invites/accept` |

قواعد واقعی پورتال: مالک لایسنس بر اساس `license.email` و «صندلی پذیرفته‌شده» شناسایی می‌شود؛ دعوت در حالت pending هیچ اختیاری نمی‌دهد؛ تغییر صندلی فقط برای owner یا صندلی پذیرفته‌شده با نقش `admin` مجاز است؛ و برای جلوگیری از «اوراکل وجود لایسنس»، هر دو حالت «وجود ندارد» و «مال شما نیست» با `404` یکسان پاسخ داده می‌شوند.
