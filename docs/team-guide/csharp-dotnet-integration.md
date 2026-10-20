# یکپارچه‌سازی C#/.NET با Keygate

این سند کامل‌ترین راهنمای عملی برای تیم است: ساخت یک کلاینت .NET که لایسنس را فعال،
اعتبارسنجی و **آفلاین** بررسی می‌کند. کنار این سند، یک پروژهٔ نمونهٔ اجرایی در
`examples/dotnet/Keygate.Sample/` قرار دارد که با کد همین سند یکی است.

> **وضعیت بیلد (تأییدشده):** پروژه با `mcr.microsoft.com/dotnet/sdk:8.0` واقعاً بیلد شد:
> `Build succeeded. 0 Warning(s), 0 Error(s)`.
> علاوه بر آن، روی یک سرور Keygate واقعی (ایمیج `keygate-keygate:latest`، PostgreSQL 18)
> چرخهٔ `verify` اجرا شد و امضای Ed25519 با خط `signature : VERIFIED` تأیید شد؛ سپس
> `offline` با `--network none` (بدون هیچ دسترسی شبکه) توکن ذخیره‌شده را معتبر اعلام کرد.
> <br>یک خطای واقعی هم در همین مسیر پیدا و اصلاح شد: امضای `Ed25519.Verify` در
> BouncyCastle پارامتر طول کلید عمومی ندارد (`Verify(sig, sigOff, pk, pkOff, msg, msgOff, msgLen)`).

---

## ۱. پیش‌نیازها

* .NET SDK 8.0 یا بالاتر (`dotnet --version`).
* دسترسی به یک سرور Keygate در حال اجرا (پیش‌فرض `http://localhost:9000`).
* یک `license_key` آزمایشی (ساختگی). هرگز کلید واقعی را در مخزن نگذارید.
* فقط یک پکیج NuGet: **`BouncyCastle.Cryptography`** (برای Ed25519؛ صرفاً managed، بدون
  کتابخانهٔ native).

---

## ۲. ساختار پروژهٔ نمونه

```
examples/dotnet/Keygate.Sample/
├── Keygate.Sample.csproj   # net8.0 + BouncyCastle
├── appsettings.json        # فقط BaseUrl (بدون هیچ رازی)
├── Models.cs               # envelope پاسخ + مدل‌های نتیجه + KeygateApiException
├── Config.cs               # خواندن appsettings.json + override محیطی
├── Crypto.cs               # Base64Url، Hex، و پوشش Ed25519
├── OfflineToken.cs         # تقسیم توکن، بررسی امضا، بررسی انقضا
├── KeygateClient.cs        # فقط endpointهای عمومی /license/*
└── Program.cs              # CLI: pubkey | activate | verify | offline
```

---

## ۳. پوشش پاسخ (Envelope)

همهٔ پاسخ‌های زیر `/api/v1` یک قالب دارند:

```json
{ "success": true,  "data": { /* ... */ } }
{ "success": false, "error": { "code": "ACTIVATION_LIMIT", "message": "maximum activations reached", "details": { "max": 3, "current": 3 } } }
```

مدل‌های معادل در `Models.cs`:

```csharp
public sealed class ApiEnvelope<T>
{
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("data")] public T? Data { get; set; }
    [JsonPropertyName("error")] public ApiError? Error { get; set; }
}
```

قاعده: **همیشه** `success` را بررسی کنید، حتی وقتی status code ۲xx است؛ و هرگز فقط به
status code تکیه نکنید.

---

## ۴. کلاینت: سه فراخوانی کلیدی

```csharp
var client = new KeygateClient("http://localhost:9000");

// 1) کلید عمومی Ed25519 (بدون اعتبارنامه)
PublicKeyResult pk = await client.GetPublicKeyAsync();

// 2) فعال‌سازی دستگاه
ActivateResult act = await client.ActivateAsync(
    licenseKey: "KG-XXXX-XXXX-XXXX-XXXX",
    identifier: "my-machine",
    label: "Office PC");

// 3) اعتبارسنجی / تازه‌سازی
VerifyResult ver = await client.VerifyAsync("KG-XXXX-XXXX-XXXX-XXXX", "my-machine");
```

نکات:

* این سه مسیر **عمومی**‌اند؛ هیچ API Key نمی‌خواهند. `license_key` خودش اعتبارنامه است.
* `identifier` باید **پایدار و منحصربه‌فرد برای هر دستگاه** باشد. پیشنهاد: ترکیب
  `MachineGuid` (ویندوز) یا `IOPlatformUUID` (مک)، یا یک UUID که در اولین اجرا ساخته و
  در یک فایل محلی ذخیره می‌شود.
* `activate` روی همان دستگاه دوباره `status = "already_activated"` می‌دهد (خطا نیست).

---

## ۵. ذخیره و بررسی توکن آفلاین

پاسخ `activate`/`verify` فیلد `token` دارد. شکل آن:

```
base64url(payload_json) "." base64url(signature)
```

`OfflineToken.VerifyAndParse` این کارها را می‌کند:

1. توکن را روی **آخرین** نقطه به دو نیمه تقسیم می‌کند.
2. امضا (۶۴ بایت) و کلید عمومی (۳۲ بایت) را dekod می‌کند.
3. امضا را روی **بایت‌های ASCII نیمهٔ اول (رشتهٔ base64)** بررسی می‌کند — نه روی JSON خام.
4. JSON را parse می‌کند و `exp` را چک می‌کند.

```csharp
var publicKey = Hex.Decode(pk.PublicKey);        // 32 bytes
VerifyToken parsed = OfflineToken.VerifyAndParse(savedToken, publicKey);
```

برای بررسی «پایان خودِ لایسنس» (نه توکن):

```csharp
if (!OfflineToken.LicenseTermOk(parsed, out var reason))
{
    // لایسنس + مهلت (grc) تمام شده
}
```

معنای فیلدها: `exp` = انقضای توکن (چک‌این بعدی)، `vun` = پایان لایسنس (`0` = دائمی)،
`upd` = پایان دورهٔ نگهداری، `grc` = روز مهلت. جزئیات کامل در
`offline-licensing-guide.md`.

---

## ۶. امضای Ed25519 در .NET

.NET (تا .NET 8) کلاس عمومی Ed25519 ندارد، بنابراین از BouncyCastle استفاده می‌کنیم:

```csharp
using Org.BouncyCastle.Math.EC.Rfc8032;

public static bool Verify(byte[] publicKey, byte[] message, byte[] signature)
{
    if (publicKey.Length != Ed25519.PublicKeySize) return false; // 32
    if (signature.Length != Ed25519.SignatureSize) return false; // 64
    return Ed25519.Verify(signature, 0, publicKey, 0, publicKey.Length,
                          message, 0, message.Length);
}
```

دلیل انتخاب BouncyCastle: صرفاً managed است (بدون `libsodium`)، API آن پایدار است، و
الگوریتم **Ed25519 خالص** است که Keygate هم همین را استفاده می‌کند.

اگر نمی‌خواهید وابستگی اضافه کنید، گزینه‌های دیگر: `NSec.Cryptography` (نیازمند native
libsodium) یا پیاده‌سازی دستی (توصیه نمی‌شود).

---

## ۷. گردش کار کامل در اپلیکیشن

```
راه‌اندازی:
  token = خواندن از فایل امن (DPAPI / Keychain / کلید محافظت‌شده)
  if token != null:
     try:
        parsed = OfflineToken.VerifyAndParse(token, pinnedPublicKey)
        if OfflineToken.LicenseTermOk(parsed):
            → حالت مجاز (آفلاین)
     catch OfflineTokenException:
        token = null

  if token == null:
     try:
        ver = await client.VerifyAsync(key, identifier)
        ذخیرهٔ ver.token
        → حالت مجاز
     catch KeygateApiException as e:
        if e.Code == "LICENSE_NOT_FOUND":
            → تاریخچهٔ فعال‌سازی را نمایش بده (activate لازم است)
        else:
            → خطا نمایش بده
```

حلقهٔ نگه‌داری (مثلاً هر ۲۴ ساعت یا در هر بوت):

```
periodically:
  try:
     ver = await client.VerifyAsync(key, identifier)
     ذخیرهٔ ver.token
  catch NETWORK_ERROR:
     اگر با توکن فعلی هنوز در بازهٔ exp هستیم → ادامه بده
     وگرنه → درخواست اتصال/تمدید
```

