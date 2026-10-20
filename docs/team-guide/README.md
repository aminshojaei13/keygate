# مستندات تیمی Keygate

این پوشه `docs/team-guide/` مرجع فارسی کار با پروژه **Keygate** برای اعضای تیم است.
تمام مطالب بر پایه کد همین مخزن نوشته شده‌اند، نه بر پایهٔ مستندات عمومی نسخه‌های دیگر.
هرجا رفتار کد با مستندات قدیمی (مثل `docs/openapi.yaml` یا `docs/api-contract.md`) تفاوت داشته باشد،
در فایل `documentation-audit.md` صریحاً ثبت شده است.

> زبان: توضیحات فارسی‌اند. نام‌های فنی، مسیرها، نام endpointها، نام فیلدها، کدها و پیام‌های اصلی خطا
> به‌صورت اصلی (انگلیسی) باقی مانده‌اند تا با کد و ترمینال قابل تطبیق باشند.

> **وضعیت تأیید (اجرا شده روی همین شاخه):** `go vet ./...` و `go build ./...` بدون خطا؛
> `go test ./...` بدون دیتابیس **همهٔ ۱۸ پکیج را پاس کرد** (تست‌های DB-محور skip شدند)؛
> تست‌های پایتون (`sdk/python` + `test/*.py`) همه سبز؛ نمونهٔ C# روی .NET 8 با
> `0 Warning(s), 0 Error(s)` بیلد شد و چرخهٔ activate → verify → offline به‌صورت end-to-end
> تأیید شد. جزئیات و محدودیت‌ها در [`documentation-audit.md`](documentation-audit.md) §۱۱.

## شروع سریع

- می‌خواهید پروژه را بالا بیاورید؟ → [`getting-started.md`](getting-started.md)
- می‌خواهید معماری را بفهمید؟ → [`architecture.md`](architecture.md)
- می‌خواهید API را صدا بزنید؟ → [`api-reference.md`](api-reference.md)
- می‌خواهید کلاینت C#/.NET بنویسید؟ → [`csharp-dotnet-integration.md`](csharp-dotnet-integration.md)
- می‌خواهید Keygate را به یک نرم‌افزار جدید وصل کنید؟ → [`python-and-typescript-examples.md`](python-and-typescript-examples.md)
- می‌خواهید بفهمید کدام مستند قابل‌اعتماد نیست؟ → [`documentation-audit.md`](documentation-audit.md)

فهرست کامل هر ۱۳ فایل در بخش «۵. راهنمای فایل‌های مستندات» (پایین همین صفحه) آمده است.

---

## ۱. Keygate چیست؟

Keygate یک پلتفرم مدیریت لایسنس نرم‌افزار (Software License Management) است که خودتان آن را
میزبانی (self-host) می‌کنید. جایگزین باز‌متن (open-source) برای سرویس‌هایی مانند Keygen، Cryptlex و
LicenseSpring است.

کار اصلی Keygate این است که به شما بگوید **چه کسی**، **چقدر** و **از چه دستگاهی** می‌تواند از
نرم‌افزار شما استفاده کند، و این تصمیم را هم در حالت آنلاین و هم به‌صورت آفلاین قابل اعمال کند.

### چه مشکلی را حل می‌کند؟

* صدور لایسنس و توزیع آن به مشتری.
* فعال‌سازی (Activation) روی دستگاه‌های مشخص و محدود کردن تعداد دستگاه‌ها.
* اعتبارسنجی (Validation) لایسنس در زمان اجرای نرم‌افزار.
* کارکرد آفلاین مشتری با یک توکن امضاشدهٔ دیجیتال.
* فروش اشتراکی (Subscription) و لایسنس دائمی (Perpetual) به‌همراه دورهٔ نگهداری (Maintenance).
* مدیریت قابلیت‌ها و سهمیه‌ها (Entitlements / Quotas) در هر پلن.
* توزیع نسخه‌های نرم‌افزار (Releases) و فید به‌روزرسانی خودکار برای
  Sparkle، Velopack و Tauri.
* امضای دیجیتال نسخه‌ها و تضمین صحت (Integrity) فایل‌های دانلودی.
* شناسهٔ دستگاه نسخه‌دار (`kgid:v1`) و چرخهٔ عمر فعال‌سازی با «حذف نرم» (tombstone).
* ابزار مستقل مانیفست صحت فایل‌ها (`cmd/keygate-manifest` + `pkg/integrity`).
* SDK مرجع پایتون بدون وابستگی خارجی (`sdk/python`).
* مدیریت مشتری، پلن، افزودنی (Addon)، صندلی (Seat)، تیم و گزارش‌های حسابرسی (Audit Log).

