# Keygate.Protection — راهنمای فارسی

نسخه‌ی فارسی مستندات SDK دات‌نت Keygate. مرجع دقیق و به‌روز، فایل
[`README.md`](README.md) است؛ این سند همان محتوا را برای تیم فارسی‌زبان
خلاصه می‌کند.

## فهرست

- [این SDK چه می‌کند](#این-sdk-چه-می‌کند)
- [نصب](#نصب)
- [شروع سریع](#شروع-سریع)
- [جدول API](#جدول-api)
- [ASP.NET Core](#aspnet-core)
- [هویت دستگاه](#هویت-دستگاه)
- [بررسی یکپارچگی فایل‌ها](#بررسی-یکپارچگی-فایل‌ها)
- [گزارش رویداد امنیتی و سیاست‌های سرور](#گزارش-رویداد-امنیتی-و-سیاست‌های-سرور)
- [محدودیت‌ها](#محدودیت‌ها)
- [چک‌لیست تولید](#چک‌لیست-تولید)
- [تست‌ها](#تست‌ها)

## این SDK چه می‌کند

سه تهدید اصلی را پوشش می‌دهد:

1. **انتقال نرم‌افزار یا توکن به دستگاه دیگر** — هر فعال‌سازی به یک شناسه‌ی
   پایدار و مشتق از سخت‌افزار گره می‌خورد (`kgid:v1:<platform>:<sha256>`)، و
   توکن لایسنس هم با اثر انگشت دستگاه امضا می‌شود.
2. **دست‌کاری فایل‌های محافظت‌شده** — مانیفست SHA-256 امضاشده با Ed25519،
   بررسی هنگام راه‌اندازی و به‌صورت دوره‌ای.
3. **گزارش تخلف و واکنش سمت سرور** — رویدادهای امنیتی امضاشده با کلید
   دستگاه، و سیاست‌هایی که می‌توانند لایسنس را flag/suspend/revoke کنند.

نکته‌ی مهم: این یک لایه‌ی **بازدارنده** است، نه اثبات امنیت مطلق. جزئیات در
بخش [محدودیت‌ها](#محدودیتها).

## نصب

```bash
dotnet add package Keygate.Protection
dotnet add package Keygate.Protection.AspNetCore   # فقط برای ASP.NET Core
```

یا با ارجاع مستقیم به پروژه در همین مخزن:

```xml
<ProjectReference Include="sdk/dotnet/Keygate.Protection/Keygate.Protection.csproj" />
```

| پکیج | Target | کاربرد |
| --- | --- | --- |
| `Keygate.Protection` | `netstandard2.0`, `net8.0` | هسته‌ی SDK (کنسول، دسکتاپ، سرویس، .NET Core 3.1، .NET Framework 4.6.2+) |
| `Keygate.Protection.AspNetCore` | `net8.0` | DI، middleware، اتریبیوت `[RequireKeygateFeature]` |
| `Keygate.Protection.Tests` | `net8.0` | تست‌های xUnit شامل وکتورهای پروتکل Go |

Ed25519 توسط `BouncyCastle.Cryptography` (کاملاً managed، بدون وابستگی native)
ارائه می‌شود. روی ویندوز حالت محلی با DPAPI رمز می‌شود؛ روی macOS و Linux یک
فایل با دسترسی `0600` است.

## شروع سریع

```csharp
using Keygate.Protection;

var options = new KeygateOptions
{
    BaseUrl            = "https://licenses.example.com",  // فقط https
    LicenseKey         = licenseKeyFromYourOwnConfig,
    ProductId          = "prod_...",                      // اکیداً توصیه می‌شود
    LicensePublicKey   = "…64 hex یا base64url…",          // پین‌کردن کلید امضا
    LicensePublicKeyId = "key_...",
    DeviceLabel        = "Acme Desktop",
};

using var protection = new KeygateProtection(options);

await protection.InitializeAsync();        // فقط محلی، بدون شبکه
if (protection.CurrentLicense is null)
    await protection.ActivateAsync();      // فقط اجرای اول؛ بعداً idempotent

LicenseValidation check = await protection.ValidateLicenseAsync();
if (!check.IsValid) { /* check.ProblemCode, check.Problem */ }

if (!protection.HasFeature("pro")) { /* مسیر ارتقا */ }
```

نمونه‌های قابل اجرا:

```bash
dotnet run --project examples/dotnet/Keygate.Sample.Console
dotnet run --project examples/dotnet/Keygate.Sample.Desktop
dotnet run --project examples/dotnet/Keygate.Sample.AspNetCore
```

## جدول API

| عضو | توضیح |
| --- | --- |
| `InitializeAsync()` | بارگذاری state محلی، ساخت شناسه‌ی دستگاه، تولید کلید Ed25519 دستگاه. بدون شبکه و بدون خطا در حالت فعال‌نشده. |
| `ActivateAsync()` | `POST /api/v1/license/activate` با `identifier`, `device_public_key`, `device_key_alg`. کلید امضای گزارش را ثبت می‌کند. |
| `ValidateLicenseAsync(forceOnline = false)` | `POST /api/v1/license/verify`، تأیید محلی توکن، و در صورت خطای شبکه fallback به توکن کش‌شده. |
| `CheckIntegrityAsync(installDirectory?)` | تأیید مانیفست امضاشده و فایل‌های نصب‌شده؛ گزارش خودکار در صورت مغایرت. |
| `ReportSecurityEventAsync(SecurityEvent)` | `POST /api/v1/license/security-events`، امضاشده با کلید دستگاه. |
| `HasFeature(name)` | بررسی entitlement روی توکن تأییدشده. |
| `IsUpdateAllowed(publishedAt)` | بررسی پنجره‌ی maintenance (`upd`). |
| `StartPeriodicValidation()` / `StopPeriodicValidation()` | اعتبارسنجی دوره‌ای در پس‌زمینه. |
| `DeactivateAsync()` | آزادکردن seat؛ کلید دستگاه می‌ماند تا فعال‌سازی مجدد همان هویت باشد. |
| `DeviceId`, `Device`, `CurrentLicense` | تشخیص و عیب‌یابی. |

خطاها:

- `KeygateConfigurationException` — پیکربندی غلط؛ باید در startup سریع شکست بخورد.
- `KeygateNetworkException` — DNS/TCP/TLS/timeout؛ **هرگز** به‌عنوان دستکاری تلقی نمی‌شود.
- `KeygateLicenseException` — با `Code` مثل `TOKEN_EXPIRED`, `DEVICE_MISMATCH`, `MISSING_FEATURE`.
- `KeygateApiException` — پاسخ خطای سرور با `Code` و `StatusCode`.

پاسخ قطعی سرور (400/401/403/404/409/410/422) به‌صورت لایسنس نامعتبر برگردانده
می‌شود و با کش پوشانده **نمی‌شود**. فقط خطای انتقال و 5xx/429 به توکن کش‌شده
برمی‌گردند.

## ASP.NET Core

```csharp
builder.Services.AddKeygate(o =>
{
    o.Protection.BaseUrl          = builder.Configuration["Keygate:BaseUrl"]!;
    o.Protection.LicenseKey       = builder.Configuration["Keygate:LicenseKey"];
    o.Protection.ProductId        = builder.Configuration["Keygate:ProductId"];
    o.Protection.LicensePublicKey = builder.Configuration["Keygate:PublicKey"];
    o.BlockOnInvalidLicense       = true;      // گیت کل اپلیکیشن
    o.PeriodicValidationInterval  = TimeSpan.FromHours(6);
});

var app = builder.Build();
app.UseKeygate();       // گیت لایسنس + هدر اتریبیوشن X-Powered-By
app.MapControllers();
```

- `AddKeygate` یک `KeygateProtection` singleton و یک hosted service ثبت می‌کند
  که در startup اعتبارسنجی کرده و تایمر پس‌زمینه را روشن می‌کند.
- `UseKeygate` درخواست‌های لایسنس‌دار را با **402** و بدنه‌ی
  `{ "error": { "code": "LICENSE_EXPIRED", … } }` رد می‌کند. مسیرهای
  `/health`, `/healthz`, `/ready`, `/readyz`, `/livez` به‌صورت پیش‌فرض مستثنا هستند.
- `[RequireKeygateFeature("pro")]` روی کنترلر یا اکشن: 402 وقتی لایسنس
  غیرقابل‌استفاده است، 403 وقتی لایسنس معتبر است ولی فیچر را ندارد. فقط برای
  کنترلرها؛ در minimal API داخل endpoint از `protection.HasFeature(...)`
  استفاده کنید.
- `HttpContext.Items["Keygate.License"]` شامل `LicenseSnapshot` تأییدشده است.

نکته‌ی استقرار: لایسنس سمت سرور به **هاست** گره می‌خورد، پس با چرخش
کانتینرها شناسه‌ی دستگاه عوض می‌شود. یا روی ماشین/VM پایدار اجرا کنید، یا یک
`KeygateOptions.DeviceId` پایدار از پلتفرم تزریق کنید (مثلاً شناسه‌ی
cluster/tenant)؛ SDK مقدار `kgid:v1:<platform>:<64-hex>` را می‌پذیرد.

## هویت دستگاه

شناسه به شکل `kgid:v1:<platform>:<sha256>` و دقیقاً مثل `pkg/deviceid` سرور
محاسبه می‌شود (با یک وکتور تست متقابل تأیید شده):

- هش هر مؤلفه: `sha256("keygate:device:v1:" + name + ":" + normalized)`
- هش ترکیبی: `sha256("keygate:device:v1:composite:" + platform + ":" + k=v;…)`

مؤلفه‌ها فقط واقعیت‌های سخت‌افزاری/سیستمی هستند: در macOS `IOPlatformUUID`،
UUID والیوم، مدل CPU، MAC کارت اصلی؛ در ویندوز `MachineGuid`، نام CPU، سریال
بورد/UUID محصول، سریال والیوم، MAC؛ در لینوکس `product_uuid`/`board_serial`،
`machine-id`، UUID فایل‌سیستم ریشه، مدل CPU، MAC. هاست‌نام به‌صورت پیش‌فرض
**خاموش** است (آنتروپی کم و پرتغییر). سریال خام هرگز از پروسه خارج نمی‌شود؛
فقط هش domain-separated آن‌ها.

اگر هیچ مؤلفه‌ای خوانده نشود (کانتینر قفل‌شده)، SDK به یک digest تصادفی
64-hex که محلی ذخیره می‌شود fallback می‌کند و `Device.IsHardwareDerived =
false` گزارش می‌دهد.

## بررسی یکپارچگی فایل‌ها

```csharp
options.IntegrityManifestPath     = "manifest.json";     // یا IntegrityManifestUri
options.IntegrityInstallDirectory = AppContext.BaseDirectory;
options.ReleasePublicKeys["rel_..."] = "…64 hex…";       // پین‌شده، از /api/v1/releases/:slug/signing-key

IntegrityCheckResult result = await protection.CheckIntegrityAsync();
```

مانیفست همان envelope سرور (`pkg/integrity`) است:
`{ format: "keygate-integrity-v1", payload: base64url(json), signature: base64url(ed25519 روی متن payload), signing_key_id, algorithm: "ed25519" }`.

قواعد اعمال‌شده:

- SHA-256 و اندازه برای هر فایل؛ مسیرهای نسبی با اسلش رو به جلو؛ بدون `..`،
  بدون مسیر مطلق، بدون symlink/reparse point، و مسیر نهایی باید داخل پوشه‌ی
  نصب بماند.
- فایل‌های قفل‌شده یا غیرقابل‌خواندن در `Errors` می‌روند و **هرگز** دستکاری
  حساب نمی‌شوند.
- امضای نامعتبر = `ManifestError` و به‌صورت `manifest_invalid` گزارش می‌شود.
- فقط فایل‌های اپلیکیشن در مانیفست باشند — هرگز log، فایل موقت، کش یا داده‌ی
  کاربر.

## گزارش رویداد امنیتی و سیاست‌های سرور

```csharp
await protection.ReportSecurityEventAsync(new SecurityEvent
{
    Type = SecurityEventTypes.IntegrityViolation,
    Severity = "high",
    Details = { ["source"] = "sdk", ["first_path"] = "app.dll" },
});
```

رشته‌ی canonical امضاشده (آینه‌ی `pkg/securityevent`، با وکتور تست):

```
keygate-security-event-v1
<event_id>
<license_id>
<device_id>
<event_type>
<occurred_at_unix>
```

- کلید لایسنس، توکن و جزئیات امضا نمی‌شوند و برای توجیه یک action لازم نیستند.
- `event_id` بین ۱ تا ۱۲۸ کاراکتر از `[A-Za-z0-9._-]`؛ تکرار همان id گزارش
  را idempotent می‌کند (سرور `duplicate: true` برمی‌گرداند).
- جزئیات: حداکثر ۱۶ ورودی، کلید `[a-z][a-z0-9_.-]{0,63}`، مقدار حداکثر ۵۱۲
  بایت؛ کلیدهای حاوی `token|license_key|private_key|secret|password|authorization|signature|cookie`
  سمت کلاینت حذف می‌شوند و سرور دوباره سانیتایز می‌کند.
- `occurred_at` حداکثر ۱۰ دقیقه در آینده و ۷ روز در گذشته.
- گزارش نیازمند لایسنس فعال و اعتبارسنجی‌شده است؛ در غیر این صورت SDK استثنای
  `KeygateSecurityEventException` می‌دهد و رویداد بی‌صاحب نمی‌فرستد.

واکنش‌های سمت سرور (جدول `security_event_policies`، actionها:
`log|flag|reject|block_activation|suspend|revoke`):

- گزارش‌های تأییدنشده (بدون کلید دستگاه ثبت‌شده یا امضای غلط) ذخیره می‌شوند
  ولی **هرگز** وضعیت لایسنس را تغییر نمی‌دهند.
- پیش‌فرض‌ها محافظه‌کارانه‌اند: `integrity_violation` فقط بعد از ۳ رویداد
  تأییدشده در ۲۴ ساعت → `suspend`؛ بقیه `flag`/`log`؛ هیچ revoke خودکاری نیست.
- `revoke` حداقل threshold ۲ می‌خواهد و نمی‌تواند product-wide (`*`) باشد.
  revoke دستی ادمین نیازمند `confirm_revoke=true` و یک رویداد تأییدشده است.
- هر تغییر وضعیت در audit log ثبت و webhook مربوطه ارسال می‌شود.

محدودیت نرخ: گارد per-license و limiter عمومی endpoint. پاسخ 429 به معنای
«بعداً تلاش کن» است، نه دستکاری؛ به‌خاطر آن کاربر را بلاک نکنید.

## محدودیت‌ها

- حفاظت سمت کلاینت **بازدارنده است، نه اثبات**. مهاجم با اجرای کد محلی
  می‌تواند کلاینت را patch کند، چک‌ها را hook کند، یا یک توکن معتبر را روی همان
  دستگاه replay کند. SDK هزینه‌ی سوءاستفاده را بالا می‌برد، ولی کپی‌برداری را
  غیرممکن نمی‌کند.
- شناسه‌ی دستگاه از سخت‌افزار مشتق می‌شود ولی attestation ندارد. کلون VM با
  UUID/MAC یکسان «همان دستگاه» دیده می‌شود. به سقف seat سمت سرور و بازبینی
  رویدادها تکیه کنید؛ وعده‌ی attestation سخت‌افزاری ندهید.
- حالت آفلاین به `exp` توکن (و `vun` + grace) محدود است و ساعت، ساعت دستگاه
  است: کاربر می‌تواند آن را عقب ببرد. اگر مهم است، اعتبارسنجی دوره‌ای آنلاین
  را فعال نگه دارید.
- مخزن راز محلی روی ویندوز DPAPI و روی macOS/Linux فایل `0600` است. این جلوی
  کاربران دیگر و کنجکاوی ساده را می‌گیرد، نه بدافزاری که با همان کاربر اجرا
  می‌شود. برای جداسازی قوی‌تر `IKeygateSecretStore` سفارشی (Keychain،
  Credential Manager، HSM) بدهید.
- بررسی یکپارچگی فایل‌ها را پوشش می‌دهد، نه حافظه را. اگر مهاجم هم مانیفست و
  هم کلید پین‌شده را عوض کند، محافظت بی‌اثر است — به همین دلیل کلیدها داخل
  باینری پین می‌شوند.
- در هیچ‌جای این SDK ادعای امنیت مطلق نشده است.

## چک‌لیست تولید

1. `LicensePublicKey` و `ReleasePublicKeys` را **پین** کنید؛ به واکشی اولیه از
   `/api/v1/license/pubkey` تکیه نکنید.
2. از `https` استفاده کنید (`AllowInsecureHttp` فقط برای توسعه‌ی محلی است —
   کلید لایسنس یک bearer credential است).
3. `ProductId` را تنظیم کنید تا توکن محصول دیگر قابل replay نباشد.
4. `AutoReportIntegrityFailures` را روشن نگه دارید ولی اولین خرابی یکپارچگی را
   «هشدار + گزارش» بگیرید؛ تشدید را به رویدادهای تأییدشده‌ی تکراری بسپارید
   (سیاست پیش‌فرض همین است).
5. هرگز منطق امنیتی را بر `NetworkError` بنا نکنید؛ یعنی «نامشخص».
6. چرخش کلید امضا را با ورودی‌های هم‌پوشان در `LicensePublicKeys` /
   `ReleasePublicKeys` انجام دهید و بعد کلید قدیمی را حذف کنید.
7. `ProblemCode` را log کنید، هرگز کلید لایسنس یا توکن را.
8. هدر `X-Powered-By: Keygate` و notice «Powered by Keygate» را نگه دارید —
   AGPL v3 بخش 7(b) این اتریبیوشن را الزامی کرده و قابل حذف نیست.

## تست‌ها

```bash
export PATH="$HOME/.dotnet:$PATH"
dotnet test sdk/dotnet/Keygate.Protection.Tests
```

پوشش: وکتورهای شناسه‌ی دستگاه و امضای رویداد از سرور Go، تأیید توکن لایسنس
(معتبر/منقضی/دستگاه اشتباه/محصول اشتباه/دست‌کاری‌شده)، تأیید مانیفست امضاشده با
فایل تغییریافته/حذف‌شده و path traversal، سانیتایزر جزئیات، مسیر
activation → validate → offline fallback → گزارش امضاشده با HTTP handler جعلی،
و نگاشت خطای فعال‌سازی.

سمت سرور:

```bash
TEST_DATABASE_URL=postgres://user@localhost:5432/keygate_test?sslmode=disable \
  go test -p 1 ./internal/handler -run TestSecurityEvent -count=1
```
