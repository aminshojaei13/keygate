# راهنمای انتشار نسخه و صحت فایل (Integrity & Release)

این سند چرخهٔ انتشار نسخه، امضای دیجیتال artifactها و مدل «صحت فایل» را در Keygate توضیح می‌دهد.
منابع: `internal/service/release.go`, `release_signing.go`, `release_feed.go`, `tauri_minisign.go`.

---

## ۱. مفهوم‌ها

| مفهوم | معنا |
|---|---|
| **Release** | یک نسخهٔ منطقی از محصول (`(product_id, version)` یکتا). |
| **Artifact** | فایل باینری یک نسخه برای یک `platform` مشخص. |
| **Channel** | `stable` / `beta` / `alpha` / `dev`. |
| **Integrity (صحت فایل)** | تضمین اینکه فایل دانلودی همان چیزی است که سازنده منتشر کرده. |
| **Signing key** | کلید Ed25519 هر محصول که artifactها را امضا می‌کند. |

نسخهٔ semver معتبر است (`1.2.3` یا `1.2.3-beta.1`)، و پلتفرم‌های مجاز در `allowedPlatforms`
تعریف شده‌اند (مثل `darwin-arm64`, `darwin-x64`, `windows-x64`, `windows-arm64`, `linux-x64`).

---

## ۲. چرخهٔ عمر انتشار

```
draft ──(publish)──▶ published ──(yank)──▶ yanked
  ▲                                            │
  └────────────(unyank)◀───────────────────────┘
draft ──(delete)──▶ حذف (فقط draft)
```

1. **ایجاد draft** — `POST /admin/releases` (نیازمند scope `releases:write`).
2. **افزودن artifact** — `POST /admin/releases/:id/artifacts` → دریافت `upload_url` (presigned PUT).
3. **آپلود** — کلاینت فایل را مستقیماً به object storage می‌فرستد.
4. **finalize** — `POST …/artifacts/:aid/finalize` → سرور فایل را می‌خواند و **خودش** `sha256` را
   محاسبه و تثبیت می‌کند.
5. **publish** — `POST …/actions/publish` → بررسی hash + **امضا** با کلید فعال محصول.
6. **yank/unyank** — خارج/داخل‌کردن از دسترس بدون حذف.

---

## ۳. صحت فایل: کی و چطور محاسبه می‌شود

### ۳.۱ `sha256` توسط سرور

در `finalize`:

* سرور **بایت‌های واقعی فایل** را از storage می‌خواند و `sha256` می‌سازد. این مقدار «مُعتمد» است.
* اگر کلاینت در بدنه `expected_sha256` (یا alias قدیمی `sha256`) فرستاده باشد، سرور آن را با مقدار
  محاسبه‌شده مقایسه می‌کند؛ عدم تطابق → **`409 SHA256_MISMATCH`**.
* سقف خواندن در finalize: **۸ GiB** (`maxFinalizeReadSize = 8 << 30`). فایل‌های بزرگ‌تر باید
  به artifactهای چندبخشی تقسیم شوند.
* فایلی که در storage کامل نشده باشد (`IsUploaded()==false`) قابل استفاده نیست.

### ۳.۲ امضای Ed25519 هر artifact

در `publish`:

* سرور فایل را دوباره می‌خواند، hash را تأیید می‌کند و artifact را با کلید خصوصی Ed25519 محصول
  امضا می‌کند → نتیجه در ستون `ed25519_sig` ذخیره می‌شود.
* امضا در زمان publish **pin** می‌شود و اگر فایل بعداً تغییر کند (race) جلوی انتشار گرفته می‌شود.
* اگر محصول `require_signing=true` باشد و کلید فعالی نباشد، publish رد می‌شود.
* برای Tauri، علاوه بر آن، امضای **minisign** ساخته و در `tauri_signature` ذخیره می‌شود.

---

## ۴. مدیریت کلید امضا

| عملیات | مسیر | نکته |
|---|---|---|
| ساخت کلید | `POST /admin/products/:id/signing-key` | پاسخ ۲۰۱ |
| چرخش | `POST /admin/products/:id/signing-key/rotate` | کلید قبلی غیرفعال می‌شود |
| لیست | `GET /admin/products/:id/signing-keys` | `{"keys":[...]}` |
| کلید عمومی (PEM) | `GET /admin/products/:id/signing-key/public.pem` | خام PEM |
| کلید عمومی (Tauri) | `GET /admin/products/:id/signing-key/tauri-pubkey` | `{"pubkey":"..."}` |
| غیرفعال‌سازی | `DELETE /admin/products/:id/signing-key` | `{"status":"deactivated"}` |
| **کشف عمومی** | `GET /api/v1/releases/:product_slug/signing-key` | بدون احراز هویت؛ فقط کلید عمومی، شامل کلیدهای `retired` |

