# راهنمای پنل مدیریت و پورتال Keygate

این سند کار با رابط کاربری Keygate (SPA با React + Vite + TypeScript) را گام‌به‌گام توضیح می‌دهد.
مسیرها، نام صفحه‌ها و عملکرد دکمه‌ها همه از کد `web/src/` استخراج شده‌اند و با خود برنامه تطبیق دارند.

> زبان رابط کاربری: در حال حاضر فقط **`en`** و **`zh`** پشتیبانی می‌شوند؛ locale فارسی (`fa`) وجود
> ندارد. بنابراین نام‌های داخل پنل در این سند به‌صورت انگلیسی (همان‌طور که در پنل می‌بینید) و توضیح
> به فارسی است.

---

## ۱. اولین اجرا: Setup

اگر هنوز هیچ کاربری مالک (owner) نداشته باشد، برنامه شما را به مسیر `/setup` می‌برد.

صفحهٔ `SetupPage` فایل `web/src/pages/setup.tsx` را اجرا می‌کند و مراحل را از
`GET /api/v1/setup/status` می‌خواند (`{needed, step}`). سپس با
`POST /api/v1/setup/initialize` اولین کاربر + محصول + پلن ساخته می‌شود و در پاسخ
`{user, product, plan}` برمی‌گردد.

پس از تکمیل، به `/login` هدایت می‌شوید.

---

## ۲. ورود

مسیر: `/login` (`LoginPage`).

روش‌های ورود از `GET /api/v1/auth/providers` می‌آید (`{dev_login, otp}`):

* **OTP** — ایمیل را وارد می‌کنید، `POST /auth/otp/send` اجرا می‌شود، کد می‌آید و با
  `POST /auth/otp/verify` وارد می‌شوید.
* **Dev login** — فقط وقتی `ENVIRONMENT=development` باشد فعال است
  (`POST /auth/dev-login`).
* **OAuth** — اگر ارائه‌دهنده‌ای پیکربندی شده باشد.

پس از ورود، کوکی `session` ست می‌شود (جزئیات در `authentication-and-security.md`).

---

## ۳. قالب پنل و ناوبری

پنل زیر مسیر `/admin` و داخل `AdminLayout` قرار دارد. نوار کناری (`adminNav` در
`web/src/components/layout.tsx`) این ساختار را دارد:

| گروه | آیتم | مسیر |
|---|---|---|
| (بدون گروه) | Dashboard | `/admin` |
| **Catalog** | Products | `/admin/products` |
| | Plans | `/admin/plans` |
| | Addons | `/admin/addons` |
| | Releases | `/admin/releases` |
| **Licensing** | Licenses | `/admin/licenses` |
| | Devices | `/admin/devices` |
| | Customers | `/admin/customers` |
| **Developer** | API Keys | `/admin/api-keys` |
| | Webhooks | `/admin/webhooks` |
| | Integration | `/admin/integration` |
| **Insights** | Analytics | `/admin/analytics` |
| | Audit | `/admin/audit` |
| (پایین) | Settings | `/admin/settings` |

همچنین یک لینک بیرونی **Sponsor** به `https://keygate.app/sponsorships` در نوار کناری است.

> مسیر پورتال مشتری: `/portal` (لیست لایسنس‌ها) و `/portal/account` (حساب کاربری).

---

## ۴. صفحه‌به‌صفحهٔ پنل مدیریت

### ۴.۱ Dashboard — `/admin`

* هدف: نمای کلی سلامت کسب‌وکار.
* دادهٔ اصلی از `GET /api/v1/admin/stats` می‌آید که شامل
  `total_licenses`, `active_licenses`, `total_activations`, `total_products`, `total_seats`,
  `total_usage_events`, `total_webhooks`, `by_status` و `recent_licenses[]` است.

### ۴.۲ Products — `/admin/products`

