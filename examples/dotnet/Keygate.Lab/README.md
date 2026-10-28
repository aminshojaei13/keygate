# Keygate.Lab (.NET)

یک آزمایشگاه کوچک و تکرارپذیر برای تست رفتار لایسنس Keygate، بدون نیاز به سرور Go،
PostgreSQL یا اینترنت. هدفش پاسخ به این سؤال‌های عملی است:

1. **strict device binding** (حالت پیش‌فرض سرور) چه اجازه‌ای می‌دهد و کجا `DEVICE_CHANGED` می‌دهد؟
2. **حالت tolerant** چطور با سقف صندلی و جابه‌جایی دستگاه کار می‌کند؟
3. اگر **فایل‌های برنامه** عوض شوند، کلاینت متوجه می‌شود؟
4. اگر **دستگاه عوض شود** یا توکن روی ماشین دیگری کپی شود، چه اتفاقی می‌افتد؟

همهٔ این‌ها با یک mock درون‌فرآیندی اجرا می‌شوند که همان قرارداد عمومی
`/api/v1/license/*`، همان قالب توکن `internal/license/token.go` و همان معنای
strict/tolerant در `internal/service/license.go` را پیاده می‌کند.

> **strict پیش‌فرض است.** سرور با `STRICT_DEVICE_BINDING=true` (پیش‌فرض) هر لایسنس را
> به **یک** دستگاه گره می‌زند، مهاجرت `previous_identifier` را رد می‌کند و
> deactivate از سمت کلاینت را می‌بندد. mock هم به‌صورت پیش‌فرض همین حالت را دارد.

## چرا سبک است؟

- بدون وابستگی به Go، PostgreSQL، Docker یا شبکه؛ mock روی یک پورت loopback موقت بالا می‌آید.
- تنها وابستگی NuGet: **BouncyCastle.Cryptography** (Ed25519 خالص managed).
- هر اجرا مستقل است: وضعیت روی دیسک ذخیره می‌شود، پس می‌توانید بی‌نهایت بار اجرا کنید.
- هیچ فایل واقعی سیستم را دست نمی‌زند؛ همه‌چیز داخل `--root` (پیش‌فرض `./.keygate-lab`) است.

## پیش‌نیاز

- .NET SDK 8.0 یا جدیدتر

## شروع سریع

```bash
cd examples/dotnet/Keygate.Lab
dotnet build
dotnet run -- demo
```

`demo` یک سناریوی کامل را اجرا و **۳۶ بررسی** را assert می‌کند؛ خروجی باید
`checks 36 / passed 36 / failed 0` باشد. اگر خطایی رخ دهد، همان خط با کد خطای مورد انتظار
چاپ می‌شود و برنامه با کد خروج ۱ تمام می‌شود.

> `demo` فقط با mock کار می‌کند (چون صندلی‌ها را تغییر می‌دهد). برای سرور واقعی از
> دستورهای تک‌تک با `--server` استفاده کنید.

## شناسهٔ دستگاه (`identifier`)

در این شاخه `identifier` باید از سخت‌افزار مشتق شود. آزمایشگاه هم دقیقاً همان الگوریتم
`pkg/deviceid` را پیاده می‌کند:

- پنج مؤلفهٔ `board`، `os`، `disk`، `cpu` و `nic` از `machineId` شبیه‌سازی می‌شوند.
- هر مؤلفه با دامنهٔ `keygate:device:v1:<component>:` هش می‌شود.
- digest نهایی = `SHA256("keygate:device:v1:composite:<platform>:" + "board=...;cpu=...;...")`
- خروجی: `kgid:v1:<platform>:<64hex>`

پس شناسهٔ تولیدشده روی سرور واقعی هم قابل استفاده است (و با تغییر `machineId` عوض می‌شود).
`--scheme uuid` برای نشان دادن حالتی است که سرور strict آن را با
`IDENTIFIER_SCHEME_UNSUPPORTED` رد می‌کند: یک UUID ذخیره‌شده روی دیسک، تعویض سخت‌افزار را
نمی‌فهمد.

## سناریوها به‌صورت دستی

### ۱) strict: یک لایسنس، یک دستگاه

```bash
dotnet run -- install --name a
dotnet run -- activate --name a            # OK
dotnet run -- deactivate --name a          # DEACTIVATION_DISABLED (عمداً)
dotnet run -- machine-change --name a
dotnet run -- start  --name a --offline    # DEVICE_MISMATCH
dotnet run -- activate --name a            # DEVICE_CHANGED
dotnet run -- verify   --name a            # LICENSE_NOT_FOUND
dotnet run -- server reset-binding         # شبیه‌سازی اقدام ادمین
dotnet run -- activate --name a            # OK
```