مدل امنیتی:

* کلید خصوصی هر محصول با **AES-256-GCM** رمزنگاری می‌شود؛ کلید master از
  `RELEASE_KEY_ENCRYPTION_KEY` می‌آید. **AAD = `product_id`** است، پس یک ردیف لو‌رفته را
  نمی‌توان به محصول دیگری منتقل کرد.
* کلید خصوصی **هرگز** در هیچ پاسخ API یا لاگ ظاهر نمی‌شود.
* در هر لحظه فقط **یک کلید فعال** به‌ازای هر محصول وجود دارد (index یکتای جزئی در دیتابیس).
* مسیرهای کلید امضا فقط با scope **`admin`** قابل دسترسی‌اند (نه `releases:write`).

خطاهای رایج: `503 SIGNING_DISABLED` (نبود `RELEASE_KEY_ENCRYPTION_KEY`)،
`409 KEY_ALREADY_ACTIVE` (کلید فعال موجود است — باید rotate کنید).

---

## ۵. مدل «صحت» در Keygate

دو لایهٔ جدا وجود دارد و نباید با هم اشتباه شوند:

### ۵.۱ لایهٔ سرور (همان چیزی که در انتشار اجرا می‌شود)

* هر artifact دو چیز دارد: `sha256` (صحت بایت‌ها) و `ed25519_sig` (اثبات اصالت).
* **فید** (`feed.xml`/`feed.json`/`upgrade.json`) این دو مقدار را برای هر نسخه حمل می‌کند.
* کلاینت امنیت را از روی امضای همراه همان artifact می‌گیرد، نه از پنهان‌بودن URL.
* سرور **هیچ endpointای** برای ساخت/امضا/توزیع مانیفست فایل‌ها ندارد.

### ۵.۲ لایهٔ مستقل: مانیفست `keygate-integrity-v1`

در همین شاخه یک پیاده‌سازی کامل و مستقل وجود دارد که «همهٔ فایل‌های نصب‌شده» را پوشش می‌دهد:

* پکیج: `pkg/integrity` (`manifest.go`, `scanner.go`, `signer.go`, `verifier.go`)
* ابزار خط فرمان: `cmd/keygate-manifest`
* تست: `pkg/integrity/integrity_test.go` (۴۲۴ خط)

این ابزار **به سرور وصل نیست** و برای تیم انتشار/CI است، نه برای فراخوانی از API.

#### ساختار مانیفست

```json
{
  "manifest_version": 1,
  "product_id": "prd_...",
  "release_id": "rel_...",
  "version": "1.2.0",
  "channel": "stable",
  "platform": "windows",
  "arch": "x64",
  "created_at": "2026-10-20T10:00:00Z",
  "signing_key_id": "key_...",
  "metadata": {"build": "ci-1234"},
  "files": [{"path": "app.exe", "sha256": "<64 hex>", "size": 123456}]
}
```

#### قالب امضاشده (envelope)

| فیلد | معنا |
|---|---|
| `format` | همیشه `keygate-integrity-v1` |
| `manifest_version` | `1` |
| `payload` | `base64url` بدون padding از JSON **کانونیکال** مانیفست |
| `signature` | `base64url` امضای Ed25519 |
| `signing_key_id` | شناسهٔ کلید |
| `algorithm` | همیشه `ed25519` |
| `manifest` | نسخهٔ خوانا برای بازرسی انسان (اختیاری) |

**نکتهٔ مهم:** امضا روی **رشتهٔ `payload`** زده می‌شود، نه روی JSON خام — دقیقاً همان
قراردادی که توکن آفلاین دارد.

#### کانونیکال‌سازی (باید بایت‌به‌بایت تکرارشدنی باشد)

* فایل‌ها بر اساس `path` مرتب می‌شوند.
* JSON بدون فاصلهٔ اضافی و با UTF-8 معتبر.
* مسیرها POSIX و نسبی‌اند؛ `..`، `/` ابتدایی، بک‌اسلش و کاراکتر کنترلی رد می‌شوند.

#### اسکن پوشه

* پیش‌فرض‌ها حذف می‌شوند: `.*` (فایل‌های مخفی)، `*.sig`، `*.tmp`، `manifest*.json`،
  `*.integrity.json`، `.DS_Store`، `Thumbs.db`.
* خروجی مرتب و با `sha256` + `size` هر فایل.

#### ترتیب بررسی در سمت کلاینت (`VerifyEnvelope`)

