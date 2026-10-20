# نمونه‌های Python و TypeScript

این سند همان چرخهٔ C# را در دو زبان دیگر نشان می‌دهد: فعال‌سازی، اعتبارسنجی، و بررسی
**آفلاین** توکن Ed25519. همهٔ داده‌ها ساختگی‌اند.

قاعدهٔ مشترک هر زبان (از `internal/license/token.go`):

```
token   = base64url(payload_json) + "." + base64url(signature_64_bytes)
امضا    = Ed25519_sign(private_key, ASCII_bytes(base64url(payload_json)))
```

یعنی **آنچه امضا می‌شود، رشتهٔ base64 نیمهٔ اول است، نه JSON خام.**

---

## ۱. Python

### ۱.۱ نصب

```bash
pip install requests cryptography
```

### ۱.۲ کلاینت کامل (فقط endpointهای عمومی)

```python
import base64
import json
import time

import requests
from cryptography.exceptions import InvalidSignature
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PublicKey

BASE = "http://localhost:9000"
LICENSE_KEY = "KG-AAAA-BBBB-CCCC-DDDD"   # ساختگی
IDENTIFIER = "my-machine"


def b64url_decode(s: str) -> bytes:
    s += "=" * (-len(s) % 4)
    return base64.urlsafe_b64decode(s)


def call(path: str, payload: dict | None = None):
    """فراخوانی یک endpoint عمومی و بازکردن envelope."""
    url = f"{BASE}/api/v1/{path}"
    r = requests.post(url, json=payload, timeout=30) if payload is not None \
        else requests.get(url, timeout=30)
    body = r.json()
    if not body.get("success"):
        err = body.get("error", {})
        raise RuntimeError(f"[{r.status_code} {err.get('code')}] {err.get('message')}")
    return body["data"]


def public_key() -> bytes:
    data = call("license/pubkey")
    assert data["algorithm"] == "ed25519"
    return bytes.fromhex(data["public_key"])


def verify_offline(token: str, pub: bytes) -> dict:
    """بررسی امضا + انقضا، سپس برگرداندن payload."""
    payload_b64, sig_b64 = token.rsplit(".", 1)
    signature = b64url_decode(sig_b64)
    message = payload_b64.encode("ascii")          # ← امضا روی رشتهٔ base64
    Ed25519PublicKey.from_public_bytes(pub).verify(signature, message)

    payload = json.loads(b64url_decode(payload_b64))
    now = int(time.time())
    if payload.get("exp", 0) and now > payload["exp"]:
        raise RuntimeError("token expired; run an online verify")
    return payload


def activate() -> str:
    data = call("license/activate", {
        "license_key": LICENSE_KEY,
        "identifier": IDENTIFIER,
        "label": "Office PC",
    })
    print("status:", data["status"])
    return data["token"]


def verify_online() -> str:
    data = call("license/verify", {
        "license_key": LICENSE_KEY,
        "identifier": IDENTIFIER,
    })
    print("plan:", data["plan_name"], "| valid_until:", data.get("valid_until"))
    return data["token"]


if __name__ == "__main__":
    pub = public_key()
    try:
        token = verify_online()
    except RuntimeError:
        token = activate()          # اگر فعال نشده بود، فعال کن

    with open("keygate-token.txt", "w") as f:
        f.write(token)

    # شبیه‌سازی حالت آفلاین
    parsed = verify_offline(open("keygate-token.txt").read(), pub)
    print("offline OK:", parsed["lid"], parsed["sts"])
```

### ۱.۳ SDK رسمی پایتون (`sdk/python`)

اگر نمی‌خواهید منطق امضا/دستگاه/مانیفست را خودتان بنویسید، در همین مخزن یک SDK مرجع
**بدون هیچ وابستگی خارجی** وجود دارد (فقط کتابخانهٔ استاندارد پایتون: `urllib`, `hashlib`,
`json`, `hmac`, `dataclasses`).

```bash
# نصب از مسیر مخزن
pip install ./sdk/python
# یا فقط کپی پوشهٔ keygate در پروژه
```

