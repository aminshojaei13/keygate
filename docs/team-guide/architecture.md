# معماری Keygate

این سند ابتدا معماری را ساده و بعد فنی توضیح می‌دهد. تمام اجزای نام‌برده‌شده در کد واقعی همین مخزن وجود دارند.

---

## ۱. در یک نگاه ساده

Keygate سه بازیگر دارد:

1. **نرم‌افزار مشتری (Client / App)**: برنامه‌ای که کاربر اجرا می‌کند. با کلید لایسنس به Keygate می‌گوید «من کی هستم و روی چه دستگاهی اجرا می‌شوم» و در ازای آن **توکن امضاشده** می‌گیرد.
2. **اپراتور شما (Admin)**: با پنل مدیریت محصول، طرح، لایسنس، نسخه، کلید امضا و تنظیمات را اداره می‌کند.
3. **خریدار (Customer)**: با پورتال مشتری لایسنس‌های خودش، دستگاه‌های فعال، تیم، صورتحساب و تمدید را می‌بیند.

سرور Keygate در وسط می‌ایستد و این کارها را می‌کند: نگه‌داشتن وضعیت لایسنس در PostgreSQL، صدور توکن آفلاین، سرو کردن فید آپدیت، و نگه‌داشتن فایل‌های نسخه روی ذخیره‌سازی S3.

## ۲. تصویر کلی معماری

```mermaid
graph TB
    subgraph Client["نرم‌افزار مشتری (هر زبانی)"]
        APP["برنامه دسکتاپ / CLI / سرویس<br/>C#، Python، Go، TypeScript ..."]
        VERIFY["بررسی امضای توکن آفلاین<br/>Ed25519 + کلید عمومی"]
    end

    subgraph Browser["مرورگر"]
        ADMIN["پنل مدیریت<br/>/admin"]
        PORTAL["پورتال مشتری<br/>/portal"]
    end

    subgraph Keygate["Keygate (یک باینری Go)"]
        GIN["Gin Router + Middlewares<br/>rate limit، brute force، idempotency، session/API key"]
        PUB["API عمومی SDK<br/>/api/v1/license/*"]
        FEED["فید آپدیت عمومی<br/>/api/v1/releases/:slug/*"]
        PAPI["API مدیریتی<br/>/api/v1/admin/*"]
        CAPI["API پورتال<br/>/api/v1/portal/*"]
        AUTH["احراز هویت<br/>OTP + JWT cookie + kg_live_ keys"]
        SVC["لایه سرویس<br/>License / Usage / Seats / Floating / Release / Webhook / Email"]
        SPA["سرو فایل‌های React<br/>web/dist + SPA fallback"]
    end

    subgraph Data["داده"]
        PG[("PostgreSQL 18<br/>licenses, activations, plans,<br/>releases, audit_logs, settings, ...")]
        S3[("S3 / R2 / MinIO<br/>release artifacts")]
        REDIS[("Redis (اختیاری)<br/>rate limit مشترک")]
    end

    STRIPE["Stripe<br/>Checkout + Webhooks"]
    SMTP["SMTP یا Cloudflare<br/>ایمیل"]

    APP -->|"HTTPS: activate / verify /<br/>entitlements / usage / download"| GIN
    VERIFY -.->|"کلید عمومی: GET /license/pubkey"| GIN
    ADMIN --> GIN
    PORTAL --> GIN
    GIN --> AUTH --> SVC
    GIN --> PUB & FEED & PAPI & CAPI
    PUB & PAPI & CAPI & FEED --> SVC
    SVC --> PG
    SVC --> S3
    SVC --> REDIS
    SVC <--> STRIPE
    SVC --> SMTP
    GIN --> SPA
```

## ۳. اجزای فنی

### ۳.۱ فرانت‌اند (`web/`)

* React 19 + Vite 8 + TypeScript + Tailwind 4 + TanStack Query + React Router 7.
* خروجی build در `web/dist` قرار می‌گیرد و **توسط خود باینری Go سرو می‌شود** (`serveFrontend` در `cmd/server/main.go`). مسیرهای `/api/`, `/pay/`, `/health`, `/metrics`, `/docs` به روتر می‌رسند و هر مسیر دیگری فایل استاتیک یا `index.html` است.
* نام فایل‌های زیر `/assets/` هش محتوا دارد و `Cache-Control: immutable` می‌گیرند؛ `index.html` همیشه `no-cache` است.
* صفحه‌های ادمین در `web/src/pages/admin/` و پورتال در `web/src/pages/portal/` هستند (فهرست کامل در [admin-panel-guide.md](admin-panel-guide.md)).
* کلاینت HTTP در `web/src/lib/api.ts` است و پوشش `{success, data}` سرور را در همان‌جا باز می‌کند.

