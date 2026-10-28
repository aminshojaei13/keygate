# گزارش رویداد امنیتی و سیاست‌های واکنش (فارسی)

این سند خلاصه‌ی فارسی قابلیت گزارش تخلف سمت کلاینت و واکنش سمت سرور است.
مرجع دقیق قرارداد: `pkg/securityevent/securityevent.go` و
`internal/service/security.go`.

## چرخه‌ی کار

1. کلاینت هنگام فعال‌سازی یک جفت‌کلید Ed25519 می‌سازد و فقط **کلید عمومی** را
   می‌فرستد (`device_public_key`, `device_key_alg`).
2. هنگام تخلف، کلاینت یک رویداد را با کلید خصوصی دستگاه امضا می‌کند.
3. سرور امضا را با کلید ثبت‌شده‌ی همان فعال‌سازی بررسی می‌کند.
4. فقط رویداد **تأییدشده** می‌تواند سیاست را فعال کند.
5. سیاست (policy) محصول تعیین می‌کند چه اقدامی انجام شود؛ همه‌چیز در audit log
   ثبت و webhook ارسال می‌شود.

## قرارداد امضا

```
keygate-security-event-v1
<event_id>
<license_id>
<device_id>
<event_type>
<occurred_at_unix>
```

امضا روی بایت‌های UTF-8 همین رشته است. کلید لایسنس، توکن و فیلدهای `details`
عمداً امضا نمی‌شوند: دو مورد اول راز هستند و `details` فقط تلمتری است که
به‌تنهایی نباید باعث action شود.

## endpoint عمومی

`POST /api/v1/license/security-events` (با محدودیت نرخ per-IP و گارد per-license)

```json
{
  "license_key": "KG-...",
  "identifier": "kgid:v1:linux:<64-hex>",
  "event_id": "0f3a...",
  "event_type": "integrity_violation",
  "severity": "high",
  "occurred_at": 1730000000,
  "details": { "source": "sdk", "modified": "1" },
  "signature": "<base64url ed25519>"
}
```

پاسخ: `event_id`, `accepted`, `duplicate`, `verified`, `action_taken`,
`license_status`, `under_review`, `is_allowed`, `retry_after`.

قواعد:

- `event_id` یکتا در محدوده‌ی همان لایسنس است؛ ارسال دوباره = `duplicate: true`
  و بدون اثر جدید (ضدتکرار و ضدreplay).
- `occurred_at` حداکثر ۱۰ دقیقه در آینده و ۷ روز در گذشته.
- `details` سانیتایز می‌شود: کلیدهای حاوی token/secret/password/... حذف،
  کنترل‌کاراکترها پاک، طول‌ها محدود.
- بدون کلید ثبت‌شده یا با امضای غلط: رویداد ذخیره می‌شود با `verified=false` و
  **هیچ تغییری در وضعیت لایسنس نمی‌دهد**.

## مسیرهای ادمین

| متد و مسیر | کار |
| --- | --- |
| `GET /api/v1/admin/products/:id/security-policy` | خواندن سیاست محصول |
| `PUT /api/v1/admin/products/:id/security-policy` | جایگزینی سیاست |
| `GET /api/v1/admin/security-events` | فهرست/فیلتر رویدادها |
| `POST /api/v1/admin/security-events/:id/action` | اعمال دستی action |
| `POST /api/v1/admin/security-events/:id/dismiss` | رد کردن رویداد |
| `POST /api/v1/admin/licenses/:id/security-reset` | پاک‌کردن نشانه‌های برگشت‌پذیر |

## actionها و پیش‌فرض‌ها

`log` | `flag` | `reject` | `block_activation` | `suspend` | `revoke`

- پیش‌فرض‌ها محافظه‌کارانه‌اند: `integrity_violation` → `suspend` فقط بعد از
  ۳ رویداد تأییدشده در ۲۴ ساعت؛ بقیه‌ی انواع → `flag` یا `log`.
- هیچ revoke خودکاری در پیش‌فرض‌ها وجود ندارد.
- `revoke` نیازمند threshold ≥ ۲ و غیر product-wide است؛ revoke دستی نیازمند
  `confirm_revoke=true` و یک رویداد تأییدشده.
- `reject` عملیات جاری را رد می‌کند ولی لایسنس را تغییر نمی‌دهد.

## محدودیت‌ها

- تأیید امضا فقط ثابت می‌کند «همین دستگاه این رویداد را فرستاده»، نه اینکه
  ادعای داخل رویداد درست است. به همین دلیل یک گزارش تنها باعث revoke نمی‌شود.
- کلاینت دست‌کاری‌شده می‌تواند گزارش نفرستد. گزارش‌دهی بازدارنده است، نه اثبات.
- اگر کاربر آفلاین باشد، رویداد دیر می‌رسد؛ پنجره‌ی زمانی ۷ روزه را در نظر
  بگیرید و روی «تعداد رویداد در بازه» تکیه کنید نه یک رویداد منفرد.
