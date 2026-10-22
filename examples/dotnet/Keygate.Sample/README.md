> روی .NET Core 3.1 هستید؟ نسخهٔ سازگار با C# 8 در [`../Keygate.Sample.NetCore31`](../Keygate.Sample.NetCore31) قرار دارد.

# Keygate.Sample (.NET)

A minimal .NET console client that exercises the **public SDK surface** of a Keygate
server (`/api/v1/license/*`). It never touches `/api/v1/admin/*`, so it is safe to
ship inside a desktop application.

It demonstrates the full loop:

* `activate` — bind this device to a license and receive a signed offline token
* `verify` — re-check a license and refresh the token
* `offline` — verify a saved token **with no network access** (Ed25519 signature + expiry)
* `pubkey` — fetch the server's Ed25519 public key

## Requirements

* .NET SDK 8.0 or newer
* Network access to a running Keygate server (or use `offline` after one online run)

The only NuGet dependency is **BouncyCastle.Cryptography** (pure managed Ed25519, no
native libraries).

## Configure

`appsettings.json`:

```json
{ "Keygate": { "BaseUrl": "http://localhost:9000" } }
```

Environment override: `KEYGATE_BASE_URL=http://localhost:9000`.

> Use only **fake** license keys while developing. Never commit a real key.

## Run

```bash
# 1) Fetch the Ed25519 public key
dotnet run -- pubkey

# 2) Activate this device (license key is a stand-in; use your own test key)
dotnet run -- activate --license-key KG-AAAA-BBBB-CCCC-DDDD --identifier "kgid:v1:windows:5581b5a7b290e096fdea9b19916561e13d15fc983a9b57c53a9e5ad91f29f4b7"

# 3) Verify and refresh the offline token
dotnet run -- verify --license-key KG-AAAA-BBBB-CCCC-DDDD --identifier "kgid:v1:windows:5581b5a7b290e096fdea9b19916561e13d15fc983a9b57c53a9e5ad91f29f4b7"

# 4) Verify the saved token with no network
dotnet run -- offline --identifier "kgid:v1:windows:5581b5a7b290e096fdea9b19916561e13d15fc983a9b57c53a9e5ad91f29f4b7"
```

Options: `--label "Office PC"`, `--token-file keygate-token.txt`.

> The long `kgid:v1:...` value above is a fixed demo identifier. In a real app, compute
> it from stable hardware/OS components at startup (never hardcode it). The server binds
> the license to whatever identifier the first activation sends, and strict mode refuses
> a different one after a hardware or OS change.

## Build

```bash
dotnet build
dotnet publish -c Release
```

## What it writes to disk

* `keygate-token.txt` — the latest signed token
* `keygate-token.txt.pub.hex` — the server public key, so `offline` works without a server

In a production app, store the public key **pinned at build time** instead of fetching it
at runtime (see `docs/team-guide/offline-licensing-guide.md`).

## Error handling

Every API failure becomes a `KeygateApiException` carrying the HTTP status and the
server's machine-readable `code`. `Friendly()` maps the documented codes to actionable
messages.
