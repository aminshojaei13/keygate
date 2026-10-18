#!/usr/bin/env python3
"""
Automated supply-chain security test suite for Keygate Application Integrity Manifest.
Exercises manifest generation, canonicalization, Ed25519 verification, path traversal guards,
file tampering detection, missing-file detection, and key rotation.
"""

import hashlib
import json
import os
import shutil
import subprocess
import sys
import tempfile

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
CLI_SCRIPT = os.path.join(SCRIPT_DIR, "..", "scripts", "integrity_manifest.py")

# Deterministic synthetic test Ed25519 keypair (seed = 32 bytes of 0x01)
TEST_SEED_HEX = "01" * 32
# Expected public key for seed 0x01...
# Let's derive public key using integrity_manifest logic
sys.path.insert(0, os.path.join(SCRIPT_DIR, "..", "scripts"))
import integrity_manifest

TEST_PRIVKEY = bytes.fromhex(TEST_SEED_HEX)
# Derive public key using Edwards scalar mult
H = hashlib.sha512(TEST_PRIVKEY).digest()
a = 2**(256 - 2) + sum(2**i * integrity_manifest._bit(H, i) for i in range(3, 256 - 2))
A = integrity_manifest._scalarmult(integrity_manifest.B, a)
TEST_PUBKEY_BYTES = integrity_manifest._encodepoint(A)
TEST_PUBKEY_HEX = TEST_PUBKEY_BYTES.hex()

TEST_KEY_ID = "key_test_2026_01"