### ۳.۲ بک‌اند (`cmd/server`, `internal/`)

* Gin، مدل‌سازی با **bun** روی `pgdriver`، PostgreSQL 18.
* تمام routeها در `cmd/server/main.go` ثبت می‌شوند: **۱۵۰ ثبت مسیر** به‌علاوه‌ی `StaticFile("/docs/openapi.yaml")` (جزئیات شمارش در [documentation-audit.md](documentation-audit.md)).
* تفکیک گروه‌ها بر اساس مدل اعتماد:
  * `/api/v1/license/*` — عمومی؛ **کلید لایسنس خودش اعتبارنامه است**.
  * `/api/v1/releases/:product_slug/*` — فید عمومی؛ اعتماد از امضای فایل می‌آید نه از مخفی بودن URL.
  * `/api/v1/portal/*` — نشست مشتری (کوکی).
  * `/api/v1/admin/*` — نشست ادمین یا API key با scope مناسب.
* پردازش‌های پس‌زمینه در `main.go`: پاک‌سازی نشست‌های floating منقضی، ارسال صف ایمیل، اجرای بررسی‌های انقضا، همگام‌سازی مصرف با Stripe، تلاش مجدد webhook، و اسنپ‌شات تحلیلی.

### ۳.۳ پایگاه داده

PostgreSQL؛ جدول‌های اصلی: `users`, `products`, `plans`, `entitlements`, `licenses`, `activations`, `seats`, `usage_events`, `usage_counters`, `addons`, `license_addons`, `floating_sessions`, `releases`, `release_artifacts`, `release_signing_keys`, `audit_logs`, `webhooks`, `webhook_deliveries`, `subscriptions`, `settings`, `otp_codes`, `refresh_tokens`, `email_queue`, `idempotency_keys`, `analytics_snapshots`, `metered_billing`, `license_renewals`, `plan_update_terms`, `schema_migrations`.

نکته‌ی کلیدی: ستون `licenses.license_key` مقدار **هش SHA-256** کلید را نگه می‌دارد و نسخه‌ی رمزنگاری‌شده‌ی کلید در `license_key_encrypted` است (`AES-256-GCM` با کلید مشتق‌شده از `RELEASE_KEY_ENCRYPTION_KEY`). مدل `model.License` فیلد کلید را با `json:"-"` مخفی می‌کند تا به‌طور تصادفی از یک پاسخ مدیریتی بیرون نریزد.

### ۳.۴ ذخیره‌سازی فایل انتشار

پشته‌ی S3 (`internal/storage`) با پشتیبانی از R2/S3/MinIO و `STORAGE_FORCE_PATH_STYLE`. آپلود از مرورگر با presigned PUT انجام می‌شود، سپس سرور خودش فایل را از باکت می‌خواند و `sha256` رسمی را محاسبه می‌کند (`FinalizeArtifact`) — یعنی هش اعلامی کلاینت، «حقیقت» نیست.

### ۳.۵ احراز هویت

* مشتری/ادمین انسانی: کد یک‌بارمصرف ۶ رقمی با ایمیل (`/auth/otp/send`, `/auth/otp/verify`) → کوکی نشست ۲۴ ساعته + کوکی refresh با عمر ۳۰ روز و مسیر محدود `/api/v1/auth/refresh`.
* سرور به سرور: کلید API با پیشوند `kg_live_` و scopeهای `admin` / `licenses:write` / `releases:write`.
* SDK عمومی: هدر یا بدنه‌ی حاوی `license_key` (و برای فیدها، `X-License-Key` یا توکن امضاشده).
* جزئیات کامل در [authentication-and-security.md](authentication-and-security.md).

### ۳.۶ SDKها

| مسیر | زبان | وضعیت |
| --- | --- | --- |
| `sdk/python` | Python | موجود؛ بدون وابستگی خارجی (پیاده‌سازی خالص Ed25519)؛ چند ناسازگاری قراردادی دارد |
| `examples/dotnet/Keygate.Sample` | C#/.NET | نمونه‌ی آموزشی این مخزن؛ SDK رسمی وجود ندارد |
| `internal/license` + `pkg/integrity` | Go | مرجع رسمی امضا/بررسی |

هیچ کد Go برای کلاینت منتشر نشده است؛ API زبانی‌مستقل است و قرارداد آن در [api-reference.md](api-reference.md) آمده است.