1. `format` درست باشد → وگرنه `ErrUnknownFormat`
2. `manifest_version == 1` → وگرنه `ErrUnsupportedVersion`
3. `algorithm == ed25519` → وگرنه `ErrUnsupportedAlgorithm`
4. `signing_key_id` در مجموعهٔ کلیدهای مورد اعتماد **وجود داشته باشد** → وگرنه `ErrUnknownSigningKey`
5. امضا با Ed25519 بررسی شود → وگرنه `ErrInvalidSignature`
6. `payload` دیکد و `signing_key_id` داخل payload با envelope مقایسه شود
7. مسیر همهٔ فایل‌ها اعتبارسنجی شود (ضد path traversal)

سپس `VerifyInstalledFiles` هر فایل را بررسی می‌کند: فایل غایب → `MissingFiles`، هش متفاوت →
`ModifiedFiles` (مقایسه با `subtle.ConstantTimeCompare`)، و بیرون‌رفتن از ریشهٔ نصب مسدود می‌شود.

#### استفاده از CLI

```bash
# ساخت و امضا
go run ./cmd/keygate-manifest create \
  --dir ./dist --product prd_demo --version 1.2.0 \
  --key-id key_demo --key ./signing-seed.hex \
  --platform windows --arch x64 \
  --out integrity.manifest.json

# بررسی امضا + فایل‌های نصب‌شده
go run ./cmd/keygate-manifest verify \
  --manifest integrity.manifest.json \
  --pubkey ./signing-public.hex \
  --dir "C:\Program Files\MyApp"

# فقط نمایش خلاصه
go run ./cmd/keygate-manifest inspect --manifest integrity.manifest.json
```

`--key` می‌تواند مسیر فایل یا مقدار hex/base64 باشد (seed ۳۲ بایتی یا کلید ۶۴ بایتی).

برای ساخت باینری مستقل:

```bash
go build -o bin/keygate-manifest ./cmd/keygate-manifest
./bin/keygate-manifest inspect --manifest integrity.manifest.json
```

> این ابزار در `Dockerfile` ساخته نمی‌شود (ایمیج فقط `cmd/server` را build می‌کند). آن را در
> مرحلهٔ CI انتشار جداگانه بسازید و کنار artifact بگذارید.

#### اتصال به سرور

* مانیفست **جایگزین** `ed25519_sig` سرور نیست؛ مکمل آن است.
* کلید عمومی مورد استفاده باید همان کلید امضای محصول باشد تا کلاینت بتواند آن را از
  `GET /api/v1/releases/:product_slug/signing-key` کشف کند (بخش ۴ همین سند).
* اینکه خروجی `keygate-manifest` کجا ذخیره/توزیع شود، هنوز تصمیم تیم است و در
  `documentation-audit.md` بخش ۹ ثبت شده است.

---

## ۶. فیدها و محل قرارگیری صحت

### ۶.۱ Sparkle — `feed.xml`

* قالب RSS appcast. هر item:
  * `sparkle:version`, `sparkle:shortVersionString`
  * `enclosure` با `url`, `length`, `type`, و **`sparkle:edSignature`** = base64 خام امضای
    ۶۴ بایتی Ed25519 روی فایل.
  * `sparkle:criticalUpdate` (اگر نسخه پایین‌تر از کف پشتیبانی باشد).
* Sparkle امضا را با کلید عمومی جاسازی‌شده در اپ بررسی می‌کند.

### ۶.۲ Velopack — `feed.json`

* JSON با لیست assetها (`{Assets:[...]}`).
* Velopack امضا را بررسی نمی‌کند؛ امنیت بر **`SHA-256` (hex)** و **HTTPS** است.
* مسیر `…/velopack/*path` برای دریافت بسته/ایندکس (ممکن است ۳۰۲ ریدایرکت شود).

### ۶.۳ Tauri — `upgrade.json`

* JSON با `version`, `pub_date`, `url`, و **`signature`** به‌قالب **minisign**.
* minisign یک envelope است: `Ed` + key id + 64-byte signature + trusted comment + global signature.
  Keygate این را در `tauri_signature` می‌سازد (`internal/service/tauri_minisign.go`).

### ۶.۴ نگه‌داری `updates_until`

* برای لایسنس‌های دائمی، `updates_until` پایان دورهٔ نگهداری است.
* `GET /license/download` نسخه‌ای که **بعد از** `updates_until` منتشر شده را رد می‌کند:
  **`403 UPDATES_EXPIRED`**.
* فید هم وقتی لایسنس می‌فرستید همان برش را اعمال می‌کند (cutoff).

---

## ۷. فید و لایسنس (gating)

