#!/usr/bin/env python3
"""
Generates deterministic cross-language test vectors for Keygate offline license tokens.
Output: test/offline_token_test_vectors.json
"""

import hashlib
import json
import os
import sys

# Import Ed25519 routines from scripts/integrity_manifest.py
sys.path.insert(0, os.path.join(os.path.dirname(__file__)))
from integrity_manifest import (
    base64url_decode,
    base64url_encode,
    ed25519_sign,
    _pure_python_ed25519_publickey,
)

SEED_PRIMARY_HEX = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
SEED_SECONDARY_HEX = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210"
SEED_UNTRUSTED_HEX = "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"

seed_primary = bytes.fromhex(SEED_PRIMARY_HEX)
seed_secondary = bytes.fromhex(SEED_SECONDARY_HEX)
seed_untrusted = bytes.fromhex(SEED_UNTRUSTED_HEX)

pub_primary = _pure_python_ed25519_publickey(seed_primary)
pub_secondary = _pure_python_ed25519_publickey(seed_secondary)
pub_untrusted = _pure_python_ed25519_publickey(seed_untrusted)

kid_primary = "key_" + hashlib.sha256(pub_primary).hexdigest()[:16]
kid_secondary = "key_" + hashlib.sha256(pub_secondary).hexdigest()[:16]
kid_untrusted = "key_" + hashlib.sha256(pub_untrusted).hexdigest()[:16]


def compute_fingerprint(did: str, pid: str) -> str:
    h = hashlib.sha256(f"{did}:{pid}".encode("utf-8")).digest()
    return h[:8].hex()


def sign_token(payload_dict: dict, priv_seed: bytes) -> str:
    # Deterministic JSON without spaces
    payload_json = json.dumps(payload_dict, separators=(",", ":"), sort_keys=True).encode("utf-8")
    b64_payload = base64url_encode(payload_json)
    sig_bytes = ed25519_sign(priv_seed, b64_payload.encode("ascii"))
    b64_sig = base64url_encode(sig_bytes)
    return f"{b64_payload}.{b64_sig}"