## ۴. جریان فعال‌سازی لایسنس (Activation)

```mermaid
sequenceDiagram
    autonumber
    participant App as نرم‌افزار مشتری
    participant API as Keygate API
    participant BF as BruteForceGuard
    participant DB as PostgreSQL
    participant WH as Webhook

    App->>App: ساخت شناسه دستگاه<br/>(kgid یا sha256 یا uuid)
    App->>API: POST /api/v1/license/activate<br/>{license_key, identifier, identifier_type?, label?}
    API->>BF: آیا این IP قفل است؟
    BF-->>API: خیر
    API->>DB: FindLicenseByKey (جستجو با هش کلید)
    DB-->>API: رکورد لایسنس + طرح + قابلیت‌ها
    API->>API: assertUsable(status, valid_until, grace)
    alt لایسنس قابل استفاده نیست
        API-->>App: 403 LICENSE_EXPIRED / SUSPENDED / REVOKED / CANCELED
        API->>DB: audit(activation_failed)
    else قابل استفاده است
        API->>DB: FindActivation(license, identifier)
        alt از قبل فعال است
            DB-->>API: رکورد موجود
            API-->>App: 200 {status: "already_activated", token}
        else فعال نیست
            API->>DB: ActivateWithinLimit (قفل تراکنشی + شمارش سهمیه)
            alt سهمیه پر است
                DB-->>API: ErrActivationLimit
                API-->>App: 409 ACTIVATION_LIMIT {max, current}
            else جا دارد
                DB-->>API: activation ساخته شد
                API->>WH: dispatch(license.activated)
                API->>API: signToken → Ed25519
                API-->>App: 200 {status:"activated", license_id, token, meta}
            end
        end
    end
    App->>App: ذخیره توکن + شناسه دستگاه برای بررسی آفلاین
```

نکته‌های مهم این جریان، همه مستخرج از `internal/service/license.go`:

* محدودیت تعداد دستگاه به‌صورت **اتمی و تراکنشی** اعمال می‌شود؛ دو درخواست هم‌زمان نمی‌توانند از سقف رد شوند.
* اگر کلاینت `previous_identifier` بفرستد، فعال‌سازی قدیمی **درجا منتقل** می‌شود و سهمیه‌ی اضافی مصرف نمی‌شود (مهاجرت طرح شناسه).
* `Activate` وضعیت دقیق را برمی‌گرداند؛ برخلاف `Verify` که همه‌ی حالت‌ها را در `404` جمع می‌کند.

## ۵. جریان اعتبارسنجی آفلاین (Offline Verification)

```mermaid
sequenceDiagram
    autonumber
    participant App as نرم‌افزار مشتری
    participant API as Keygate API
    participant Local as حافظه محلی دستگاه

    Note over App,Local: مرحله ۱ — گرفتن کلید عمومی (یک‌بار، سپس cache می‌شود)
    App->>API: GET /api/v1/license/pubkey
    API-->>App: {kid, algorithm:"ed25519", public_key:"<hex>", format:"hex"}
    App->>Local: ذخیره کلید عمومی + kid

    Note over App,Local: مرحله ۲ — فعال‌سازی/اعتبارسنجی آنلاین و گرفتن توکن
    App->>API: POST /api/v1/license/verify
    API-->>App: {status, plan_id, features, token, grace_days, valid_until?}
    App->>Local: ذخیره توکن

    Note over App,Local: مرحله ۳ — قطع اینترنت: بررسی محلی
    App->>Local: خواندن توکن
    App->>App: جدا کردن payload.signature
    App->>App: ed25519.Verify(pubkey, ascii(payload_b64), sig)
    alt امضا نامعتبر است
        App->>App: رد کن (توکن جعلی یا دست‌کاری‌شده)
    else امضا معتبر است
        App->>App: بررسی exp (پایان بازه آفلاین)
        App->>App: بررسی vun + grc (پایان ترم لایسنس + مهلت)
        App->>App: بررسی pid و did/fpr (محصول و دستگاه درست)
        App->>App: بررسی upd (سقف نسخه‌های مجاز)
        App->>App: خواندن ftr (قابلیت‌های مجاز)
        App->>App: اجازه اجرا در بازه معتبر
    end
```

کلید خصوصی امضا **هرگز** از سرور خارج نمی‌شود؛ فقط کلید عمومی منتشر می‌شود. جزئیات دقیق ساختار توکن در [offline-licensing-guide.md](offline-licensing-guide.md).

## ۶. جریان انتشار نسخه و آپدیت