```python
from keygate import KeygateClient, ActivationLimitError, LicenseNotFoundError

client = KeygateClient(base_url="https://licensing.example.com")

# کلیدهای امضا را با kid واقعی‌شان در keyring ثبت کن (پیش از بررسی آفلاین)
keys = client.get_license_public_keys()   # GET /license/pubkey → keys[]
print([(k.kid, k.status) for k in keys])

try:
    res = client.activate("KG-AAAA-BBBB-CCCC-DDDD", label="Office PC")
    print(res.status, res.license_id, res.features)
except ActivationLimitError as e:
    print("سهم دستگاه پر است:", e.current, "/", e.max)
except LicenseNotFoundError:
    print("کلید لایسنس نامعتبر است")

# اعتبارسنجی آنلاین
v = client.verify_online("KG-AAAA-BBBB-CCCC-DDDD")
print(v.status, v.valid_until)

# بررسی آفلاین توکن (بدون شبکه)
claims = client.verify_offline_token(res.token, expected_product_id=res.product_id)
print(claims.expires_at, claims.features)

# بررسی مانیفست صحت (خروجی keygate-manifest)
manifest = client.verify_integrity_manifest(envelope_json, install_dir="./dist")
```

| متد | کار |
|---|---|
| `activate(key, identifier=?, label=?)` | `POST /license/activate` + برگرداندن `ActivationResult` |
| `verify_online(key)` | `POST /license/verify` |
| `deactivate(key, identifier=?)` | `POST /license/deactivate` |
| `get_entitlements(key)` | `POST /license/entitlements` |
| `record_usage(key, feature, count=1, idempotency_key=?)` | `POST /license/usage` |
| `get_license_public_keys()` | `GET /license/pubkey` |
| `get_release_signing_keys(product_slug)` | `GET /releases/{slug}/signing-key` |
| `verify_offline_token(token, ...)` | بررسی محلی Ed25519 با keyring |
| `verify_integrity_manifest(envelope, dir)` | بررسی مانیفست + فایل‌های نصب‌شده |

نکات مهم:

* **شناسهٔ دستگاه:** اگر `identifier` ندهید، `DeviceIdentifier.get_or_create_device_id()`
  یک `kgid:v1:<platform>:<64hex>` از مشخصات سخت‌افزاری می‌سازد و در نبود آن، UUID محلی را
  در `~/Library/Application Support/Keygate` (macOS)، `%APPDATA%` (ویندوز) یا
  `~/.local/share/keygate` (لینوکس) ذخیره می‌کند.
* **keyring و چرخش کلید:** توکن‌های این نسخه فیلد `kid` دارند (`key_<16hex>`). اگر فقط
  `license_public_key_hex` را به سازنده بدهید، کلید با نام `bootstrap` ثبت می‌شود و
  `verify_offline_token` با خطای `UNKNOWN_SIGNING_KEY` شکست می‌خورد. راه درست:
  `client.get_license_public_keys()` را صدا بزنید تا هر کلید با `kid` واقعی خودش ثبت شود.
  توکن‌های قدیمی بدون `kid` با کلید پیش‌فرض بررسی می‌شوند.
* **ساعت:** `verify_offline_token` پارامتر `clock_skew` (پیش‌فرض ۶۰ ثانیه)،
  `required_features` و `now` دارد؛ برای دستگاه‌هایی که ساعتشان جلو/عقب است تنظیمش کنید.
* **تست‌ها:** `python3 -m unittest discover -s sdk/python/tests` (فایل `test_sdk.py`).
  اسکریپت‌های مستقل هم در `test/` هستند (`offline_token_test.py`, `integrity_test.py`,
  `device_identity_test.py`).

> ⚠️ **ناسازگاری شناخته‌شده:** الگوریتم ساخت `kgid` در `sdk/python/keygate/device.py` با
> `pkg/deviceid` در Go یکسان نیست (پایتون مقادیر خام را هش می‌کند، Go ابتدا هر مؤلفه را با
> domain separation هش و سپس digest مرکب می‌سازد). چون سرور فعلاً فقط **قالب** را اعتبارسنجی
> می‌کند اثر عملی ندارد، ولی اگر روزی drift scoring سمت سرور فعال شود باید یکی شوند.
> این مورد در `documentation-audit.md` ثبت شده است.