def main():
    test_did = "kgid:v1:darwin:abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789"
    test_pid = "prod_desktop_app"
    test_fpr = compute_fingerprint(test_did, test_pid)

    ref_now = 1776500000  # 2026-04-18T10:53:20Z

    vectors = []

    # 1. Valid current token
    tok1_payload = {
        "ver": "1",
        "kid": kid_primary,
        "lid": "lic_test_12345",
        "pid": test_pid,
        "pln": "plan_pro_annual",
        "sts": "active",
        "did": test_did,
        "fpr": test_fpr,
        "ftr": {"pro": True, "max_exports": 100, "cloud_sync": True},
        "iat": ref_now - 3600,
        "exp": ref_now + (30 * 86400),
        "vun": ref_now + (365 * 86400),
        "upd": ref_now + (365 * 86400),
        "grc": 7,
        "nce": "fixed-nonce-0001",
    }
    tok1_raw = sign_token(tok1_payload, seed_primary)
    vectors.append({
        "name": "valid_current_token",
        "description": "Validly signed active token within expiration and term limits.",
        "token": tok1_raw,
        "expected_result": "valid",
        "expected_error": None,
        "evaluation_time": ref_now,
        "expected_claims": tok1_payload,
        "verify_params": {
            "expected_product_id": test_pid,
            "expected_identifier": test_did,
            "required_features": ["pro", "cloud_sync"]
        }
    })

    # 2. Expired offline token
    tok2_payload = dict(tok1_payload)
    tok2_payload["exp"] = ref_now - 100
    tok2_payload["nce"] = "fixed-nonce-0002"
    tok2_raw = sign_token(tok2_payload, seed_primary)
    vectors.append({
        "name": "expired_token",
        "description": "Token whose offline check-in window (exp) has expired.",
        "token": tok2_raw,
        "expected_result": "invalid",
        "expected_error": "token expired",
        "evaluation_time": ref_now,
        "verify_params": {
            "expected_product_id": test_pid,
            "expected_identifier": test_did
        }
    })

    # 3. License term expired (vun + grace passed)
    tok3_payload = dict(tok1_payload)
    tok3_payload["vun"] = ref_now - (10 * 86400)
    tok3_payload["grc"] = 3
    tok3_payload["nce"] = "fixed-nonce-0003"
    tok3_raw = sign_token(tok3_payload, seed_primary)
    vectors.append({
        "name": "license_term_expired",
        "description": "Token whose license term (vun) plus grace period (grc) has lapsed.",
        "token": tok3_raw,
        "expected_result": "invalid",
        "expected_error": "license term expired",
        "evaluation_time": ref_now,
        "verify_params": {
            "expected_product_id": test_pid,
            "expected_identifier": test_did
        }
    })

    # 4. Invalid signature (flipped bit in signature)
    tok1_parts = tok1_raw.split(".")
    sig_chars = list(tok1_parts[1])
    sig_chars[10] = "A" if sig_chars[10] != "A" else "B"
    tok4_raw = f"{tok1_parts[0]}.{''.join(sig_chars)}"
    vectors.append({
        "name": "invalid_signature",
        "description": "Token where signature bytes were tampered.",
        "token": tok4_raw,
        "expected_result": "invalid",
        "expected_error": "invalid signature",
        "evaluation_time": ref_now,
        "verify_params": {}
    })

    # 5. Tampered payload
    tok5_payload = dict(tok1_payload)
    tok5_payload["sts"] = "active"
    tok5_payload["ftr"]["enterprise_unlimited"] = True
    tok5_payload_json = json.dumps(tok5_payload, separators=(",", ":"), sort_keys=True).encode("utf-8")
    tampered_b64 = base64url_encode(tok5_payload_json)
    tok5_raw = f"{tampered_b64}.{tok1_parts[1]}"  # signature from tok1
    vectors.append({
        "name": "tampered_payload",
        "description": "Token where payload was altered after signing.",
        "token": tok5_raw,
        "expected_result": "invalid",
        "expected_error": "invalid signature",
        "evaluation_time": ref_now,
        "verify_params": {}
    })

    # 6. Unknown signing key (signed by untrusted key)
    tok6_payload = dict(tok1_payload)
    tok6_payload["kid"] = kid_untrusted
    tok6_payload["nce"] = "fixed-nonce-0006"
    tok6_raw = sign_token(tok6_payload, seed_untrusted)
    vectors.append({
        "name": "unknown_signing_key",
        "description": "Token signed with key not present in trusted keyring.",
        "token": tok6_raw,
        "expected_result": "invalid",
        "expected_error": "unknown signing key",
        "evaluation_time": ref_now,
        "verify_params": {}
    })

    # 7. Rotated key token (valid under secondary key)
    tok7_payload = dict(tok1_payload)
    tok7_payload["kid"] = kid_secondary
    tok7_payload["nce"] = "fixed-nonce-0007"
    tok7_raw = sign_token(tok7_payload, seed_secondary)
    vectors.append({
        "name": "rotated_key_token",
        "description": "Token signed with secondary/rotated key in trusted keyring.",
        "token": tok7_raw,
        "expected_result": "valid",
        "expected_error": None,
        "evaluation_time": ref_now,
        "verify_params": {
            "expected_product_id": test_pid,
            "expected_identifier": test_did
        }
    })

    # 8. Wrong product ID
    tok8_payload = dict(tok1_payload)
    tok8_payload["pid"] = "prod_unrelated_app"
    tok8_payload["fpr"] = compute_fingerprint(test_did, "prod_unrelated_app")
    tok8_payload["nce"] = "fixed-nonce-0008"
    tok8_raw = sign_token(tok8_payload, seed_primary)
    vectors.append({
        "name": "wrong_product_id",
        "description": "Token signed for a different product than expected.",
        "token": tok8_raw,
        "expected_result": "invalid",
        "expected_error": "product ID mismatch",
        "evaluation_time": ref_now,
        "verify_params": {
            "expected_product_id": test_pid
        }
    })

    # 9. Incorrect device binding
    other_did = "kgid:v1:windows:1122334455667788112233445566778811223344556677881122334455667788"
    vectors.append({
        "name": "incorrect_device_binding",
        "description": "Token issued for device A verified on device B.",
        "token": tok1_raw,
        "expected_result": "invalid",
        "expected_error": "device identifier mismatch",
        "evaluation_time": ref_now,
        "verify_params": {
            "expected_product_id": test_pid,
            "expected_identifier": other_did
        }
    })

    # 10. Missing entitlement
    vectors.append({
        "name": "missing_entitlement",
        "description": "Token missing an application-required feature flag.",
        "token": tok1_raw,
        "expected_result": "invalid",
        "expected_error": "missing required entitlement",
        "evaluation_time": ref_now,
        "verify_params": {
            "expected_product_id": test_pid,
            "required_features": ["pro", "satellite_uplink"]
        }
    })

    # 11. Malformed token (missing dot)
    vectors.append({
        "name": "malformed_token_no_dot",
        "description": "Token without dot delimiter.",
        "token": "eyJsaWQiOiJsaWNfdGVzdF8xMjM0NSJ9",
        "expected_result": "invalid",
        "expected_error": "invalid token format",
        "evaluation_time": ref_now,
        "verify_params": {}
    })

    # 12. Malformed token (corrupted base64)
    vectors.append({
        "name": "malformed_token_corrupted_base64",
        "description": "Token with invalid base64 characters.",
        "token": "!!!not_b64!!!.!!!not_b64!!!",
        "expected_result": "invalid",
        "expected_error": "invalid signature",
        "evaluation_time": ref_now,
        "verify_params": {}
    })

    # 13. Unsupported token version
    tok13_payload = dict(tok1_payload)
    tok13_payload["ver"] = "99"
    tok13_payload["nce"] = "fixed-nonce-0013"
    tok13_raw = sign_token(tok13_payload, seed_primary)
    vectors.append({
        "name": "unsupported_version",
        "description": "Token with unsupported schema version (ver=99).",
        "token": tok13_raw,
        "expected_result": "invalid",
        "expected_error": "unsupported token version",
        "evaluation_time": ref_now,
        "verify_params": {}
    })

    # 14. Legacy token without ver or kid (backward compatibility)
    tok14_payload = dict(tok1_payload)
    del tok14_payload["ver"]
    del tok14_payload["kid"]
    tok14_payload["nce"] = "fixed-nonce-0014"
    tok14_raw = sign_token(tok14_payload, seed_primary)
    vectors.append({
        "name": "legacy_token_no_kid_or_ver",
        "description": "Legacy token lacking ver and kid claims verified with primary key.",
        "token": tok14_raw,
        "expected_result": "valid",
        "expected_error": None,
        "evaluation_time": ref_now,
        "verify_params": {
            "expected_product_id": test_pid,
            "expected_identifier": test_did
        }
    })

    output_data = {
        "format": "keygate-offline-token-test-vectors-v1",
        "synthetic_keyring": {
            kid_primary: {
                "algorithm": "ed25519",
                "public_key_hex": pub_primary.hex(),
                "status": "active",
                "description": "Primary synthetic test signing key"
            },
            kid_secondary: {
                "algorithm": "ed25519",
                "public_key_hex": pub_secondary.hex(),
                "status": "retired",
                "description": "Secondary synthetic test signing key (rotated)"
            }
        },
        "untrusted_key": {
            "kid": kid_untrusted,
            "public_key_hex": pub_untrusted.hex(),
            "description": "Key intentionally omitted from trusted keyring"
        },
        "vectors": vectors
    }

    out_path = os.path.join(os.path.dirname(os.path.dirname(__file__)), "test", "offline_token_test_vectors.json")
    with open(out_path, "w", encoding="utf-8") as f:
        json.dump(output_data, f, indent=2)
    print(f"Generated {len(vectors)} test vectors -> {out_path}")


if __name__ == "__main__":
    main()