---

## ۲. اجزای اصلی سیستم

| جزء | فناوری | نقش |
|---|---|---|
| Backend / API | Go 1.27 + Gin | همهٔ endpointهای عمومی، پورتال و مدیریتی |
| دیتابیس | PostgreSQL 18 (Bun ORM) | نگه‌داری لایسنس، دستگاه، پلن، release، تیم و… |
| فرانت‌اند | React + Vite + TypeScript | پنل مدیریت و پورتال مشتری (SPA) |
| Rate limit / cache | Redis یا Valkey (اختیاری) | اشتراک محدودیت نرخ بین چند نمونه |
| Object storage | S3 / Cloudflare R2 / MinIO (اختیاری) | نگه‌داری فایل‌های نسخه‌ها |
| پرداخت | Stripe | فروش و اشتراک (اختیاری برای نصب پایه) |
| Email | SMTP یا Cloudflare (اختیاری) | کد ورود (OTP)، دعوت‌نامه، اعلان‌ها |
| امضای لایسنس | Ed25519 | توکن آفلاین قابل‌اعتبارسنجی |
| امضای نسخه | Ed25519 + minisign | امضای artifactهای release |
| مانیفست صحت | `pkg/integrity` + `cmd/keygate-manifest` | امضای فهرست فایل‌های نصب‌شده (خارج از سرور) |
| SDK پایتون | Python stdlib | کلاینت مرجع فعال‌سازی/بررسی آفلاین/مانیفست |

---

## ۳. معماری کلی (خلاصه)

```
        ┌───────────────────────────┐
        │        Client App         │
        │  (C#/.NET, Rust, ...)     │
        └─────────────┬─────────────┘
                      │ HTTPS (license_key)
                      ▼
  ┌─────────────────────────────────────────────┐
  │                Keygate Server               │
  │  Go / Gin                                   │
  │  ├── /api/v1/license/*      (عمومی، SDK)     │
  │  ├── /api/v1/releases/*     (فید عمومی)      │
  │  ├── /api/v1/portal/*       (پورتال، session)│
  │  └── /api/v1/admin/*        (API مدیریتی)    │
  └───────┬───────────────┬──────────────┬───────┘
          │               │              │
          ▼               ▼              ▼
   ┌────────────┐  ┌────────────┐  ┌──────────────┐
   │ PostgreSQL │  │  Storage   │  │    Stripe    │
   │            │  │ (S3/R2/…)  │  │  (optional)  │
   └────────────┘  └────────────┘  └──────────────┘
```

توضیح کامل اجزا و نمودارهای دقیق در `architecture.md` آمده است.

---

## ۴. از کجا شروع کنم؟

اگر تازه به تیم اضافه شده‌اید، این ترتیب را دنبال کنید:

1. [`architecture.md`](architecture.md) — بفهمید سیستم از چه بخش‌هایی ساخته شده و داده کجا می‌رود.
2. [`getting-started.md`](getting-started.md) — پروژه را روی سیستم خودتان بالا بیاورید.
3. [`admin-panel-guide.md`](admin-panel-guide.md) — با پنل مدیریت و امکاناتش آشنا شوید.
4. [`authentication-and-security.md`](authentication-and-security.md) — مدل امنیتی و تفاوت انواع API را بفهمید.
5. [`api-reference.md`](api-reference.md) — مرجع کامل همهٔ endpointها.
6. [`offline-licensing-guide.md`](offline-licensing-guide.md) و [`integrity-and-release-guide.md`](integrity-and-release-guide.md) — مفاهیم پیشرفته.
7. [`csharp-dotnet-integration.md`](csharp-dotnet-integration.md) — پیاده‌سازی در نرم‌افزار C#/.NET.
8. [`python-and-typescript-examples.md`](python-and-typescript-examples.md) — نمونه‌های زبان‌های دیگر.
9. [`deployment-troubleshooting-and-team-workflows.md`](deployment-troubleshooting-and-team-workflows.md) — استقرار و عیب‌یابی.
10. [`glossary-fa.md`](glossary-fa.md) — واژه‌نامه اصطلاحات.

---

## ۵. راهنمای فایل‌های مستندات