`server reset-binding` فقط روی mock است و همان اثر
`POST /api/v1/admin/licenses/:id/reset-device-binding` را دارد. روی سرور واقعی این کار از
پنل مدیریت انجام می‌شود و کلاینت راهی برای آزاد کردن binding ندارد — همین نکته هدف strict است.

### ۲) strict: شناسهٔ غیرسخت‌افزاری

```bash
dotnet run -- install --name c --scheme uuid
dotnet run -- activate --name c            # IDENTIFIER_SCHEME_UNSUPPORTED
```

### ۳) tolerant: سقف صندلی و جابه‌جایی

```bash
dotnet run -- server reset --device-binding tolerant --max-activations 1
dotnet run -- install --name a
dotnet run -- install --name b

dotnet run -- activate --name a            # صندلی را می‌گیرد
dotnet run -- activate --name b            # ACTIVATION_LIMIT
dotnet run -- deactivate --name a          # آزاد می‌شود
dotnet run -- activate --name b            # OK
```

مهاجرت شناسه در حالت tolerant:

```bash
dotnet run -- machine-change --name b
dotnet run -- activate --name b --previous-identifier <identifier-قدیمی>
# status=activated و همان صندلی درجا منتقل می‌شود
```

### ۴) تغییر فایل‌های برنامه

```bash
dotnet run -- tamper --name a --target app
dotnet run -- start  --name a --offline     # APP_FILES_CHANGED
dotnet run -- repair --name a --target app
dotnet run -- start  --name a --offline     # OK
```

`manifest.json` هش SHA-256 همهٔ فایل‌های `app/` را نگه می‌دارد؛ فایل تغییرکرده،
حذف‌شده و اضافه‌شده هر سه گزارش می‌شوند.

### ۵) دست‌کاری توکن

```bash
dotnet run -- tamper --name a --target token
dotnet run -- start  --name a --offline     # TOKEN_SIGNATURE_INVALID
dotnet run -- activate --name a             # توکن تازه
```

### ۶) انقضای لایسنس و grace

```bash
dotnet run -- server reset --valid-in-days -1 --grace-days 0
dotnet run -- activate --name a
dotnet run -- start --name a --offline      # LICENSE_TERM_ENDED

dotnet run -- server reset --valid-in-days -1 --grace-days 3
dotnet run -- activate --name a
dotnet run -- start --name a --offline      # OK (داخل بازهٔ grace)
```

`--valid-in-days 0` یعنی «همین حالا تمام می‌شود»؛ برای حالت «قبلاً تمام شده» عدد منفی بدهید.

## سرور واقعی

```bash
dotnet run -- activate --name a \
  --server http://localhost:9000 \
  --license-key KG-XXXX-XXXX-XXXX-XXXX
```

- با `--server` هیچ mock‌ای بالا نمی‌آید و درخواست‌ها به همان آدرس می‌روند.
- `demo`، `server info`، `server reset` و `server reset-binding` در حالت سرور واقعی کار نمی‌کنند
  (`DEMO_REQUIRES_MOCK` / `MOCK_ONLY`) تا اشتباهی وضعیت سرور واقعی تغییر نکند.
- شناسهٔ `kgid` تولیدشده با الگوریتم همین مخزن ساخته می‌شود، پس سرور strict آن را می‌پذیرد.
- اگر در حالت آنلاین سرور در دسترس نباشد، `start` به توکن ذخیره‌شده برمی‌گردد و
  یک هشدار چاپ می‌کند؛ تصمیم نهایی را همان توکن امضاشده و `exp` آن می‌گیرد.
- برای تست سناریوی تغییر دستگاه روی سرور واقعی به دسترسی ادمین نیاز دارید
  (reset device binding در پنل).

## دستورها

```text
install        --name a [--label "Laptop A"] [--scheme kgid|sha256|uuid] [--platform darwin] [--force]
activate       --name a [--previous-identifier ID] [--license-key KEY]
verify         --name a [--license-key KEY]
deactivate     --name a [--identifier ID]
start          --name a [--offline]
status         [--name a]
machine-change --name a [--deactivate-old]
tamper         --name a --target app|token|identity
repair         --name a [--target app|identity|all] [--token]
server         info|reset|reset-binding|url
reset          --name a | --mock | --all
demo           (فقط mock)
```

گزینه‌های مشترک: `--server URL`، `--license-key KEY`، `--root DIR`.
متغیرهای محیطی: `KEYGATE_LAB_SERVER`، `KEYGATE_LAB_LICENSE_KEY`، `KEYGATE_LAB_ROOT`.

کنترل mock در `server reset`:

| گزینه | معنا |
|---|---|
| `--device-binding MODE` | `strict` (پیش‌فرض) یا `tolerant` |
| `--max-activations N` | سقف صندلی در حالت tolerant (پیش‌فرض ۱) |
| `--token-ttl-days N` | عمر توکن آفلاین (پیش‌فرض ۷) |
| `--valid-in-days N` | مدت لایسنس؛ ۰ = همین حالا، منفی = گذشته |
| `--grace-days N` | دورهٔ grace بعد از پایان لایسنس (پیش‌فرض ۷) |
| `--status STATE` | `active`، `trialing`، `past_due`، `canceled`، `suspended`، `revoked`، `expired` |
| `--rotate-key` | کلید امضای تازه؛ توکن‌های جدید `kid` تازه می‌گیرند |

> `server reset` فقط صندلی‌ها را پاک می‌کند. توکن‌های آفلاین ذخیره‌شده تا `exp` خودشان
> معتبر می‌مانند؛ برای باطل‌کردن فوری، کلید را rotate کنید و کلاینت را وادار به verify کنید.

## ساختار داده

```text
.keygate-lab/
  .keygate-lab-root          نشانگر مالکیت؛ بدون آن reset --all اجرا نمی‌شود
  mock/server.json           وضعیت mock: کلید امضا، binding mode، صندلی‌ها، پلن، featureها
  installs/
    a/
      device.json            machineId (شبیه‌سازی سخت‌افزار)
      install.json           installId + scheme/platform + identifier
      license.json           کلید، توکن امضاشده، کلید عمومی pin‌شده
      manifest.json          هش SHA-256 فایل‌های app/
      app/                   «برنامهٔ نصب‌شده» که قابل دست‌کاری است
    b/ ...
  demo/                      دادهٔ demo (چون demo از صندلی‌ها استفاده می‌کند)
```

## کدهای خطای پایدار

| کد | معنا |
|---|---|
| `DEVICE_CHANGED` | strict: لایسنس به دستگاه دیگری بسته است (یا `previous_identifier` در strict) |
| `DEACTIVATION_DISABLED` | strict: کلاینت اجازهٔ آزادکردن دستگاه را ندارد |
| `IDENTIFIER_SCHEME_UNSUPPORTED` | strict: شناسهٔ سخت‌افزاری نیست (`kgid`/`sha256` لازم است) |
| `ACTIVATION_LIMIT` | tolerant: سقف صندلی پر است |
| `LICENSE_NOT_FOUND` | کلید/فعال‌سازی روی سرور پیدا نشد (مثلاً شناسهٔ جدید بعد از تعویض دستگاه) |
| `APP_FILES_CHANGED` | فایل‌های برنامه با manifest نمی‌خوانند |
| `NO_TOKEN` | توکن ذخیره‌شده‌ای نیست |
| `TOKEN_SIGNATURE_INVALID` | امضای توکن نامعتبر است |
| `TOKEN_KEY_UNKNOWN` | توکن با کلید دیگری امضا شده؛ کلید عمومی pin‌شده را دوباره بگیرید |
| `TOKEN_EXPIRED` | `exp` توکن گذشته است |
| `LICENSE_TERM_ENDED` | مدت لایسنس + grace تمام شده است |
| `DEVICE_MISMATCH` | توکن برای این `identifier` صادر نشده است |
| `MISSING_LICENSE_KEY` | کلید لایسنس داده نشده |
| `MOCK_ONLY` / `DEMO_REQUIRES_MOCK` | عملیات فقط با mock مجاز است |
| `REFUSING_TO_DELETE` | مسیر حذف‌شدنی امن نیست |

## محدودیت‌ها

- mock فقط یک محصول/پلن/لایسنس دارد؛ برای multi-product از سرور واقعی استفاده کنید.
- mock فقط endpointهای عمومی لایسنس را پیاده می‌کند (`pubkey`, `activate`, `verify`, `deactivate`)
  به‌علاوهٔ یک مسیر آزمایشی برای شبیه‌سازی `reset-device-binding`؛ بخش ادمین، پورتال،
  release و webhook را ندارد.
- امضای توکن با seed ذخیره‌شده در `mock/server.json` انجام می‌شود؛ این کلید صرفاً برای آزمایش است و
  نباید هیچ‌جا به‌عنوان کلید تولیدی استفاده شود.
- این پروژه یک **آزمایشگاه** است، نه نمونهٔ تولیدی؛ برای الگوی تولیدی به
  [`../Keygate.Sample`](../Keygate.Sample/README.md) و
  [`docs/team-guide/offline-licensing-guide.md`](../../../docs/team-guide/offline-licensing-guide.md) نگاه کنید.

## پاک‌سازی

```bash
dotnet run -- reset --name a      # یک نصب
dotnet run -- reset --mock        # وضعیت mock
dotnet run -- reset --all         # کل پوشهٔ ریشه
```