### ۱.۴ نکات Python

* `requests` روی پاسخ ۴xx استثنا پرتاب نمی‌کند؛ باید خودتان envelope را باز کنید
  (همان‌طور که در `call` هست).
* برای `Idempotency-Key` روی `usage`:

```python
requests.post(f"{BASE}/api/v1/license/usage",
              json={"license_key": LICENSE_KEY, "feature": "api_calls", "quantity": 1},
              headers={"Idempotency-Key": "req-2026-10-19-0001"}, timeout=30)
```

* کتابخانهٔ `cryptography` فقط Ed25519 دارد (کافی است)؛ برای minisign (فید Tauri) به
  کتابخانهٔ جداگانه نیاز دارید.

---

## ۲. TypeScript / Node

### ۲.۱ نصب

```bash
npm install @noble/ed25519
# اگر روی Node < 20 هستید، یک polyfill برای sha512 لازم است:
# npm install @noble/hashes
```

### ۲.۲ کلاینت

```ts
import { sha512 } from '@noble/hashes/sha512';
import * as ed from '@noble/ed25519';

// @noble/ed25519 v2 نیاز به sha512 سفارشی دارد (برای محیط‌های بدون WebCrypto)
ed.etc.sha512Sync = (...m) => sha512(ed.etc.concatBytes(...m));

const BASE = process.env.KEYGATE_BASE_URL ?? 'http://localhost:9000';
const LICENSE_KEY = 'KG-AAAA-BBBB-CCCC-DDDD'; // ساختگی
const IDENTIFIER = 'my-machine';

function b64urlToBytes(s: string): Uint8Array {
  s = s.replace(/-/g, '+').replace(/_/g, '/');
  s += '='.repeat((4 - (s.length % 4)) % 4);
  const bin = atob(s);
  return Uint8Array.from(bin, (c) => c.charCodeAt(0));
}

async function call<T>(path: string, body?: unknown): Promise<T> {
  const res = await fetch(`${BASE}/api/v1/${path}`, {
    method: body === undefined ? 'GET' : 'POST',
    headers: body === undefined ? {} : { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const env = await res.json();
  if (!env.success) {
    const e = env.error ?? {};
    throw new Error(`[${res.status} ${e.code}] ${e.message}`);
  }
  return env.data as T;
}

async function publicKey(): Promise<Uint8Array> {
  const data = await call<{ algorithm: string; public_key: string }>('license/pubkey');
  if (data.algorithm !== 'ed25519') throw new Error('unexpected algorithm');
  return Uint8Array.from(data.public_key.match(/.{2}/g)!.map((h) => parseInt(h, 16)));
}

async function verifyOffline(token: string, pub: Uint8Array): Promise<any> {
  const i = token.lastIndexOf('.');
  const payloadB64 = token.slice(0, i);
  const sig = b64urlToBytes(token.slice(i + 1));
  const msg = new TextEncoder().encode(payloadB64);  // ← امضا روی رشتهٔ base64

  const ok = await ed.verifyAsync(sig, msg, pub);
  if (!ok) throw new Error('signature verification failed');

  const payload = JSON.parse(new TextDecoder().decode(b64urlToBytes(payloadB64)));
  const now = Math.floor(Date.now() / 1000);
  if (payload.exp && now > payload.exp) throw new Error('token expired');
  return payload;
}

async function verifyOnline(): Promise<string> {
  const data = await call<{ token: string; plan_name: string }>('license/verify', {
    license_key: LICENSE_KEY,
    identifier: IDENTIFIER,
  });
  console.log('plan:', data.plan_name);
  return data.token;
}

(async () => {
  const pub = await publicKey();
  const token = await verifyOnline();
  const parsed = await verifyOffline(token, pub);
  console.log('offline OK:', parsed.lid, parsed.sts);
})();
```

### ۲.۳ نکات TypeScript

