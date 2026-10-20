# واژه‌نامه فارسی Keygate

این واژه‌نامه اصطلاحاتی را پوشش می‌دهد که در کد و مستندات Keygate واقعاً به‌کار رفته‌اند.
ستون «نام انگلیسی» همان چیزی است که در کد، پاسخ‌های API و پیام‌های خطا می‌بینید.

| اصطلاح فارسی | نام انگلیسی | توضیح ساده | کاربرد در Keygate |
|---|---|---|---|
| لایسنس | License | اجازهٔ استفاده از نرم‌افزار برای یک مشتری. | رکورد جدول `licenses`؛ صاحب `license_key`، `product_id`، `plan_id`، `status` و تاریخ‌ها. |
| کلید لایسنس | License Key | رشتهٔ محرمانه‌ای که نرم‌افزار مشتری با آن خودش را معرفی می‌کند. | قالب `PREFIX-XXXXXXXX-XXXXXXXX-XXXXXXXX-XXXXXXXX` (پیش‌فرض `KG`)، ساخته‌شده با `license.GenerateKey`. در دیتابیس به‌صورت hash و رمزنگاری‌شده ذخیره می‌شود. |
| پلن | Plan | بستهٔ فروش: تعیین می‌کند چند دستگاه، چند صندلی، دورهٔ آزمایشی و… مجاز است. | رکورد جدول `plans`؛ شامل `max_activations`، `max_seats`، `license_model`، `grace_days`، `token_ttl_days` و… . |
| محصول | Product | نرم‌افزاری که لایسنس می‌فروشید. | رکورد جدول `products`؛ شامل `slug`، `type` (`desktop`/`saas`/`hybrid`) و تنظیمات فید. |
| نوع محصول | Product Type | دسته‌بندی محصول که تعیین می‌کند کدام قابلیت‌ها فعال باشند. | `desktop` = فعال‌سازی + انتشار؛ `saas` = صندلی؛ `hybrid` = همه. تابع `model.ProductSupports`. |
| فعال‌سازی | Activation | ثبت یک دستگاه (یا کاربر) به‌عنوان مصرف‌کنندهٔ مجاز لایسنس. | `POST /api/v1/license/activate`؛ افزودن رکورد به جدول `activations`. |
| غیرفعال‌سازی | Deactivation | آزاد کردن یک سهم فعال‌سازی تا دستگاه دیگری جای آن را بگیرد. | `POST /api/v1/license/deactivate` (و معادل‌های portal/admin)؛ رکورد **حذف نمی‌شود**، به `status='deactivated'` تغییر می‌کند. |
| اعتبارسنجی | Verify / Validation | بررسی اینکه لایسنس همین حالا برای این دستگاه معتبر است یا نه. | `POST /api/v1/license/verify`؛ خروجی شامل توکن امضاشده و قابلیت‌ها. |
| شناسه دستگاه | Device Identifier | رشته‌ای که دستگاه مشتری را معرفی می‌کند (به‌جای «اثرانگشت» از این نام استفاده می‌کنیم). | فیلد `identifier` در درخواست فعال‌سازی/اعتبارسنجی؛ یکتا به‌ازای هر `(license, identifier)`. |
| نوع شناسه | Identifier Type | مشخص می‌کند شناسه مربوط به «دستگاه» است یا «کاربر». | فیلد `identifier_type` با مقادیر `device` (پیش‌فرض) یا `user`. |
| محدودیت فعال‌سازی | Activation Limit | سقف تعداد دستگاه‌های مجاز. | از پلن می‌آید (`max_activations`)؛ عبور از آن خطای `ACTIVATION_LIMIT` می‌دهد. |
| توکن آفلاین | Offline Token | رشتهٔ امضاشده‌ای که نرم‌افزار مشتری بدون اینترنت اعتبارسنجی می‌کند. | خروجی فیلد `token` از `activate`/`verify`؛ قالب `base64url(payload).base64url(signature)`. |
| امضای دیجیتال | Digital Signature | اثبات ریاضی اینکه داده دست‌نخورده است و از صاحب کلید خصوصی آمده. | الگوریتم **Ed25519** برای هر دو کاربرد توکن لایسنس و امضای release. |
| کلید عمومی | Public Key | کلیدی که همه می‌توانند داشته باشند و برای «بررسی» امضا به‌کار می‌رود. | `GET /api/v1/license/pubkey` (لایسنس) و `…/signing-key/public.pem` (release). |
| کلید خصوصی | Private Key | کلید محرمانهٔ امضا؛ فقط روی سرور می‌ماند. | کلید لایسنس: از `LICENSE_SIGNING_KEY`. کلید release: رمزنگاری‌شده در جدول `release_signing_keys`. |
| چرخش کلید | Key Rotation | جایگزینی کلید امضا با کلید تازه. | `POST …/signing-key/rotate`؛ کلید قدیمی غیرفعال می‌شود و کلید فعال همیشه یکی است. |
| قابلیت | Entitlement / Feature | توانایی یا پرچمی که یک پلن به مشتری می‌دهد. | رکورد جدول `entitlements` با `value_type` برابر `bool`/`int`/`string`/`quota`/`flag`. |
| سهمیه | Quota | سقف مصرف یک قابلیت در یک بازه (ساعت/روز/ماه/سال). | `value_type=quota`؛ مصرف در جدول `usage_counters` شمرده می‌شود. |
| مصرف | Usage | ثبت مقدار استفاده از یک قابلیت. | `POST /api/v1/license/usage`؛ رویداد در `usage_events`. |
| دورهٔ سهمیه | Quota Period | بازه‌ای که سهمیه در آن شمرده می‌شود. | مقادیر `hourly`/`daily`/`monthly`/`yearly`؛ پیش‌فرض `monthly`. |
| صندلی | Seat | حق دسترسی یک کاربر (به‌جای دستگاه) به لایسنس. | جدول `seats`؛ نقش‌ها `admin` یا `member`. |
| لایسنس شناور | Floating License | مدل لایسنسینگی که مشتری به تعداد محدود «جلسه» به‌طور هم‌زمان قرض می‌گیرد. | `license_model=floating`؛ `POST /license/floating/checkout`، `checkin`، `heartbeat`. |
| جلسهٔ شناور | Floating Session | یک سهم اشغال‌شده از لایسنس شناور تا زمان انقضا. | جدول `floating_sessions`؛ اگر heartbeat نکند منقضی می‌شود. |
| افزودنی | Addon | قابلیت یا سهمیهٔ اضافی که می‌توان به یک لایسنس ضمیمه کرد. | جداول `addons` و `license_addons`؛ قابلیت افزودنی، قابلیت پلن را برای همان feature جایگزین می‌کند. |
| انتشار (نسخه) | Release | یک نسخهٔ نرم‌افزاری معین از یک محصول. | جدول `releases`؛ وضعیت‌های `draft`/`published`/`yanked`، یکتا به‌ازای `(product, version)`. |
| فایل نسخه | Artifact | فایل باینری یک نسخه برای یک platform مشخص. | جدول `release_artifacts`؛ شامل `sha256`، `ed25519_sig` و `tauri_signature`. |
| کانال | Channel | مسیر انتشار: پایدار یا پیش‌انتشار. | `stable`/`beta`/`alpha`/`dev`؛ هر کانال فید جدا دارد. |
| فید به‌روزرسانی | Update Feed | خوراکی که آپدیتر مشتری برای یافتن نسخهٔ جدید می‌خواند. | `feed.xml` (Sparkle)، `feed.json` (Velopack)، `upgrade.json` (Tauri). |
| مانیفست صحت | Integrity Manifest | فهرست نسخه + hash + امضای هر فایل که صحت دانلود را تضمین می‌کند. | در Keygate، «manifest» همان محتوای فید است که `sha256` و امضای Ed25519 هر artifact را حمل می‌کند. |
| SHA-256 | SHA-256 | تابع درهم‌سازی که اثر انگشت بایت‌های فایل را می‌دهد. | سرور پس از آپلود، خودش hash را محاسبه و تثبیت می‌کند. |
| URL پیش‌امضاشده | Presigned URL | لینک موقت به object storage که خودش مجوز دسترسی دارد. | برای آپلود (PUT) و دانلود (GET) فایل‌های نسخه استفاده می‌شود. |
| کلید API | API Key | کلید برنامه‌ای برای دسترسی سرور‌به‌سرور. | قالب `kg_live_…`؛ جدول `api_keys`؛ فقط hash ذخیره می‌شود. |
| دامنهٔ دسترسی | Scope | مجوزهای یک کلید API. | `admin` (همه‌کاره)، `licenses:write`، `releases:write`. |
| نشست | Session | ورود تعاملی انسان به پنل با کوکی. | JWT در کوکی HttpOnly؛ middleware `SessionAuth`. |
| JWT | JSON Web Token | توکن امضاشدهٔ نشست. | امضای HS256 با `JWT_SECRET`. |
| یکتایی درخواست | Idempotency | تضمین اینکه تکرار یک درخواست، اثر دوباره نسازد. | هدر `Idempotency-Key` روی `activate`، `usage`، `floating/checkout`. |
| لاگ حسابرسی | Audit Log | ثبت اینکه چه کسی چه تغییری داد. | جدول `audit_logs`؛ فیلدهای `entity`، `action`، `actor_type`، `changes`. |
| وبهوک | Webhook | فراخوانی سرور مشتری هنگام رخداد. | جدول `webhooks`؛ رویدادها مثل `license.created`، `quota.exceeded`. |
| دورهٔ مهلت | Grace Period | بازه‌ای پس از انقضا که لایسنس هنوز کار می‌کند. | `grace_days` پلن؛ در محاسبهٔ `assertUsable` و عمر توکن لحاظ می‌شود. |
| اعتبار تا | Valid Until | تاریخی که خود لایسنس منقضی می‌شود. | فیلد `valid_until`؛ `null` یعنی دائمی. |
| به‌روزرسانی تا | Updates Until | پایان دورهٔ نگهداری لایسنس دائمی؛ نسخه‌های جدیدتر نصب نمی‌شوند. | فیلد `updates_until`؛ خطای `UPDATES_EXPIRED`. |
| عمر توکن | Token TTL | مدتی که توکن آفلاین معتبر می‌ماند (بازهٔ check-in). | پیش‌فرض ۷ روز؛ قابل تنظیم با `token_ttl_days` پلن. |
| شرکت/مستأجر | Tenant / Product scope | جداسازی دادهٔ هر محصول. | هر لایسنس `product_id` دارد؛ کلید API محصول‌محور فقط روی همان محصول عمل می‌کند. |
| محدودیت نرخ | Rate Limit | سقف تعداد درخواست در بازه. | middleware `RateLimitByIPScoped`؛ پیش‌فرض‌ها در `config`. |
| محافظت از حدس زدن | Brute-force Protection | قفل موقت پس از چند شکست پیاپی. | `LicenseBruteForceGuard` روی `/license/*`؛ خطای `LOCKED_OUT`. |
| کد یک‌بارمصرف | OTP | کدی که با ایمیل می‌آید و برای ورود استفاده می‌شود. | `POST /auth/otp/send` و `/auth/otp/verify`. |
| انتساب | Attribution | نمایش «Powered by Keygate» به‌عنوان الزام مجوز. | `attribution_text`/`attribution_url` در `/config`، هدر و پاورقی ایمیل‌ها. |
| حذف نرم | Soft Delete / Tombstone | رکورد پاک نمی‌شود؛ فقط از وضعیت «فعال» خارج می‌شود. | در `activations`: `status='deactivated'` + `deactivated_at` + `deactivated_reason`. |
| طرح شناسه | Identifier Scheme | برچسب شکل شناسهٔ دستگاه. | `kgid-v1`، `sha256-v1`، `uuid`، `opaque`، `legacy` (ستون `scheme`). |
| شناسهٔ دستگاه Keygate | KGID | شناسهٔ مرکب نسخه‌دار از چند مشخصهٔ سخت‌افزاری. | `kgid:v1:<platform>:<64 hex>`؛ ساخته‌شده با `pkg/deviceid` یا `DeviceIdentifier`. |
| رانش سخت‌افزاری | Hardware Drift | تغییر جزئی سخت‌افزار (رم، دیسک، NIC) بدون عوض‌شدن کل دستگاه. | `EvaluateDrift` با وزن `board/os/disk/cpu/nic` و آستانه‌های ۶۰/۳۰. |
| اثبات مالکیت | Proof of Possession | امضای یک challenge با کلید سخت‌افزاری (TPM/Secure Enclave). | `VerifyProofOfPossession` در `pkg/deviceid`؛ فعلاً در سرور استفاده نمی‌شود. |
| مانیفست صحت | Integrity Manifest | فهرست امضاشدهٔ فایل‌ها با `sha256` و اندازه. | قالب `keygate-integrity-v1`؛ `pkg/integrity` و `cmd/keygate-manifest`. |
| پاکت امضا | Signed Envelope | ظرفی که payload کانونیکال و امضا را کنار هم نگه می‌دارد. | `{format, manifest_version, payload, signature, signing_key_id, algorithm}`. |
