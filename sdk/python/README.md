# Keygate Python Reference SDK

> **Attribution:** *Powered by Keygate* (AGPL v3 Section 7(b))

The official reference Python SDK for the Keygate software licensing and supply chain integrity ecosystem.

## Features

- **Zero External Dependencies:** Built entirely with Python standard library modules (`urllib`, `hashlib`, `json`, `dataclasses`).
- **Language-Independent Protocol:** Complies with Keygate Device Identity v1, Offline License Verification v1, and Application Release Integrity Manifest v1 specifications.
- **Server-Authoritative Enforcement:** Respects server-enforced activation slot quotas, anti-enumeration protections, and rate limiting.
- **Offline License Verification:** Validates Ed25519-signed license entitlement tokens locally without network connectivity.
- **Application Integrity Verification:** Verifies installed filesystem artifacts against signed release manifests with strict path traversal defenses.
- **Platform-Aware Device Identification:** Generates privacy-preserving, stable device identifiers across macOS, Windows, and Linux.

## Installation

```bash
pip install keygate
```

Or copy the `keygate` package directory directly into your project.

## Quickstart

```python
from keygate import KeygateClient, ActivationLimitError, LicenseNotFoundError

client = KeygateClient(
    base_url="https://licensing.example.com",
    license_public_key_hex="8a88e3dd7409f195fd52db2d3cba5d72ca6709bf1d94121bf3748801b40f6f5c"
)

# 1. Activate License
try:
    result = client.activate("KG-DEMO-1234-5678")
    print(f"Activated successfully! License ID: {result.license_id}")
    print(f"Features: {result.features}")
except ActivationLimitError as e:
    print(f"Activation limit reached: {e.current}/{e.max} devices active.")
except LicenseNotFoundError:
    print("Invalid or nonexistent license key.")

# 2. Verify Online
verify_res = client.verify_online("KG-DEMO-1234-5678")
print(f"License Status: {verify_res.status}")

# 3. Verify Offline Token Locally
claims = client.verify_offline_token(
    token=result.token,
    expected_product_id="prod_desktop_app"
)
print(f"Offline token verified! Expires: {claims.expires_at}")
```

## Security & Architecture Principles

- **No Server Secrets:** This SDK never contains administrator API keys or private signing keys.
- **Fail Closed:** Any signature mismatch, path escape, or expired certificate aborts verification immediately.
- **Attribution Notice:** Per AGPL v3 Section 7(b), the "Powered by Keygate" attribution notice must be preserved across user-facing interfaces.