---

## ۸. مدل خطاها

هر خطا به `KeygateApiException` با `Status` و `Code` تبدیل می‌شود. نگاشت مهم‌ترین کدها:

| کد | راهنمای کاربر |
|---|---|
| `LICENSE_NOT_FOUND` | کلید نامعتبر است یا روی این دستگاه فعال نشده |
| `ACTIVATION_LIMIT` | سقف دستگاه پر است؛ یک دستگاه را آزاد کن |
| `FEATURE_NOT_AVAILABLE` | این محصول فعال‌سازی دستگاهی ندارد |
| `LOCKED_OUT` | تلاش‌های ناموفق زیاد؛ `details.retry_after` صبر کن |
| `UPDATES_EXPIRED` | دورهٔ نگهداری تمام شده؛ تمدید لازم است |
| `RELEASE_YANKED` | آن نسخه پس گرفته شده |
| `NETWORK_ERROR` | سرور در دسترس نیست (کد محلی، از خود کلاینت) |

قالب‌بندی امن پیام خطا: هرگز بدنهٔ خام خطای سرور را به کاربر نشان ندهید؛ از `Friendly()`
یا پیام خودتان استفاده کنید.

---

## ۹. اشتباهات رایج (باید پرهیز کرد)

* ❌ گذاشتن `kg_live_…` (API Key مدیریتی) در اپ دسکتاپ.
* ❌ بررسی امضا روی JSON خام به‌جای رشتهٔ base64.
* ❌ استفاده از `exp` به‌عنوان پایان لایسنس (آن `vun` است).
* ❌ ذخیرهٔ توکن در plaintext بدون محافظت سیستمی.
* ❌ فرض‌کردن «معتبر» صرفاً به این دلیل که شبکه جواب نداد.
* ❌ حذف/تغییر انتساب «Powered by Keygate».

---

## ۱۰. پیوستن به HTTPS و سایر endpointهای عمومی

* برای دانلود نسخه: `POST /api/v1/license/download` (پارامترها `platform`, `version`,
  `channel`). پاسخ presigned URL + `sha256` دارد؛ hash را بعد از دانلود بررسی کنید.
* برای سهمیه (SaaS/hybrid): `POST /api/v1/license/usage` و `/usage/status`.
* برای بررسی یک قابلیت: `POST /api/v1/license/entitlements`.
* برای پلن‌های شناور: `/license/floating/checkout|checkin|heartbeat`.
* برای کشف کلید عمومی امضا (بدون احراز هویت):
  `GET /api/v1/releases/{product_slug}/signing-key` → `keys[]` با `kid`, `algorithm`,
  `public_key_pem`, `public_key_b64`, `tauri_pubkey`, `status`. همان کلید برای بررسی امضای
  artifact و مانیفست `keygate-integrity-v1` به‌کار می‌رود؛ در بیلد pin کنید و در زمان اجرا
  کلید `active` را انتخاب کنید (کلیدهای `retired` فقط برای نسخه‌های قدیمی‌اند).

### ۱۰.۱ سناریوی «توکن آماده» (Enterprise / نصب بدون اینترنت)

اگر مشتری نمی‌تواند خودش activate کند (محیط بسته)، ادمین از پنل
`POST /admin/licenses/:id/offline-token` را با `identifier` دستگاه صدا می‌زند و توکن
امضاشده را به مشتری می‌دهد. کلاینت همان توکن را با `OfflineToken.Verify` و کلید عمومی
`GET /api/v1/license/pubkey` بررسی می‌کند — هیچ تماس شبکه‌ای لازم نیست.

> توجه: این کار روی سرور **یک فعال‌سازی واقعی** می‌سازد و سهم دستگاه مصرف می‌شود.

نمونهٔ `HttpClient` با base URL استاتیک و timeout ۳۰ ثانیه در `KeygateClient.cs` است.

---

## ۱۱. آزمون دستی

```bash
cd examples/dotnet/Keygate.Sample

dotnet run -- pubkey
# انتظار: algorithm=ed25519, format=hex, public_key=<64 hex>

dotnet run -- activate --license-key KG-TEST-TEST-TEST-TEST --identifier demo-1
# انتظار: status=activated یا already_activated + signature VERIFIED

dotnet run -- offline
# انتظار: Offline verification: OK (بدون تماس شبکه)
```