* هدف: مدیریت محصولات (نرم‌افزارهایی که می‌فروشید).
* توابع API: `listProducts`, `getProduct`, `createProduct`, `updateProduct`, `deleteProduct`.
* نکات مهم فیلدها:
  * `type` یکی از `desktop` / `saas` / `hybrid` است و **قابلیت‌های مجاز** را تعیین می‌کند:
    `desktop` → activations + releases؛ `saas` → seats؛ `hybrid` → همه.
  * `require_signing` — اگر روشن باشد، انتشار نسخه بدون کلید امضای فعال رد می‌شود.
  * `feed_license_required` — اگر روشن باشد، فید به‌روزرسانی نیازمند `license_key` می‌شود.
  * `minimum_supported_version` — کف نسخهٔ پشتیبانی‌شده؛ کلاینت‌های قدیمی‌تر رد می‌شوند.
  * `download_url` — لینک صفحهٔ دانلود (اختیاری).
* خطاهای رایج: `DUPLICATE` (slug تکراری)، `INCOMPATIBLE_PRODUCT_TYPE` (تغییر نوع محصولی که
  نسخه دارد)، و خطاهای مربوط به gate فید.

### ۴.۳ Plans — `/admin/plans`

* هدف: مدیریت پلن‌های فروش و entitlements.
* توابع API: `listPlans`, `getPlan`, `createPlan`, `updatePlan`, `deletePlan`,
  `createEntitlement`, `updateEntitlement`, `deleteEntitlement`.
* فیلدهای کلیدی پلن:
  * `license_type` ∈ `subscription` / `perpetual` / `trial`.
  * `billing_interval` ∈ `""` / `month` / `year`.
  * `max_activations` (سقف دستگاه)، `max_seats` (سقف صندلی).
  * `grace_days` (دورهٔ مهلت)، `trial_days`، `token_ttl_days` (عمر توکن آفلاین).
  * `license_model` ∈ `standard` / `floating` و `floating_timeout` (دقیقه).
  * دورهٔ نگهداری: `updates_days`, `renewal_days`, `stripe_renewal_price_id`.
  * `stripe_price_id` (یکتا در سطح سیستم — خطای `STRIPE_PRICE_IN_USE`).
* Entitlements: هر ردیف `feature` + `value_type` (`bool`/`int`/`string`/`quota`/`flag`) +
  `value` (+ `quota_period` و `quota_unit` برای سهمیه‌ها). `stripe_meter_event_name` فقط برای
  `quota` معنا دارد.
* نکته: اگر محصولی فید عمومی دارد، ساخت پلن «کران‌دار» (با دورهٔ به‌روزرسانی) رد می‌شود
  (`FEED_NOT_GATED`).

### ۴.۴ Addons — `/admin/addons`

* هدف: کاتالوگ افزودنی‌ها در سطح محصول.
* توابع API: `listAddons`, `createAddon`, `updateAddon`, `deleteAddon`.
* افزودنی یک `feature` + `value_type` (`bool`/`int`/`string`/`quota`) دارد و به لایسنس‌های مشخصی
  ضمیمه می‌شود (بخش لایسنس‌ها). اگر افزودنی فعال باشد، مقدار آن برای همان `feature` جای پلن را
  می‌گیرد.

### ۴.۵ Releases — `/admin/releases`

* هدف: مدیریت نسخه‌ها و فایل‌های آن‌ها و انتشار برای آپدیترها.
* توابع API: `listReleases`, `getRelease`, `createRelease`, `addArtifact`, `finalizeArtifact`,
  `deleteArtifact`, `publishRelease`, `yankRelease`, `unyankRelease`, `updateReleaseNotes`,
  `deleteRelease`.
* **جریان آپلود فایل در UI**: پس از `addArtifact` یک `upload_url` (presigned PUT) گرفته می‌شود،
  فایل با `fetch(init.upload_url, {method:"PUT"})` آپلود می‌شود، و سپس `finalizeArtifact` با
  `expected_sha256` صدا زده می‌شود. hash سمت کلاینت فقط برای فایل‌های کوچک‌تر از
  `256 MiB` (`CLIENT_HASH_MAX_BYTES`) محاسبه می‌شود؛ در هر حال **سرور** hash نهایی را می‌سازد.
* دکمه‌ها: **New Release**، **Update Settings** (گفتگوی تنظیمات به‌روزرسانی)، در منوی سطر:
  View Artifacts، Publish (`Rocket`)، Yank (`AlertTriangle`)، Unyank، Delete Draft.
