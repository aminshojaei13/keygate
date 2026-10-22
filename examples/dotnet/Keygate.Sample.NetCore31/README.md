# Keygate.Sample (نسخهٔ .NET Core 3.1)

همان چرخهٔ نمونهٔ اصلی (activate → verify → pubkey → offline) ولی با کد سازگار با
**C# 8** و **netcoreapp3.1**، برای پروژه‌های قدیمی‌تر.

```bash
cd examples/dotnet/Keygate.Sample.NetCore31
dotnet restore
dotnet run -- pubkey
dotnet run -- activate --license-key KG-TEST-TEST-TEST-TEST --identifier "kgid:v1:windows:5581b5a7b290e096fdea9b19916561e13d15fc983a9b57c53a9e5ad91f29f4b7"
dotnet run -- offline --identifier "kgid:v1:windows:5581b5a7b290e096fdea9b19916561e13d15fc983a9b57c53a9e5ad91f29f4b7"
```

> مقدار `kgid:v1:...` بالا یک شناسهٔ نمایشیِ ثابت است. در برنامهٔ واقعی آن را در startup
> از مشخصات پایدار سخت‌افزار/سیستم‌عامل بسازید و هرگز hardcode نکنید؛ سرور لایسنس را به
> همان شناسه‌ای می‌بندد که اولین activate می‌فرستد و بعد از تغییر سخت‌افزار/OS شناسهٔ
> متفاوت را در حالت strict رد می‌کند.

نکات:

* تنها وابستگی NuGet: `BouncyCastle.Cryptography` ‏2.4.0 (netstandard2.0) برای Ed25519.
* از `init`, `record`, `namespace X;`, `new()` و `System.Net.Http.Json` استفاده نمی‌شود؛
  `StringContent` + `JsonSerializer` جای آن‌ها را گرفته تا روی کامپایلر C# 8 هم بیلد شود.
* .NET Core 3.1 از دسامبر ۲۰۲۲ پشتیبانی رسمی ندارد. این نمونه فقط برای نگهداری پروژه‌های
  موجود است؛ برای پروژهٔ جدید از [`Keygate.Sample`](../Keygate.Sample) (net8.0) استفاده کنید.
* هیچ API Key مدیریتی در این کلاینت وجود ندارد و فقط `license_key` + کلید عمومی مصرف می‌شود.

بیلد و اجرا با Docker (قبلاً تأیید شده؛ پس از تغییرات strict دوباره اجرا کنید):

```bash
docker run --rm --platform linux/amd64 -v "$PWD":/src -w /src \
  mcr.microsoft.com/dotnet/sdk:3.1 dotnet build -c Release
```
