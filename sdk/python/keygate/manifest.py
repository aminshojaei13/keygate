"""
Keygate Application Release Integrity Manifest Verification Engine.
Implements the language-independent Keygate Integrity Verification Protocol (v1).
"""

import hashlib
import json
import os
import posixpath
from typing import Optional

from .errors import ManifestVerificationError
from .models import Manifest, ManifestFileEntry
from .offline import _ed25519_verify_pure, base64url_decode


class ManifestVerifier:
    """
    Verifies installed application files against an Ed25519-signed Keygate integrity manifest envelope.
    """

    FORMAT_IDENTIFIER = "keygate-manifest-envelope-v1"

    def __init__(self, trusted_keys: Optional[dict[str, bytes]] = None, single_pubkey: Optional[bytes] = None):
        self.keyring: dict[str, bytes] = dict(trusted_keys or {})
        if single_pubkey:
            if len(single_pubkey) != 32:
                raise ValueError("public key must be 32 bytes")
            kid = "key_" + hashlib.sha256(single_pubkey).hexdigest()[:16]
            self.keyring[kid] = single_pubkey
            self.default_kid = kid
        else:
            self.default_kid = next(iter(self.keyring.keys())) if self.keyring else None

    def add_key(self, kid: str, pubkey_bytes: bytes) -> None:
        """Adds a trusted release verification key."""
        if len(pubkey_bytes) != 32:
            raise ValueError("public key must be 32 bytes")
        self.keyring[kid] = pubkey_bytes
        if not self.default_kid:
            self.default_kid = kid

    @staticmethod
    def validate_relative_path(path_str: str) -> None:
        """Enforces strict POSIX relative path security rules."""
        if not path_str or not isinstance(path_str, str):
            raise ManifestVerificationError("empty or non-string file path in manifest", "INVALID_PATH")
        if "\\" in path_str:
            raise ManifestVerificationError(f"path '{path_str}' contains backslashes; must use POSIX '/'", "INVALID_PATH")
        if path_str.startswith("/"):
            raise ManifestVerificationError(f"path '{path_str}' is absolute; leading slashes forbidden", "PATH_TRAVERSAL")
        if ":" in path_str and (len(path_str) >= 2 and path_str[1] == ":"):
            raise ManifestVerificationError(f"path '{path_str}' contains Windows drive letter", "PATH_TRAVERSAL")
        if "//" in path_str:
            raise ManifestVerificationError(f"path '{path_str}' contains empty segment '//'", "INVALID_PATH")

        parts = path_str.split("/")
        for part in parts:
            if part in (".", ".."):
                raise ManifestVerificationError(f"path '{path_str}' contains traversal token '{part}'", "PATH_TRAVERSAL")

    SUPPORTED_FORMATS = {"keygate-integrity-v1", "keygate-manifest-envelope-v1"}

    def verify_envelope(self, envelope: dict) -> Manifest:
        """
        Cryptographically verifies the signed envelope and returns the deserialized Manifest.
        """
        fmt = envelope.get("format")
        if fmt not in self.SUPPORTED_FORMATS:
            raise ManifestVerificationError(f"unsupported envelope format: '{fmt}'", "UNSUPPORTED_FORMAT")

        algo = envelope.get("algorithm")
        if algo != "ed25519":
            raise ManifestVerificationError(f"unsupported algorithm: '{algo}'", "UNSUPPORTED_ALGORITHM")

        kid = envelope.get("key_id") or envelope.get("signing_key_id")
        pubkey = None
        if kid and kid in self.keyring:
            pubkey = self.keyring[kid]
        elif self.default_kid and self.default_kid in self.keyring:
            pubkey = self.keyring[self.default_kid]

        if not pubkey:
            raise ManifestVerificationError(f"unknown or missing signing key ID: '{kid}'", "UNKNOWN_SIGNING_KEY")

        payload_b64 = envelope.get("payload", "")
        sig_b64 = envelope.get("signature", "")

        try:
            sig_bytes = base64url_decode(sig_b64)
            payload_bytes = base64url_decode(payload_b64)
        except Exception as e:
            raise ManifestVerificationError(f"failed to decode base64url envelope: {e}", "DECODE_ERROR")

        if len(sig_bytes) != 64:
            raise ManifestVerificationError(f"invalid signature length: {len(sig_bytes)}", "INVALID_SIGNATURE")

        # Verify signature over canonical payload string bytes
        if not _ed25519_verify_pure(pubkey, payload_b64.encode("ascii"), sig_bytes):
            raise ManifestVerificationError("envelope cryptographic signature check failed", "INVALID_SIGNATURE")

        try:
            m_dict = json.loads(payload_bytes.decode("utf-8"))
        except Exception as e:
            raise ManifestVerificationError(f"failed to parse manifest JSON payload: {e}", "UNMARSHAL_ERROR")

        entries = []
        for f in m_dict.get("files", []):
            self.validate_relative_path(f["path"])
            entries.append(
                ManifestFileEntry(
                    path=f["path"],
                    sha256=f["sha256"].lower(),
                    size=int(f["size"]),
                    executable=f.get("executable"),
                )
            )

        return Manifest(
            manifest_version=m_dict.get("manifest_version", "1.0"),
            product_id=m_dict.get("product_id", ""),
            release_id=m_dict.get("release_id", ""),
            created_at=m_dict.get("created_at", ""),
            key_id=m_dict.get("key_id", kid),
            files=entries,
            platform=m_dict.get("platform"),
            architecture=m_dict.get("architecture"),
            metadata=m_dict.get("metadata", {}),
        )

    def verify_installed_files(self, manifest: Manifest, install_dir: str) -> None:
        """
        Verifies all protected files in the manifest against the actual filesystem install directory.
        Raises ManifestVerificationError if any file is missing, altered, or size-mismatched.
        """
        clean_install_root = os.path.realpath(install_dir)
        if not os.path.isdir(clean_install_root):
            raise ManifestVerificationError(f"install directory not found: '{install_dir}'", "DIR_NOT_FOUND")

        for entry in manifest.files:
            self.validate_relative_path(entry.path)

            full_path = os.path.realpath(os.path.join(clean_install_root, *entry.path.split("/")))
            if not full_path.startswith(clean_install_root + os.sep) and full_path != clean_install_root:
                raise ManifestVerificationError(f"path escapes directory jail: '{entry.path}'", "JAIL_ESCAPE")

            if not os.path.exists(full_path):
                raise ManifestVerificationError(f"missing protected file: '{entry.path}'", "FILE_MISSING")

            actual_size = os.path.getsize(full_path)
            if actual_size != entry.size:
                raise ManifestVerificationError(
                    f"file size mismatch for '{entry.path}': expected {entry.size} bytes, got {actual_size}",
                    "SIZE_MISMATCH",
                )

            # Streaming SHA-256 calculation (64 KB chunks)
            hasher = hashlib.sha256()
            with open(full_path, "rb") as f:
                while chunk := f.read(65536):
                    hasher.update(chunk)

            calculated_digest = hasher.hexdigest().lower()
            if calculated_digest != entry.sha256:
                raise ManifestVerificationError(
                    f"digest mismatch for '{entry.path}': expected {entry.sha256}, got {calculated_digest}",
                    "HASH_MISMATCH",
                )