* مفهوم **«نسخهٔ پایین‌تر از آخرین»**: صفحه برای هر `(product, channel)` آخرین نسخه را با
  `computeLatestVersions` و `CHANNEL_FALLBACK` (مثلاً `beta → [beta, stable]`) حساب می‌کند و
  نسخه‌های قدیمی‌تر را با نشان کهربایی مشخص می‌کند.
* کانال‌ها (`RELEASE_CHANNELS`): `stable`, `beta`, `alpha`, `dev`.

### ۴.۶ Licenses — `/admin/licenses`

* هدف: مدیریت لایسنس‌ها، فعال‌سازی‌ها، سهمیه، صندلی و افزودنی‌های هر لایسنس.
* توابع API: `listLicenses`, `getLicense`, `revealLicenseKey`, `resendLicenseEmail`,
  `createLicense`, `setLicenseValidUntil`, `setLicenseUpdatesUntil`, `revokeLicense`,
  `suspendLicense`, `reinstateLicense`, `refundLicense`, `deleteActivation`,
  `deactivateActivation`, `reactivateActivation`, `transferLicense`, `issueOfflineToken`,
  `unlinkStripeSubscription`, `changeLicensePlan`, `getLicenseUsage`, `resetUsageCounter`,
  `getLicenseSeats`, `getLicenseAddons`, `addLicenseAddon`, `removeLicenseAddon`,
  `getFloatingSessions`.
* نکات مهم:
  * `revealLicenseKey` کلید خام را برمی‌گرداند و **یک رکورد audit** می‌سازد
    (`action:"key_revealed"`). اگر رمزگشایی ممکن نباشد، `503 LICENSE_KEY_UNAVAILABLE`.
  * `resendLicenseEmail` فقط به ایمیل خودِ لایسنس می‌فرستد، نه به آدرس دلخواه.
  * `refundLicense` واقعاً در Stripe لغو اشتراک می‌کند و وضعیت را `revoked` می‌کند.
  * `setLicenseUpdatesUntil` فقط روی لایسنس‌های **دائمی** معنا دارد (خطای `NOT_PERPETUAL`).
  * فیلترها: `product_id`, `status`, `search`, `external_customer_id`, `external_workspace_id`
    و مرتب‌سازی با `sort` (`created_at`/`valid_until`/`email`/`status`/`product`/`plan`).
  * در لیست، به‌جای کلید خام، `license_key_hints` (چهار کاراکتر آخر) برگردانده می‌شود.
  * **انتقال لایسنس** (`transferLicense`): ایمیل جدید + گزینهٔ «آزادکردن فعال‌سازی‌ها»
    (پیش‌فرض روشن) + دلیل اختیاری. ایمیل یکسان → `409 IDENTICAL_EMAIL`. عملیات در audit با
    `transferred` ثبت و webhook `license.transferred` ارسال می‌شود.
  * **صدور توکن آفلاین** (`issueOfflineToken`): یک `identifier` (و برچسب اختیاری) می‌گیرد و
    **یک فعال‌سازی واقعی** روی همان شناسه انجام می‌دهد؛ یعنی سهم دستگاه مصرف می‌شود. توکن
    برگشتی در همان صفحه با «بازرس توکن آفلاین» قابل تجزیه است (بدون تأیید رمزنگاری‌شده).
  * **غیرفعال/فعال‌کردن دستگاه** (`deactivateActivation` / `reactivateActivation`): آزادکردن
    نرم سهم و بازگرداندن آن؛ بازگرداندن ممکن است `409 ACTIVATION_LIMIT` بدهد اگر سهم پلن پر
    باشد.

### ۴.۶.۱ Devices — `/admin/devices`

* هدف: نمای سراسری همهٔ activationها (فراتر از یک لایسنس) برای بازرسی و پاک‌سازی دستگاه‌ها.
* **endpoint اختصاصی ندارد**: صفحه ابتدا لایسنس‌ها را می‌گیرد و activationهای همراهشان را
  سمت کلاینت تجمیع می‌کند (بیشتر از `GET /admin/licenses`).