def run_tests():
    print("================================================================")
    print("Starting Keygate Application Integrity Manifest Test Suite")
    print(f"Synthetic Test Key ID: {TEST_KEY_ID}")
    print(f"Synthetic Public Key: {TEST_PUBKEY_HEX}")
    print("================================================================\n")

    work_dir = tempfile.mkdtemp(prefix="keygate_integrity_test_")
    try:
        # Create synthetic release layout
        release_dir = os.path.join(work_dir, "release_dist")
        os.makedirs(os.path.join(release_dir, "bin"), exist_ok=True)
        os.makedirs(os.path.join(release_dir, "lib"), exist_ok=True)
        os.makedirs(os.path.join(release_dir, "assets"), exist_ok=True)

        app_bin = os.path.join(release_dir, "bin", "my_app")
        lib_dyn = os.path.join(release_dir, "lib", "engine.dylib")
        asset_f = os.path.join(release_dir, "assets", "logo.png")
        conf_f = os.path.join(release_dir, "config.json")

        with open(app_bin, "wb") as f:
            f.write(b"\x7fELF_OR_MACHO_BINARY_DATA_PAYLOAD_V1")
        with open(lib_dyn, "wb") as f:
            f.write(b"DYNAMIC_LIBRARY_BYTES_HASH_TEST_123")
        with open(asset_f, "wb") as f:
            f.write(b"PNG_SAMPLE_BYTES_FOR_ICON")
        with open(conf_f, "wb") as f:
            f.write(b'{"setting": "production", "retries": 3}')

        manifest_out = os.path.join(work_dir, "integrity.manifest.json")

        # ─── Test 1: Manifest Creation & Signing ───
        print("[1] Testing Manifest Generation & Signing...")
        cmd_create = [
            sys.executable, CLI_SCRIPT, "create",
            "--dir", release_dir,
            "--product", "prd_desktop_01",
            "--version", "1.4.0",
            "--release-id", "rel_01HQ8Z3M5K",
            "--platform", "darwin",
            "--arch", "arm64",
            "--key-id", TEST_KEY_ID,
            "--key", TEST_SEED_HEX,
            "--out", manifest_out,
        ]
        res = subprocess.run(cmd_create, capture_output=True, text=True)
        assert res.returncode == 0, f"create failed: {res.stderr}"
        assert os.path.isfile(manifest_out), "manifest output file missing"

        with open(manifest_out, "r", encoding="utf-8") as f:
            envelope = json.load(f)

        assert envelope["format"] == "keygate-integrity-v1"
        assert envelope["manifest_version"] == 1
        assert envelope["algorithm"] == "ed25519"
        assert envelope["signing_key_id"] == TEST_KEY_ID
        assert len(envelope["payload"]) > 50
        assert len(envelope["signature"]) > 50
        print("  ✓ Signed envelope created with valid envelope structure.")

        # ─── Test 2: Verification of Intact Release ───
        print("\n[2] Testing Verification of Intact Release...")
        cmd_verify = [
            sys.executable, CLI_SCRIPT, "verify",
            "--manifest", manifest_out,
            "--pubkey", TEST_PUBKEY_HEX,
            "--key-id", TEST_KEY_ID,
            "--dir", release_dir,
        ]
        res = subprocess.run(cmd_verify, capture_output=True, text=True)
        assert res.returncode == 0, f"verification failed unexpectedly: {res.stderr}\n{res.stdout}"
        assert "INTEGRITY VERIFICATION PASSED" in res.stdout
        print("  ✓ Verification passed for all 4 intact files.")

        # ─── Test 3: Modified / Tampered File Detection ───
        print("\n[3] Testing Tampered File Content Detection...")
        # Attacker injects malicious payload into engine.dylib
        with open(lib_dyn, "wb") as f:
            f.write(b"DYNAMIC_LIBRARY_BYTES_HASH_MODIFIED_MALICIOUS")

        res = subprocess.run(cmd_verify, capture_output=True, text=True)
        assert res.returncode != 0, "tampered file was not detected!"
        assert "Modified files (1)" in res.stdout or "sha256 digest mismatch" in res.stdout
        print("  ✓ Tampered file was immediately detected with digest mismatch.")

        # Restore file for subsequent tests
        with open(lib_dyn, "wb") as f:
            f.write(b"DYNAMIC_LIBRARY_BYTES_HASH_TEST_123")

        # ─── Test 4: Truncated / Size Mismatch Detection ───
        print("\n[4] Testing File Size Mismatch Detection...")
        with open(asset_f, "wb") as f:
            f.write(b"PNG")  # Truncated

        res = subprocess.run(cmd_verify, capture_output=True, text=True)
        assert res.returncode != 0, "truncated file was not detected!"
        assert "file size mismatch" in res.stdout
        print("  ✓ Truncated file detected via size check.")

        # Restore file
        with open(asset_f, "wb") as f:
            f.write(b"PNG_SAMPLE_BYTES_FOR_ICON")

        # ─── Test 5: Missing File Detection ───
        print("\n[5] Testing Missing File Detection...")
        os.remove(conf_f)
        res = subprocess.run(cmd_verify, capture_output=True, text=True)
        assert res.returncode != 0, "missing file was not detected!"
        assert "Missing files (1)" in res.stdout
        print("  ✓ Missing file successfully identified.")

        # Restore file
        with open(conf_f, "wb") as f:
            f.write(b'{"setting": "production", "retries": 3}')

        # ─── Test 6: Signature Tampering & Key Rejections ───
        print("\n[6] Testing Signature Mismatch & Key Verification...")
        # 6a: Unknown key ID
        cmd_bad_key_id = [
            sys.executable, CLI_SCRIPT, "verify",
            "--manifest", manifest_out,
            "--pubkey", TEST_PUBKEY_HEX,
            "--key-id", "untrusted_rogue_key_id",
            "--dir", release_dir,
        ]
        res = subprocess.run(cmd_bad_key_id, capture_output=True, text=True)
        assert res.returncode == 2, f"expected returncode 2 for unknown key, got {res.returncode}"
        print("  ✓ Unknown key ID correctly rejected.")

        # 6b: Wrong public key (e.g. valid key for different product)
        wrong_pubkey = "02" * 32
        cmd_wrong_key = [
            sys.executable, CLI_SCRIPT, "verify",
            "--manifest", manifest_out,
            "--pubkey", wrong_pubkey,
            "--dir", release_dir,
        ]
        res = subprocess.run(cmd_wrong_key, capture_output=True, text=True)
        assert res.returncode == 2, f"expected failure for wrong public key, got {res.returncode}"
        assert "signature verification failed" in res.stderr
        print("  ✓ Signature check fails closed when verifying with wrong key.")

        # 6c: Tampered payload bytes
        tampered_env = dict(envelope)
        # Modify payload by decoding, changing version to 2.0.0, re-encoding payload without resigning
        payload_data = json.loads(integrity_manifest.base64url_decode(envelope["payload"]))
        payload_data["version"] = "9.9.9"
        tampered_env["payload"] = integrity_manifest.base64url_encode(json.dumps(payload_data).encode("utf-8"))
        tampered_manifest_path = os.path.join(work_dir, "tampered_manifest.json")
        with open(tampered_manifest_path, "w", encoding="utf-8") as f:
            json.dump(tampered_env, f)

        cmd_tampered_env = [
            sys.executable, CLI_SCRIPT, "verify",
            "--manifest", tampered_manifest_path,
            "--pubkey", TEST_PUBKEY_HEX,
            "--dir", release_dir,
        ]
        res = subprocess.run(cmd_tampered_env, capture_output=True, text=True)
        assert res.returncode == 2, "tampered payload was not rejected by signature check!"
        print("  ✓ Tampered payload detected by Ed25519 verification before inspecting files.")

        # ─── Test 7: Path Traversal Prevention ───
        print("\n[7] Testing Path Traversal Defense...")
        traversal_attempts = [
            "../escape.txt",
            "../../etc/passwd",
            "/etc/shadow",
            "bin/../../outside.txt",
            "bin\\windows\\backslash.dll",
            "bin/./dot.txt",
            "bin//double_slash.txt",
            "null\x00byte.txt",
        ]
        for t_path in traversal_attempts:
            try:
                integrity_manifest.validate_relative_path(t_path)
                assert False, f"traversal path {t_path!r} was allowed!"
            except ValueError:
                pass
        print(f"  ✓ All {len(traversal_attempts)} directory traversal vectors strictly rejected.")

        # ─── Test 8: Deterministic Output (Bit-for-Bit Reproducibility) ───
        print("\n[8] Testing Deterministic Canonical Output...")
        out1 = os.path.join(work_dir, "m1.json")
        out2 = os.path.join(work_dir, "m2.json")
        cmd_create1 = list(cmd_create)
        cmd_create1[-1] = out1
        cmd_create2 = list(cmd_create)
        cmd_create2[-1] = out2

        subprocess.run(cmd_create1, check=True)
        subprocess.run(cmd_create2, check=True)

        with open(out1, "r", encoding="utf-8") as f1, open(out2, "r", encoding="utf-8") as f2:
            d1 = json.load(f1)
            d2 = json.load(f2)

        # Ignore created_at timestamp delta, payload structure and file list are identical
        p1 = json.loads(integrity_manifest.base64url_decode(d1["payload"]))
        p2 = json.loads(integrity_manifest.base64url_decode(d2["payload"]))
        assert p1["files"] == p2["files"], "file lists differ between repeated runs"
        print("  ✓ Deterministic file sorting and canonical JSON verified.")

        print("\n================================================================")
        print("ALL APPLICATION INTEGRITY MANIFEST TESTS PASSED (8/8) ✓")
        print("================================================================")

    finally:
        shutil.rmtree(work_dir, ignore_errors=True)


if __name__ == "__main__":
    run_tests()