* در Node 20+ و مرورگرهای جدید، `WebCrypto` موجود است؛ `@noble/ed25519` خودش از آن
  استفاده می‌کند و نیازی به `sha512Sync` نیست.
* در مرورگر **هرگز** API Key مدیریتی نگذارید. فقط `/license/*` عمومی.
* `atob` فقط ASCII را درست handle می‌کند؛ برای payload حتماً `b64urlToBytes` بزنید، نه
  `atob` مستقیم روی UTF-8.

---

## ۳. دفترچهٔ curl

```bash
# کلید عمومی
curl -s http://localhost:9000/api/v1/license/pubkey | jq

# فعال‌سازی
curl -s -X POST http://localhost:9000/api/v1/license/activate \
  -H 'Content-Type: application/json' \
  -d '{"license_key":"KG-AAAA-BBBB-CCCC-DDDD","identifier":"my-machine","label":"Office PC"}' | jq

# اعتبارسنجی
curl -s -X POST http://localhost:9000/api/v1/license/verify \
  -H 'Content-Type: application/json' \
  -d '{"license_key":"KG-AAAA-BBBB-CCCC-DDDD","identifier":"my-machine"}' | jq

# ثبت مصرف با Idempotency-Key
curl -s -X POST http://localhost:9000/api/v1/license/usage \
  -H 'Content-Type: application/json' \
  -H 'Idempotency-Key: demo-0001' \
  -d '{"license_key":"KG-AAAA-BBBB-CCCC-DDDD","feature":"api_calls","quantity":1}' | jq

# دانلود نسخه (URL امضاشده)
curl -s -X POST http://localhost:9000/api/v1/license/download \
  -H 'Content-Type: application/json' \
  -d '{"license_key":"KG-AAAA-BBBB-CCCC-DDDD","platform":"windows-x64"}' | jq

# لغو فعال‌سازی
curl -s -X POST http://localhost:9000/api/v1/license/deactivate \
  -H 'Content-Type: application/json' \
  -d '{"license_key":"KG-AAAA-BBBB-CCCC-DDDD","identifier":"my-machine"}' | jq
```

---

## ۴. جدول تطبیق سه زبان

| کار | C# | Python | TypeScript |
|---|---|---|---|
| بررسی امضا | `Ed25519.Verify(...)` (BouncyCastle) | `Ed25519PublicKey.verify` | `ed.verifyAsync` |
| base64url | `Base64Url.Decode` | `base64.urlsafe_b64decode` | دستی (`atob` + جایگزینی) |
| JSON | `System.Text.Json` | `json` | `JSON.parse` |
| HTTP | `HttpClient` | `requests` | `fetch` |

هر سه باید امضا را روی **رشتهٔ base64** بررسی کنند، نه JSON.

---

## ۵. آزمون خودکار Ed25519 با کلید ثابت

اگر می‌خواهید منطق بررسی را **بدون سرور** تست کنید، یک جفت کلید ثابت بسازید و یک توکن
دستی امضا کنید؛ سپس همان منطق را اجرا کنید. نمونهٔ ساخت توکن در پایتون:

```python
import base64, json, time
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey
from cryptography.hazmat.primitives import serialization

priv = Ed25519PrivateKey.generate()
pub = priv.public_key().public_bytes(
    serialization.Encoding.Raw, serialization.PublicFormat.Raw)

payload = {"lid": "lic_test", "pid": "prod_test", "pln": "plan_test",
           "sts": "active", "did": "my-machine",
           "iat": int(time.time()), "exp": int(time.time()) + 3600,
           "vun": 0, "upd": 0, "grc": 7, "nce": "test", "fpr": "0011223344556677"}

payload_b64 = base64.urlsafe_b64encode(
    json.dumps(payload, separators=(",", ":")).encode()).rstrip(b"=").decode()
sig = priv.sign(payload_b64.encode("ascii"))
sig_b64 = base64.urlsafe_b64encode(sig).rstrip(b"=").decode()
print(f"{payload_b64}.{sig_b64}")
print("pub(hex):", pub.hex())
```

این خروجی با همان منطق `verify_offline` قابل بررسی است. (این داده‌ها کاملاً ساختگی‌اند.)