| فایل | موضوع | مخاطب |
|---|---|---|
| [`README.md`](README.md) | همین صفحه؛ معرفی کلی و نقشهٔ مستندات | همه |
| [`getting-started.md`](getting-started.md) | نصب، پیکربندی و اجرای پروژه (محلی و Docker) | تازه‌وارد |
| [`architecture.md`](architecture.md) | معماری، جریان داده و نمودار اجزا | همه |
| [`admin-panel-guide.md`](admin-panel-guide.md) | راهنمای صفحه‌به‌صفحهٔ پنل مدیریت و پورتال | ادمین / پشتیبانی |
| [`api-reference.md`](api-reference.md) | مرجع کامل endpointها با قالب یکسان | توسعه‌دهندهٔ backend/یکپارچه‌ساز |
| [`authentication-and-security.md`](authentication-and-security.md) | احراز هویت، scope، idempotency، محدودیت نرخ، امنیت device | همه |
| [`offline-licensing-guide.md`](offline-licensing-guide.md) | توکن آفلاین، امضای Ed25519، کلید عمومی | توسعه‌دهندهٔ کلاینت |
| [`integrity-and-release-guide.md`](integrity-and-release-guide.md) | release، امضای نسخه، صحت فایل (manifest) | تیم انتشار |
| [`csharp-dotnet-integration.md`](csharp-dotnet-integration.md) | پیاده‌سازی کامل لایسنس در C#/.NET | توسعه‌دهندهٔ دسکتاپ |
| [`python-and-typescript-examples.md`](python-and-typescript-examples.md) | نمونه فراخوانی API با Python و TypeScript | اسکریپت‌نویس / وب |
| [`deployment-troubleshooting-and-team-workflows.md`](deployment-troubleshooting-and-team-workflows.md) | استقرار، لاگ، عیب‌یابی و روال کاری تیم | DevOps / همه |
| [`glossary-fa.md`](glossary-fa.md) | واژه‌نامهٔ فارسی اصطلاحات | همه |
| [`documentation-audit.md`](documentation-audit.md) | ممیزی مستندات: تعداد route، تفاوت‌ها، نقاط بدون تست | سرپرست فنی |

### مستندات تخصصی موجود در مخزن (انگلیسی)

این راهنما جایگزین مستندات رسمی پروتکل‌ها نیست؛ برای جزئیات بایت‌به‌بایت به این فایل‌ها
در پوشهٔ `docs/` مراجعه کنید:

| فایل | موضوع |
|---|---|
| [`../device-identity.md`](../device-identity.md) | مشخصات پروتکل شناسهٔ دستگاه نسخهٔ ۱ (`kgid:v1`، مهاجرت با `previous_identifier`) |
| [`../integrity-manifest-format.md`](../integrity-manifest-format.md) | قالب مانیفست صحت `keygate-integrity-v1` |
| [`../integrity-verification.md`](../integrity-verification.md) | الگوریتم بررسی مانیفست و فایل‌های نصب‌شده |
| [`../offline-licensing.md`](../offline-licensing.md) | مشخصات توکن آفلاین و قواعد اعتبارسنجی |
| [`../client-integration.md`](../client-integration.md) | راهبرد یکپارچه‌سازی مستقل از زبان و معماری SDK |
| [`../security-review.md`](../security-review.md) | بازبینی امنیتی این شاخه و یافته‌های باز/بسته |
| [`../configuration.md`](../configuration.md) | مرجع کامل متغیرهای محیطی |
| [`../installation.md`](../installation.md) | نصب production و reverse proxy |

---

## ۶. نکات مهم پیش از شروع

* **مجوز AGPL v3 (بخش ۷(b))**: نمایش «Powered by Keygate» یک الزام حقوقی مجوز است، نه یک
  قابلیت قابل حذف. این متن در پنل مدیریت، پورتال مشتری، صفحهٔ ورود، پاورقی ایمیل‌ها و پاسخ‌های API
  (فیلدهای `attribution_text` / `attribution_url` و هدر `X-Powered-By`) ظاهر می‌شود و نباید حذف
  یا تغییر داده شود. برای حذف آن به مجوز تجاری نیاز است (`hello@keygate.app`).
* **کلیدها هرگز در کلاینت قرار نمی‌گیرند**: کلید API مدیریتی (`kg_live_…`) و کلید خصوصی امضا
  (private key) نباید در فایل اجرایی دسکتاپ، فرانت‌اند یا مخزن کد قرار بگیرند. جزئیات در
  `authentication-and-security.md`.
* **عمومی بودن فیدها عمدی است**: تطبیق امنیت release از طریق امضای Ed25519 هر artifact انجام
  می‌شود، نه پنهان‌کاری آدرس. جزئیات در `integrity-and-release-guide.md`.
* در این مستندات هیچ رمز، توکن یا کلید واقعی وجود ندارد؛ همهٔ نمونه‌ها ساختگی‌اند.