* کارت‌های بالای صفحه: تعداد دستگاه‌های ردیابی‌شده، سهم‌های فعال، غیرفعال‌شده‌ها، و توزیع
  **scheme**ها (`kgid-v1` / `sha256-v1` / `uuid` / `opaque` / `legacy`).
* فیلترها: محصول، وضعیت (`active`/`deactivated`)، scheme، و جست‌وجو روی identifier، برچسب،
  IP یا ایمیل.
* عملیات هر ردیف: Deactivate (آزادکردن سهم)، Reactivate (مصرف دوبارهٔ سهم)، Delete.
* ⚠️ متن دیالوگ Delete در UI می‌گوید «برای همیشه حذف می‌شود»، ولی backend از نسخهٔ فعلی
  **حذف نرم** انجام می‌دهد (tombstone با `status='deactivated'`). این ناهم‌خوانی متن/رفتار در
  `documentation-audit.md` ثبت شده است.

### ۴.۷ Customers — `/admin/customers`

* هدف: نمای یکپارچهٔ مشتری.
* توابع API: `listUsers`, `getUserDetail`.
* `getUserDetail` شامل `user`, `licenses`, `subscriptions`, `total_usage`, `active_seats`,
  `activations`, `recent_audit_logs` و `license_key_hints` است.

### ۴.۸ API Keys — `/admin/api-keys`

* هدف: ساخت و مدیریت کلیدهای سرور‌به‌سرور.
* توابع API: `listAPIKeys`, `createAPIKey`, `rotateAPIKey`, `deleteAPIKey`.
* **مهم**: کلید خام (`kg_live_…`) فقط یک‌بار هنگام `create` یا `rotate` نمایش داده می‌شود.
* برای هر کلید می‌توان `product_id` و مجموعهٔ `scopes`
  (`admin` / `licenses:write` / `releases:write`) تعیین کرد.

### ۴.۹ Webhooks — `/admin/webhooks`

* هدف: مدیریت endpointهای دریافت رویداد و مشاهدهٔ تحویل‌ها.
* توابع API: `listWebhooks`, `createWebhook`, `updateWebhook`, `deleteWebhook`,
  `listWebhookDeliveries`, `resendWebhookDelivery`, `testWebhook`.
* رویدادها شامل `license.created`, `license.canceled`, `license.suspended`,
  `license.reinstated`, `license.revoked`, `quota.warning`, `quota.exceeded`, `seat.added` و… است.
* ارسال وبهوک با محافظت SSRF انجام می‌شود.

### ۴.۹.۱ Integration — `/admin/integration`

* هدف: صفحهٔ «شروع سریع برای توسعه‌دهنده» — snippetهای آماده برای فعال‌سازی، بررسی آفلاین و
  بررسی امضا، به‌همراه یک **بازرس توکن آفلاین** در مرورگر.
* endpoint اختصاصی ندارد؛ داده‌ها از `GET /config` (آدرس عمومی)، `GET /license/pubkey` و
  کلیدهای امضای محصول می‌آید.
* بازرس توکن فقط payload را دیکد و **در مرورگر امضا را بررسی می‌کند**؛ چیزی روی سرور ذخیره
  نمی‌شود.
* هیچ API Key مدیریتی در snippetها قرار نمی‌گیرد؛ فقط `license_key` و کلید عمومی.

### ۴.۱۰ Analytics — `/admin/analytics`

* هدف: تحلیل رشد، ترکیب لایسنس‌ها، مصرف و روند فعال‌سازی.
* توابع API: `getAnalytics`, `getAnalyticsSummary`, `getAnalyticsBreakdown`,
  `getAnalyticsUsageTop`, `getAnalyticsActivationTrend`, `getAnalyticsInsights`.

### ۴.۱۱ Audit — `/admin/audit`

* هدف: مشاهدهٔ لاگ حسابرسی.
* تابع API: `listAuditLogs` با فیلترهای `entity`, `entity_id`, `product_id`.

### ۴.۱۲ Settings — `/admin/settings`

* هدف: پیکربندی سراسری، رازها، قالب ایمیل‌ها و عملیات سیستم.
* توابع API: `getSettings`, `updateSettings`, `sendTestEmail`, `clearSecretSetting`,
  `getEmailTemplates`, `checkUpdate` (یعنی `GET /admin/system/update-check`).