کلید `KG-TEST-…` ساختگی است و فقط اگر روی سرور آزمایشی شما وجود داشته باشد کار می‌کند؛
در غیر این صورت `404 LICENSE_NOT_FOUND` می‌گیرید که خودش یک مسیر خطای درست است.

---

## ۱۲. دستورهای بیلد و تأیید

```bash
cd examples/dotnet/Keygate.Sample
dotnet restore
dotnet build -c Release
dotnet run -- pubkey
```

اگر `dotnet restore` به‌خاطر نبود شبکه شکست خورد، پکیج `BouncyCastle.Cryptography` را
به‌صورت لوکال درآورید (`dotnet add package` روی ماشین متصل) یا از یک فید NuGet داخلی
استفاده کنید.

**وضعیت واقعی این محیط:** بیلد با Docker انجام و تأیید شد. برای بازتولید:

```bash
docker run --rm -v "$PWD:/app" -w /app \
  mcr.microsoft.com/dotnet/sdk:8.0 dotnet build -c Release
```

سپس برای اجرای end-to-end باید `KEYGATE_BASE_URL` را به سرور در دسترسِ کانتینر اشاره دهد
(مثلاً `http://host.docker.internal:9000` یا نام سرویس در یک شبکهٔ Docker مشترک).

---

## ۱۳. پروژه‌های قدیمی: .NET Core 3.1 (C# 8)

اگر پروژهٔ موجود شما روی `netcoreapp3.1` است، نسخهٔ آمادهٔ همین نمونه در
[`examples/dotnet/Keygate.Sample.NetCore31`](../../examples/dotnet/Keygate.Sample.NetCore31)
هست. همان چهار دستور (`pubkey`, `activate`, `verify`, `offline`) را دارد و با
`dotnet build -c Release` روی SDK 3.1 با `0 Warning(s), 0 Error(s)` بیلد می‌شود.

تفاوت‌هایی که برای سازگاری لازم بود:

| مورد | در نسخهٔ net8 | در نسخهٔ netcoreapp3.1 |
|---|---|---|
| namespace | `namespace Keygate.Sample;` | `namespace Keygate.Sample { … }` |
| مدل‌ها | `{ get; init; }` | `{ get; set; }` |
| ساخت شیء | `new()` | `new JsonSerializerOptions()` |
| الگو | `is { Count: > 0 }` | `Features != null && Features.Count > 0` |
| JSON body | `JsonContent.Create(...)` (پکیج `System.Net.Http.Json`) | `StringContent(JsonSerializer.Serialize(...), Encoding.UTF8, "application/json")` |
| usingها | `ImplicitUsings` | صریح در هر فایل |
| خواندن پاسخ | `ReadAsStringAsync(ct)` | `ReadAsStringAsync()` |

نکات مهم برای انتقال به پروژهٔ خودتان:

* **Ed25519 در BCL نیست.** حتی در net8 هم `System.Security.Cryptography` امضای Ed25519
  ندارد؛ به همین دلیل هر دو نمونه از `BouncyCastle.Cryptography` استفاده می‌کنند
  (سازگار با `netstandard2.0`، پس روی 3.1 هم کار می‌کند). جایگزین بدون وابستگی وجود ندارد
  مگر اینکه خودتان الگوریتم را پیاده کنید.
* الگوریتم امضا تغییر نکرده: امضا روی **رشتهٔ base64url نیمهٔ اول توکن** زده می‌شود، نه JSON خام.
* توکن‌های جدید فیلد `kid` دارند (`key_<16hex>`). اگر کلید را pin می‌کنید و فقط یک کلید
  دارید، همان کلید را با کلید عمومی `GET /api/v1/license/pubkey` بررسی کنید؛ برای چرخش کلید
  باید کلید متناظر با `kid` را انتخاب کنید.
* `System.Text.Json` در 3.1 داخل shared framework هست؛ نیازی به پکیج نیست.
* **هشدار:** .NET Core 3.1 از دسامبر ۲۰۲۲ EOL است و وصلهٔ امنیتی نمی‌گیرد. اگر امکان دارد
  پروژه را به net8.0 ارتقا دهید؛ این نمونه فقط برای دورهٔ گذار است.
