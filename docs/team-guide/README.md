# راهنمای تیم Keygate

این پوشه، مستندات فارسی تیم برای پروژه **Keygate** است. همه‌ی مطالب این پوشه بر اساس **کد فعلی همین مخزن** (شاخه‌ی جاری) نوشته شده و نه بر اساس مستندات نسخه‌ی عمومی یا READMEهای قدیمی. هرجا کد و مستندات با هم اختلاف داشته‌اند، رفتار کد مبنا قرار گرفته و اختلاف در فایل [documentation-audit.md](documentation-audit.md) ثبت شده است.

---

## ۱. Keygate چیست؟

Keygate یک سامانه‌ی **مدیریت لایسنس نرم‌افزار** است که به‌صورت self-hosted اجرا می‌شود. کار اصلی آن این است که یک نرم‌افزار (مثلاً یک برنامه‌ی دسکتاپ ویندوزی با C#/.NET) بتواند از سرور بپرسد:

* «آیا این لایسنس معتبر است؟»
* «آیا این دستگاه اجازه‌ی اجرا دارد؟»
* «کاربر خریدار چه قابلیت‌هایی دارد؟»
* «آخرین نسخه‌ی مجاز برای این لایسنس کدام است؟»

و اگر شبکه در دسترس نبود، کلاینت بتواند برای مدتی محدود **به‌صورت آفلاین** اعتبار لایسنس را با **امضای دیجیتال (Digital Signature)** بررسی کند.

## ۲. چه مشکلی را حل می‌کند؟

| مشکل | راه‌حل Keygate |
| --- | --- |
| توزیع کلید لایسنس و پیگیری نصب‌ها | صدور کلید یکتا، ثبت فعال‌سازی هر دستگاه |
| اشتراک‌گذاری یک کلید روی چند دستگاه | محدودیت تعداد دستگاه‌ها (`max_activations`) و شناسه‌ی دستگاه |
| «لپ‌تاپم را گم کردم» | غیرفعال‌سازی دستگاه از پورتال مشتری یا پنل مدیریت و آزادسازی سهمیه |
| قطعی اینترنت کاربر | توکن آفلاین امضاشده با Ed25519 که کلاینت خودش بررسی می‌کند |
| فروش قابلیت‌ها به‌صورت لایه‌ای | طرح‌ها (`Plan`) + قابلیت‌های مجاز (`Entitlements`) + افزودنی‌ها (`Addons`) |
| سهمیه‌ی مصرف (متریک) | `usage_events` + `usage_counters` با سهمیه‌ی دوره‌ای (ساعتی/روزانه/ماهانه/سالانه) |
| توزیع بیلد و آپدیت امن | انتشار (`Release`) چندسکویی، `sha256` محاسبه‌شده توسط سرور، امضای Ed25519 هر فایل |
| صورتحساب | اشتراک Stripe، جایگزین‌کردن پلن، لغو، فاکتورها، و صورت‌حساب مصرف |

## ۳. معماری کلی (خلاصه‌ی یک‌پاراگرافی)

یک باینری Go (Gin) که هم API را سرو می‌کند و هم فایل‌های ساخته‌شده‌ی فرانت‌اند React را از `web/dist`؛ داده‌ها در PostgreSQL؛ فایل‌های انتشار روی هر ذخیره‌ساز سازگار با S3 (Cloudflare R2 / AWS S3 / MinIO)؛ پرداخت با Stripe؛ ایمیل با SMTP یا Cloudflare. جزئیات و نمودار در [architecture.md](architecture.md).

## ۴. قابلیت‌های اصلی (وضعیت واقعی)

| قابلیت | وضعیت | مرجع |
| --- | --- | --- |
| CRUD محصول/طرح/قابلیت/افزودنی | پیاده‌سازی‌شده | `internal/handler/admin.go` |
| فعال‌سازی لایسنس روی دستگاه | پیاده‌سازی‌شده | `internal/service/license.go` |
| اعتبارسنجی آنلاین | پیاده‌سازی‌شده | همان |
| محدودیت تعداد دستگاه | پیاده‌سازی‌شده (تراکنشی) | `internal/store` (`ActivateWithinLimit`) |
| ثبت/غیرفعال‌سازی/فعال‌سازی مجدد دستگاه | پیاده‌سازی‌شده | `internal/handler/admin.go`, `portal_activations.go` |
| انتقال لایسنس به ایمیل دیگر | پیاده‌سازی‌شده | `AdminHandler.TransferLicense` |
| سیاست نصب مجدد ویندوز/تغییر سخت‌افزار | **پیاده‌سازی‌نشده در سرور** (کتابخانه‌ی drift فقط تست دارد) | `pkg/deviceid` |
| توکن آفلاین امضاشده (Ed25519) | پیاده‌سازی‌شده | `internal/license/token.go` |
| صدور توکن آفلاین توسط ادمین | پیاده‌سازی‌شده | `AdminHandler.IssueOfflineToken` |
| مدیریت کلیدهای امضای release | پیاده‌سازی‌شده | `internal/handler/release_signing_admin.go` |
| انتشار نسخه و فید آپدیت (Sparkle/Velopack/Tauri) | پیاده‌سازی‌شده | `internal/handler/release_public.go` |
| بررسی صحت فایل (sha256) | پیاده‌سازی‌شده | `ReleaseService.FinalizeArtifact` |
| Integrity Manifest | **ابزار خط فرمان جدا**؛ به سرور وصل نیست | `cmd/keygate-manifest`, `pkg/integrity` |
| API عمومی لایسنس | پیاده‌سازی‌شده | `/api/v1/license/*` |
| API مدیریتی | پیاده‌سازی‌شده | `/api/v1/admin/*` |
| API پورتال مشتری | پیاده‌سازی‌شده | `/api/v1/portal/*` |
| گزارش حسابرسی (Audit Log) | پیاده‌سازی‌شده | `audit_logs` + `/admin/audit-logs` |
| محدودسازی نرخ درخواست | پیاده‌سازی‌شده (حافظه‌ای، اختیاری Redis) | `internal/middleware/ratelimit.go` |
| Idempotency | پیاده‌سازی‌شده برای سه مسیر نوشتنی | `internal/middleware/idempotency.go` |
| SDK پایتون | پیاده‌سازی‌شده اما دارای چند باگ قراردادی | `sdk/python` |
| SDK دات‌نت رسمی | **وجود ندارد**؛ نمونه‌ی آموزشی در `examples/dotnet` | `examples/dotnet/Keygate.Sample` |
| پنل مدیریت | پیاده‌سازی‌شده (۱۴ صفحه) | `web/src/pages/admin` |

## ۵. برای شروع کار

* می‌خواهید پروژه را بالا بیاورید؟ → [getting-started.md](getting-started.md)
* می‌خواهید معماری را بفهمید؟ → [architecture.md](architecture.md)
* می‌خواهید API را صدا بزنید؟ → [api-reference.md](api-reference.md)
* می‌خواهید کلاینت C#/.NET بنویسید؟ → [csharp-dotnet-integration.md](csharp-dotnet-integration.md)
* می‌خواهید Keygate را به یک نرم‌افزار جدید وصل کنید؟ → [python-and-typescript-examples.md](python-and-typescript-examples.md)
* می‌خواهید بفهمید کدام مستند قابل‌اعتماد نیست؟ → [documentation-audit.md](documentation-audit.md)

## ۶. جدول راهنمای مستندات

| فایل | برای چه کسی | چه چیزی در آن است |
| --- | --- | --- |
| [README.md](README.md) | همه | این صفحه؛ نقشه‌ی راه مستندات |
| [getting-started.md](getting-started.md) | توسعه‌دهنده‌ی جدید | پیش‌نیازها، اجرای محلی، Docker، تست، مهاجرت‌ها |
| [architecture.md](architecture.md) | معمار / توسعه‌دهنده‌ی ارشد | اجزا، جریان‌ها، نمودار Mermaid |
| [admin-panel-guide.md](admin-panel-guide.md) | پشتیبانی / ادمین | تمام صفحه‌های پنل و عملیات واقعی هرکدام |
| [api-reference.md](api-reference.md) | هر دو طرف یکپارچه‌سازی | تمام endpointهای واقعی با بدنه و پاسخ |
| [authentication-and-security.md](authentication-and-security.md) | تیم امنیت | احراز هویت، scopeها، کلیدها، rate limit، تهدیدها |
| [offline-licensing-guide.md](offline-licensing-guide.md) | توسعه‌دهنده‌ی کلاینت | ساختار توکن آفلاین، امضا، انقضا، محدودیت‌ها |
| [integrity-and-release-guide.md](integrity-and-release-guide.md) | تیم انتشار | release، manifest، امضا، verify |
| [csharp-dotnet-integration.md](csharp-dotnet-integration.md) | توسعه‌دهنده‌ی C#/.NET | راهنمای کامل یکپارچه‌سازی + کد نمونه |
| [python-and-typescript-examples.md](python-and-typescript-examples.md) | توسعه‌دهنده‌ی غیر Go | نمونه‌های Python و TypeScript |
| [deployment-troubleshooting-and-team-workflows.md](deployment-troubleshooting-and-team-workflows.md) | DevOps / پشتیبانی | عیب‌یابی گام‌به‌گام |
| [glossary-fa.md](glossary-fa.md) | همه | واژه‌نامه‌ی اصطلاحات |
| [documentation-audit.md](documentation-audit.md) | مدیر فنی | تناقض‌ها، قابلیت‌های ناقص، ریسک‌ها، تصمیم‌های لازم |

## ۷. نکته‌ی حقوقی (AGPL v3)

این پروژه تحت AGPL v3 با شرط اضافه‌ی بند ۷(b) منتشر شده است. اعلان **«Powered by Keygate»** یک **الزام حقوقی** است و حذف آن نقض لایسنس محسوب می‌شود. بنابراین موارد زیر نباید حذف یا پنهان شوند:

* سرآیند پاسخ `X-Powered-By: Keygate` (منبع: `internal/branding/branding.go`, `cmd/server/main.go`)
* فیلدهای `attribution_text` و `attribution_url` در پاسخ `GET /api/v1/config`
* متن اعلان در پنل مدیریت، پورتال مشتری، صفحه‌ی ورود و پاصفحه‌ی ایمیل‌ها

برای مجوز تجاری به `hello@keygate.app` مراجعه کنید (فایل‌های `LICENSE` و `NOTICE`).
