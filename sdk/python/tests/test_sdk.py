"""
Comprehensive Test Suite for the Keygate Reference Python SDK.
Tests client operations, device identification, offline tokens, and manifest verification.
"""

import json
import os
import sys
import tempfile
import unittest

# Add sdk/python to path
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from keygate import (
    KeygateClient,
    DeviceIdentifier,
    OfflineTokenVerifier,
    ManifestVerifier,
    ActivationLimitError,
    LicenseNotFoundError,
    APIError,
    TimeoutError,
    OfflineVerificationError,
    ManifestVerificationError,
)

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))))
VECTORS_PATH = os.path.join(REPO_ROOT, "test", "offline_token_test_vectors.json")


class TestDeviceIdentifier(unittest.TestCase):
    def test_get_canonical_platform(self):
        plat = DeviceIdentifier.get_canonical_platform()
        self.assertIn(plat, ["darwin", "windows", "linux", "other"])

    def test_compute_fingerprint(self):
        fp1 = DeviceIdentifier.compute_fingerprint("device-1", "prod-1")
        fp2 = DeviceIdentifier.compute_fingerprint("device-1", "prod-1")
        self.assertEqual(fp1, fp2)
        self.assertEqual(len(fp1), 16)

        fp3 = DeviceIdentifier.compute_fingerprint("device-2", "prod-1")
        self.assertNotEqual(fp1, fp3)

    def test_get_or_create_device_id(self):
        with tempfile.TemporaryDirectory() as tmp_dir:
            dev_id = DeviceIdentifier.get_or_create_device_id(storage_dir=tmp_dir)
            self.assertTrue(len(dev_id) > 10)
            # Re-reading should yield the exact same identifier
            dev_id_cached = DeviceIdentifier.get_or_create_device_id(storage_dir=tmp_dir)
            self.assertEqual(dev_id, dev_id_cached)


class TestOfflineTokenVerifier(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        with open(VECTORS_PATH, "r", encoding="utf-8") as f:
            cls.vectors_data = json.load(f)
        cls.keyring = {}
        for kid, kinfo in cls.vectors_data["synthetic_keyring"].items():
            cls.keyring[kid] = bytes.fromhex(kinfo["public_key_hex"])
        cls.verifier = OfflineTokenVerifier(keyring=cls.keyring)

    def test_valid_token(self):
        vec = self.vectors_data["vectors"][0]
        claims = self.verifier.verify(
            raw_token=vec["token"],
            now=vec["evaluation_time"],
            expected_product_id=vec["verify_params"]["expected_product_id"],
            expected_identifier=vec["verify_params"]["expected_identifier"],
            required_features=["pro", "cloud_sync"],
        )
        self.assertEqual(claims.license_id, "lic_test_12345")
        self.assertEqual(claims.version, "1")
        self.assertTrue(claims.features.get("pro"))

    def test_expired_token(self):
        vec = [v for v in self.vectors_data["vectors"] if v["name"] == "expired_token"][0]
        with self.assertRaises(OfflineVerificationError) as ctx:
            self.verifier.verify(raw_token=vec["token"], now=vec["evaluation_time"], clock_skew=0)
        self.assertEqual(ctx.exception.code, "TOKEN_EXPIRED")

    def test_invalid_signature(self):
        vec = [v for v in self.vectors_data["vectors"] if v["name"] == "invalid_signature"][0]
        with self.assertRaises(OfflineVerificationError) as ctx:
            self.verifier.verify(raw_token=vec["token"], now=vec["evaluation_time"])
        self.assertEqual(ctx.exception.code, "INVALID_SIGNATURE")

    def test_product_mismatch(self):
        vec = [v for v in self.vectors_data["vectors"] if v["name"] == "wrong_product_id"][0]
        with self.assertRaises(OfflineVerificationError) as ctx:
            self.verifier.verify(
                raw_token=vec["token"],
                now=vec["evaluation_time"],
                expected_product_id="prod_desktop_app",
            )
        self.assertEqual(ctx.exception.code, "PRODUCT_MISMATCH")

    def test_device_mismatch(self):
        vec = [v for v in self.vectors_data["vectors"] if v["name"] == "incorrect_device_binding"][0]
        with self.assertRaises(OfflineVerificationError) as ctx:
            self.verifier.verify(
                raw_token=vec["token"],
                now=vec["evaluation_time"],
                expected_identifier=vec["verify_params"]["expected_identifier"],
            )
        self.assertEqual(ctx.exception.code, "DEVICE_MISMATCH")


class TestManifestVerifier(unittest.TestCase):
    def test_path_traversal_detection(self):
        for bad_path in ["../secret.txt", "/etc/passwd", "dir/../../file", "C:\\windows\\system32", "dir//file"]:
            with self.assertRaises(ManifestVerificationError):
                ManifestVerifier.validate_relative_path(bad_path)

    def test_verify_manifest_end_to_end(self):
        sys.path.insert(0, os.path.join(REPO_ROOT, "scripts"))
        from integrity_manifest import scan_directory, sign_manifest, _pure_python_ed25519_publickey

        seed = bytes.fromhex("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")
        pubkey = _pure_python_ed25519_publickey(seed)

        verifier = ManifestVerifier(trusted_keys={"key_test": pubkey})

        with tempfile.TemporaryDirectory() as tmp_dir:
            file_a = os.path.join(tmp_dir, "bin", "app")
            os.makedirs(os.path.dirname(file_a), exist_ok=True)
            with open(file_a, "wb") as f:
                f.write(b"app content 12345")

            files = scan_directory(tmp_dir)
            m = {
                "manifest_version": 1,
                "product_id": "test.app",
                "release_id": "1.0.0",
                "created_at": "2026-10-18T09:00:00Z",
                "files": files,
            }
            envelope = sign_manifest(m, seed, key_id="key_test")

            # 1. Verification of intact release must pass
            manifest = verifier.verify_envelope(envelope)
            verifier.verify_installed_files(manifest, tmp_dir)
            self.assertEqual(manifest.product_id, "test.app")

            # 2. Size mismatch must fail
            with open(file_a, "wb") as f:
                f.write(b"truncated")
            with self.assertRaises(ManifestVerificationError) as ctx:
                verifier.verify_installed_files(manifest, tmp_dir)
            self.assertEqual(ctx.exception.code, "SIZE_MISMATCH")

            # 3. Hash mismatch (same 17 bytes, different content) must fail
            with open(file_a, "wb") as f:
                f.write(b"app content 99999")
            with self.assertRaises(ManifestVerificationError) as ctx:
                verifier.verify_installed_files(manifest, tmp_dir)
            self.assertEqual(ctx.exception.code, "HASH_MISMATCH")


class TestErrorHierarchy(unittest.TestCase):
    def test_activation_limit_error(self):
        err = ActivationLimitError("Limit reached", details={"current": 2, "max": 2})
        self.assertEqual(err.http_status, 409)
        self.assertEqual(err.code, "ACTIVATION_LIMIT")
        self.assertEqual(err.current, 2)
        self.assertEqual(err.max, 2)

    def test_license_not_found_error(self):
        err = LicenseNotFoundError()
        self.assertEqual(err.http_status, 404)
        self.assertEqual(err.code, "LICENSE_NOT_FOUND")


if __name__ == "__main__":
    unittest.main()
