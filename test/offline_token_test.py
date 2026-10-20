#!/usr/bin/env python3
"""
Test runner for Keygate Offline License Tokens.
Validates the language-independent verification algorithm against test/offline_token_test_vectors.json.
"""

import hashlib
import json
import os
import sys

# Import Ed25519 & base64url routines from scripts/integrity_manifest.py
sys.path.insert(0, os.path.join(os.path.dirname(os.path.dirname(__file__)), "scripts"))
from integrity_manifest import (
    base64url_decode,
    _pure_python_ed25519_verify,
)


def compute_fingerprint(did: str, pid: str) -> str:
    h = hashlib.sha256(f"{did}:{pid}".encode("utf-8")).digest()
    return h[:8].hex()


class TokenVerificationError(Exception):
    pass


def verify_offline_token(
    raw_token: str,
    keyring: dict[str, bytes],
    ref_time: int,
    clock_skew: int = 0,
    expected_product_id: str = None,
    expected_identifier: str = None,
    required_features: list[str] = None,
    max_offline_duration: int = 0,
) -> dict:
    """
    Conforming implementation of the Keygate Offline License Token Verification Protocol.
    """
    if not raw_token or not isinstance(raw_token, str):
        raise TokenVerificationError("invalid token format")

    parts = raw_token.split(".")
    if len(parts) != 2 or not parts[0] or not parts[1]:
        raise TokenVerificationError("invalid token format")

    b64_payload, b64_sig = parts[0], parts[1]

    try:
        sig_bytes = base64url_decode(b64_sig)
    except Exception as e:
        raise TokenVerificationError(f"signature decode error: {e}")

    if len(sig_bytes) != 64:
        raise TokenVerificationError("invalid signature")

    try:
        payload_bytes = base64url_decode(b64_payload)
    except Exception as e:
        raise TokenVerificationError(f"payload decode error: {e}")

    try:
        claims = json.loads(payload_bytes.decode("utf-8"))
    except Exception as e:
        raise TokenVerificationError(f"payload unmarshal error: {e}")

    # Check version
    ver = claims.get("ver")
    if ver is not None and ver != "1":
        raise TokenVerificationError(f"unsupported token version: {ver}")

    # Resolve signing key
    kid = claims.get("kid")
    pubkey = None
    if kid:
        if kid not in keyring:
            raise TokenVerificationError(f"unknown signing key: {kid}")
        pubkey = keyring[kid]
    else:
        # Backward compatibility for legacy tokens without kid
        if len(keyring) == 1:
            pubkey = next(iter(keyring.values()))
        elif "primary" in keyring:
            pubkey = keyring["primary"]
        else:
            # Try first available key in keyring
            pubkey = next(iter(keyring.values()))

    if not pubkey or len(pubkey) != 32:
        raise TokenVerificationError("ed25519 public key not initialised")

    # Cryptographic signature verification over base64url(payload)
    if not _pure_python_ed25519_verify(pubkey, b64_payload.encode("ascii"), sig_bytes):
        raise TokenVerificationError("invalid signature")

    # Duration check
    iat = claims.get("iat", 0)
    exp = claims.get("exp", 0)
    if max_offline_duration > 0 and iat > 0 and exp > 0:
        if (exp - iat) > max_offline_duration:
            raise TokenVerificationError("token offline duration exceeds permitted limit")

    # Future issuance check
    if iat > (ref_time + clock_skew):
        raise TokenVerificationError("token issued in the future")

    # Offline expiration check
    if exp > 0 and (ref_time - clock_skew) > exp:
        raise TokenVerificationError("token expired")

    # License term expiration check
    vun = claims.get("vun", 0)
    if vun > 0:
        grc_days = claims.get("grc", 0)
        term_deadline = vun + (grc_days * 86400)
        if (ref_time - clock_skew) > term_deadline:
            raise TokenVerificationError("license term expired")

    # Product ID validation
    if expected_product_id:
        if claims.get("pid") != expected_product_id:
            raise TokenVerificationError(
                f"product ID mismatch: got {claims.get('pid')}, want {expected_product_id}"
            )

    # Device binding validation
    if expected_identifier:
        did = claims.get("did")
        if did and did != expected_identifier:
            raise TokenVerificationError(
                f"device identifier mismatch: got {did}, want {expected_identifier}"
            )
        fpr = claims.get("fpr")
        if fpr:
            expected_fpr = compute_fingerprint(expected_identifier, claims.get("pid", ""))
            if fpr != expected_fpr:
                raise TokenVerificationError(
                    f"device fingerprint mismatch: got {fpr}, want {expected_fpr}"
                )

    # Required entitlements / features
    if required_features:
        features = claims.get("ftr", {})
        for feat in required_features:
            if not features.get(feat):
                raise TokenVerificationError(f"missing required entitlement: {feat}")

    return claims


def run_tests():
    vectors_file = os.path.join(os.path.dirname(__file__), "offline_token_test_vectors.json")
    if not os.path.exists(vectors_file):
        print(f"Error: vectors file not found: {vectors_file}")
        sys.exit(1)

    with open(vectors_file, "r", encoding="utf-8") as f:
        data = json.load(f)

    # Build keyring from test vectors
    keyring = {}
    for kid, info in data["synthetic_keyring"].items():
        keyring[kid] = bytes.fromhex(info["public_key_hex"])

    vectors = data["vectors"]
    passed = 0
    total = len(vectors)

    print("=" * 64)
    print("Starting Keygate Offline License Token Test Suite")
    print(f"Loaded {total} test vectors from {os.path.basename(vectors_file)}")
    print(f"Active Keyring Keys: {list(keyring.keys())}")
    print("=" * 64)
    print()

    for idx, vec in enumerate(vectors, start=1):
        name = vec["name"]
        raw_tok = vec["token"]
        expected_result = vec["expected_result"]
        expected_err = vec["expected_error"]
        ref_time = vec["evaluation_time"]
        v_params = vec.get("verify_params", {})

        print(f"[{idx}/{total}] Testing '{name}'...")

        try:
            claims = verify_offline_token(
                raw_token=raw_tok,
                keyring=keyring,
                ref_time=ref_time,
                expected_product_id=v_params.get("expected_product_id"),
                expected_identifier=v_params.get("expected_identifier"),
                required_features=v_params.get("required_features"),
            )
            if expected_result == "valid":
                print(f"  ✓ Valid token successfully accepted ({claims.get('lid')})")
                passed += 1
            else:
                print(f"  ✗ FAILED: Expected error '{expected_err}', but token was accepted!")
                sys.exit(1)
        except TokenVerificationError as e:
            err_msg = str(e)
            if expected_result == "invalid":
                if expected_err and expected_err.lower() not in err_msg.lower():
                    print(f"  ✗ FAILED: Expected error containing '{expected_err}', got: '{err_msg}'")
                    sys.exit(1)
                print(f"  ✓ Expected failure correctly triggered: {err_msg}")
                passed += 1
            else:
                print(f"  ✗ FAILED: Expected valid token, but got error: {err_msg}")
                sys.exit(1)

    print()
    print("=" * 64)
    print(f"ALL OFFLINE LICENSE TOKEN TESTS PASSED ({passed}/{total}) ✓")
    print("=" * 64)


if __name__ == "__main__":
    run_tests()