```mermaid
sequenceDiagram
    autonumber
    participant CI as CI/CD یا ادمین
    participant API as Keygate
    participant S3 as S3/R2
    participant App as Updater کلاینت

    CI->>API: POST /admin/releases {product_id, version, channel}
    API-->>CI: release با وضعیت draft
    CI->>API: POST /admin/releases/:id/artifacts {platform, filename?}
    API-->>CI: {artifact, upload_url, expires_at}
    CI->>S3: PUT فایل با upload_url
    CI->>API: POST /admin/releases/:id/artifacts/:aid/finalize
    API->>S3: خواندن فایل و محاسبه sha256 (مرجع سرور)
    API-->>CI: artifact با sha256 و file_size
    CI->>API: POST /admin/releases/:id/actions/publish
    API->>API: امضای هر artifact با کلید فعال Ed25519 محصول
    API-->>CI: release منتشرشده (status=published)
    App->>API: GET /releases/:slug/feed.json (یا feed.xml / upgrade.json)
    API-->>App: فید + URL امضاشده فایل + sha256 + ed25519_sig
    App->>S3: دانلود فایل
    App->>App: verify امضا و sha256 قبل از اجرا
```

* ترتیب چک‌ها در انتشار (`ReleaseService.Publish`): همه‌ی artifactها باید آپلود شده باشند → اگر `require_signing` روشن است، کلید فعال اجباری است (وگرنه `409`) → emit امضا → تغییر وضعیت.
* اگر محصول کلید امضا نداشته باشد و `require_signing=false` باشد، نسخه **عمداً unsigned** منتشر می‌شود و امضاهای قبلی حذف می‌شوند (تا کلاینت امضای کلید بازنشسته را نبیند).
* مانیفست صحت فایل‌ها (`Integrity Manifest`) بخشی از این جریان نیست و ابزار جداگانه‌ای است — بخش ۷ را ببینید.

## ۷. مانیفست صحت فایل‌ها (Integrity Manifest)

`cmd/keygate-manifest` یک CLI مستقل است (`create` / `verify` / `inspect`) که با `pkg/integrity` کار می‌کند: فایل‌های یک پوشه را هش می‌کند، مانیفست را کانونیکال می‌کند، با Ed25519 امضا می‌کند و در یک پوش `keygate-integrity-v1` می‌گذارد. مهم: **هیچ endpoint سروری این مانیفست را نمی‌سازد، امضا نمی‌کند یا توزیع نمی‌کند.** برای یکپارچه‌سازی نیاز به تصمیم تیم است. جزئیات در [integrity-and-release-guide.md](integrity-and-release-guide.md).

## ۸. کدام بخش‌ها زبانی‌مستقل‌اند و کدام وابسته به SDK؟

| بخش | زبانی‌مستقل؟ | توضیح |
| --- | --- | --- |
| فراخوانی `activate` / `verify` / `deactivate` | ✅ | فقط HTTP + JSON؛ در C#، Python، Go، TypeScript و … یکسان |
| بررسی امضای توکن آفلاین | ✅ | به کتابخانه‌ی Ed25519 در زبان خودتان نیاز دارد (مثلاً `System.Security.Cryptography` یا `cryptography`) |
| `entitlements` / `usage` | ✅ | JSON خالص |
| فیدهای آپدیت | ✅ | قالب‌های Sparkle / Velopack / Tauri استاندارد صنعت هستند |
| بررسی امضای artifact | ✅ از نظر الگوریتم (Ed25519) | اما **قالب امضای Tauri** یک فایل minisign است؛ اگر از آپدیت‌کننده‌ی Tauri استفاده می‌کنید، ابزار خودش این کار را می‌کند |
| مانیفست صحت | ⚠️ | قرارداد JSON مشخص است، اما ابزار مرجع فقط به‌صورت CLI Go وجود دارد |
| SDK رسمی | ❌ | SDK دات‌نت رسمی وجود ندارد؛ نمونه‌ی `examples/dotnet` آموزشی است |

## ۹. خطاها و «پوشش» پاسخ‌ها در یک نگاه

هر پاسخ موفق `200/201` با پوشش `{"success": true, "data": ...}` می‌آید و هر خطا با `{"success": false, "error": {"code", "message", "details?"}}`. لیست‌ها همیشه `total`، `limit` و `offset` را هم برمی‌گردانند و یک مجموعه‌ی خالی به‌صورت `[]` برمی‌آید، نه `null`. مستثناها: فیدها (XML/JSON خام و ساختار Velopack/Tauri)، `204` در Tauri وقتی آپدیتی نیست، و `/health`, `/metrics`, `/docs`.