* رازها (مثل `smtp_password`, `cloudflare_api_token`) هرگز مقدارشان بازگردانده نمی‌شود؛ فقط
  «تنظیم‌شده/نشده» (`secrets_set`) گزارش می‌شود.
* **قالب ایمیل‌ها** (`EmailTemplatesManager` در `web/src/pages/admin/email-templates.tsx`) داخل
  همین صفحه و در تب `templates` رندر می‌شود — **مسیر مستقیم `/admin/email-templates` وجود ندارد**.
  کلیدهای قالب: `license_created`, `license_expiring`, `updates_ending`, `license_expired`,
  `trial_expired`, `license_suspended`, `quota_warning`, `seat_invite`, `payment_failed`.
  پیش‌نمایش در `iframe` سندباکس‌شده انجام می‌شود؛ ذخیره/بازنشانی با
  `updateSettings({email_template_<key>: ...})` است.

---

## ۵. پورتال مشتری

### ۵.۱ Licenses — `/portal`

صفحهٔ `PortalLicensesPage`:

* `portal.licenses` → `{licenses, renewals_enabled}`.
* برای هر لایسنس: کپی کلید، آمار (valid_from, valid_until، فعال‌سازی‌ها `n/max`، صندلی‌ها)،
  دورهٔ نگهداری (با دکمهٔ تمدید اگر `renewals_enabled` باشد).
* تب‌ها: **Activations**، **Entitlements**، **Usage** (برای `saas`/`hybrid`)، **Team Members**
  (اگر `max_seats != 1`).
* عملیات مالک (owner) فقط: پورتال پرداخت Stripe، تغییر پلن، لغو اشتراک، مشاهدهٔ فاکتورها.
  تشخیص مالک: ایمیل کاربر (بدون حساسیت به بزرگی/کوچکی) برابر `lic.email` باشد.

### ۵.۲ Account — `/portal/account`

صفحهٔ `PortalAccountPage`: مدیریت پروفایل مشتری (`PUT /portal/profile` با `{name}`).

---

## ۶. روال‌های کاری رایج

### افزودن محصول و پلن جدید
1. `/admin/products` → New Product (انتخاب `type`).
2. `/admin/plans` → New Plan (اتصال به محصول، `license_type`, سقف‌ها، قیمت Stripe).
3. افزودن entitlements لازم به پلن.

### صدور لایسنس دستی
1. `/admin/licenses` → New License (انتخاب `product_id` و `plan_id` و `email`).
2. کلید خام در پاسخ `create` یک‌بار برمی‌گردد؛ آن را به مشتری برسانید.
3. برای مشاهدهٔ مجدد: دکمهٔ Reveal Key (با ثبت در audit).

### انتشار نسخهٔ جدید
1. `/admin/releases` → New Release (نسخهٔ semver + کانال).
2. افزودن artifact برای هر platform و آپلود فایل.
3. Finalize (تثبیت hash) و سپس Publish.
4. اگر محصول `require_signing` دارد، اول از صفحهٔ محصول کلید امضا بسازید.

### عیب‌یابی «چرا مشتری نمی‌تواند فعال کند»
* وضعیت لایسنس را در `/admin/licenses` ببینید (`active`/`trialing`/`suspended`/`expired`/…).
* تعداد فعال‌سازی‌ها را با سقف پلن مقایسه کنید.
* اگر `floating` است، جلسه‌های باز را در تب floating ببینید.

---

## ۷. نکات UI که ممکن است گیج‌کننده باشد

* لینک بیرونی **Sponsor** بخشی از ناوبری است اما صفحهٔ داخلی نیست.
* تب قالب‌های ایمیل داخل Settings است، نه یک آیتم ناوبری مستقل.
* در لیست‌ها کلید لایسنس کامل نمایش داده نمی‌شود (به‌دلیل امنیت) و برای دیدن آن باید Reveal بگیرید.
* عنوان‌های `nav.catalog`, `nav.licensing`, `nav.developer`, `nav.insights` از فایل ترجمه می‌آیند؛
  اگر متن انگلیسی/چینی می‌بینید طبیعی است چون فعلاً فقط این دو زبان وجود دارد.