* همهٔ کانال‌ها به‌طور پیش‌فرض **عمومی**اند.
* اگر روی محصول `feed_license_required=true` باشد، فید بدون لایسنس
  **`401 LICENSE_KEY_REQUIRED`** می‌دهد.
* ارسال کلید:
  * هدر `X-License-Key: <license_key>`، یا
  * `license_token` (کوئری) یا هدر `X-License-Token` = همان توکن امضاشده از `/license/verify`.
* **کلید لایسنس در URL پذیرفته نمی‌شود** (کوئری `license_key` → `400` با پیام راهنما)؛ چون
  در لاگ CDN/proxy ثبت می‌شود.
* پاسخ‌های فید `Vary: X-License-Key, X-License-Token` دارند و برای درخواست‌های شخصی
  `Cache-Control: private, no-store`.
* پارامترها: `platform` (الزامی)، `channel` (پیش‌فرض `stable`)، `limit` (۱–۱۰۰، پیش‌فرض ۲۰).

---

## ۸. مسیرهای قدیمی فید

`GET /releases/feed.xml`, `/releases/feed.json`, `/releases/upgrade.json`, `/releases/feed`
همه **`410 FEED_PATH_REMOVED`** می‌دهند و به مسیر جدید راهنمایی می‌کنند. هیچ کلاینت زنده‌ای
باقی نمانده است (cutover پیش از عرضه).

---

## ۹. تأیید صحت در سمت کلاینت

### ۹.۱ برای Sparkle/Tauri

* کلید عمومی محصول را یک‌بار دریافت و در اپ pin کنید:
  `GET /api/v1/releases/:product_slug/signing-key` (بدون احراز هویت، `public_key_pem` /
  `public_key_b64` / `tauri_pubkey`)، یا از مسیرهای ادمین
  `GET /admin/products/:id/signing-key/public.pem` و `…/tauri-pubkey`.
* امضای همراه فید را با همان کلید بررسی کنید؛ اگر معتبر نبود، فایل را نصب نکنید.

### ۹.۲ برای دانلود دستی

پس از `POST /license/download`:

1. فایل را از `url` دانلود کنید.
2. `sha256` فایل را محاسبه و با فیلد `sha256` پاسخ مقایسه کنید.
3. (اختیاری) امضای artifact را با کلید عمومی محصول بررسی کنید.

---

## ۱۰. خطاهای مسیر دانلود و فید

| کد | وضعیت | معنا |
|---|---|---|
| `INVALID_PLATFORM` | ۴۰۰ | پلتفرم نامعتبر |
| `INVALID_CHANNEL` | ۴۰۰ | کانال نامعتبر |
| `LICENSE_NOT_FOUND` | ۴۰۴ | لایسنس نامعتبر/غیرقابل‌استفاده |
| `RELEASE_NOT_FOUND` | ۴۰۴ | نسخه‌ای مطابق درخواست نیست |
| `PLATFORM_NOT_AVAILABLE` | ۴۰۴ | نسخه برای این پلتفرم artifact ندارد |
| `RELEASE_YANKED` | ۴۱۰ | نسخه yank شده |
| `UPDATES_EXPIRED` | ۴۰۳ | نسخه بعد از پایان دورهٔ نگهداری منتشر شده |
| `STORAGE_DISABLED` | ۵۰۳ | storage پیکربندی نشده |
| `SHA256_MISMATCH` | ۴۰۹ | عدم تطابق hash در finalize |
| `LICENSE_KEY_REQUIRED` | ۴۰۱ | فید نیازمند لایسنس |
| `FEED_PATH_REMOVED` | ۴۱۰ | مسیر قدیمی فید |
| `SIGNING_DISABLED` | ۵۰۳ | کلید امضا پیکربندی نشده |
| `KEY_ALREADY_ACTIVE` | ۴۰۹ | کلید فعال موجود است |

---

## ۱۱. چک‌لیست انتشار یک نسخه

1. مطمئن شوید محصول `require_signing` مناسب دارد و کلید فعال دارد.
2. draft بسازید (`POST /admin/releases`).
3. برای هر پلتفرم artifact اضافه و آپلود کنید.
4. هر artifact را finalize کنید (سرور hash را می‌سازد).
5. publish کنید (امضاها ساخته می‌شوند).
6. فید را با `platform` و `channel` درست تست کنید.
7. اگر دورهٔ نگهداری دارید، مطمئن شوید `feed_license_required` و `updates_until` درست تنظیم
   شده‌اند.
8. در صورت نیاز، نسخه را yank کنید (نه حذف) تا مشتریان قدیمی نسخه‌های قدیمی را از دست ندهند.
