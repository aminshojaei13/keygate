"""
Keygate Main Client Implementation.
Provides typed, HTTP-based access to the authoritative Keygate License and Release APIs.
Zero external dependencies (uses standard library urllib).
"""

import json
import socket
import urllib.error
import urllib.parse
import urllib.request
from typing import Any, Optional

from .device import DeviceIdentifier
from .errors import (
    APIError,
    ActivationLimitError,
    KeygateError,
    LicenseNotFoundError,
    NetworkError,
    TimeoutError,
)
from .manifest import ManifestVerifier
from .models import (
    ActivationResult,
    EntitlementResult,
    Manifest,
    OfflineTokenClaims,
    VerificationKey,
    VerifyResult,
)
from .offline import OfflineTokenVerifier


class KeygateClient:
    """
    Authoritative client for the Keygate Licensing and Application Integrity Ecosystem.
    """

    def __init__(
        self,
        base_url: str,
        timeout: float = 10.0,
        license_public_key_hex: Optional[str] = None,
        release_public_key_hex: Optional[str] = None,
        device_id: Optional[str] = None,
    ):
        self.base_url = base_url.rstrip("/")
        self.default_timeout = timeout
        self.device_id = device_id or DeviceIdentifier.get_or_create_device_id()

        # Offline token verifier keyring
        self.token_verifier = OfflineTokenVerifier()
        if license_public_key_hex:
            pub_bytes = bytes.fromhex(license_public_key_hex.strip())
            self.token_verifier.add_key("bootstrap", pub_bytes)
            self.token_verifier.default_kid = "bootstrap"

        # Release manifest verifier keyring
        self.manifest_verifier = ManifestVerifier()
        if release_public_key_hex:
            rel_bytes = bytes.fromhex(release_public_key_hex.strip())
            self.manifest_verifier.add_key("bootstrap", rel_bytes)
            self.manifest_verifier.default_kid = "bootstrap"

    def _request(
        self,
        method: str,
        path: str,
        payload: Optional[dict[str, Any]] = None,
        headers: Optional[dict[str, str]] = None,
        timeout: Optional[float] = None,
    ) -> dict[str, Any]:
        """
        Executes an HTTP request against the Keygate API and parses the envelope.
        """
        url = f"{self.base_url}/api/v1{path}"
        req_headers = {
            "Accept": "application/json",
            "User-Agent": "Keygate-Python-SDK/1.2.0",
        }
        if headers:
            req_headers.update(headers)

        data = None
        if payload is not None:
            data = json.dumps(payload).encode("utf-8")
            req_headers["Content-Type"] = "application/json"

        req = urllib.request.Request(url, data=data, headers=req_headers, method=method)
        req_timeout = timeout if timeout is not None else self.default_timeout

        try:
            with urllib.request.urlopen(req, timeout=req_timeout) as resp:
                resp_bytes = resp.read()
                if not resp_bytes:
                    return {}
                parsed = json.loads(resp_bytes.decode("utf-8"))
                return parsed.get("data", {})
        except urllib.error.HTTPError as e:
            # Parse error envelope: {"success": false, "error": {"code": "...", "message": "...", "details": {...}}}
            err_body = e.read()
            err_code = "HTTP_ERROR"
            err_msg = f"HTTP {e.code}: {e.reason}"
            err_details = {}
            if err_body:
                try:
                    envelope = json.loads(err_body.decode("utf-8"))
                    err_obj = envelope.get("error", {})
                    err_code = err_obj.get("code", err_code)
                    err_msg = err_obj.get("message", err_msg)
                    err_details = err_obj.get("details", {})
                except Exception:
                    pass

            if err_code == "ACTIVATION_LIMIT":
                raise ActivationLimitError(message=err_msg, details=err_details)
            elif err_code == "LICENSE_NOT_FOUND":
                raise LicenseNotFoundError(message=err_msg)
            else:
                raise APIError(message=err_msg, code=err_code, http_status=e.code, details=err_details)
        except (socket.timeout, TimeoutError) as e:
            raise TimeoutError(f"request to {path} timed out after {req_timeout}s") from e
        except urllib.error.URLError as e:
            if isinstance(e.reason, socket.timeout):
                raise TimeoutError(f"request to {path} timed out after {req_timeout}s") from e
            raise NetworkError(f"failed to connect to Keygate server at {self.base_url}: {e.reason}", cause=e) from e
        except Exception as e:
            raise NetworkError(f"unexpected network or transport error: {e}", cause=e) from e

    # ─── Public License Operations ───

    def get_license_public_keys(self, timeout: Optional[float] = None) -> list[VerificationKey]:
        """
        Retrieves active and rotated Ed25519 public verification keys from /license/pubkey.
        Updates the internal OfflineTokenVerifier keyring.
        """
        data = self._request("GET", "/license/pubkey", timeout=timeout)
        keys_list = []

        # Multi-key keyring response
        if "keys" in data and isinstance(data["keys"], list):
            for k in data["keys"]:
                vk = VerificationKey(
                    kid=k.get("kid", ""),
                    algorithm=k.get("algorithm", "ed25519"),
                    public_key=k.get("public_key", ""),
                    format=k.get("format", "hex"),
                    status=k.get("status", "active"),
                )
                keys_list.append(vk)
                try:
                    self.token_verifier.add_key(vk.kid, bytes.fromhex(vk.public_key))
                except Exception:
                    pass
        elif "public_key" in data:
            # Single key response
            kid = data.get("kid", "primary")
            vk = VerificationKey(
                kid=kid,
                algorithm=data.get("algorithm", "ed25519"),
                public_key=data.get("public_key", ""),
                format=data.get("format", "hex"),
                status="active",
            )
            keys_list.append(vk)
            try:
                self.token_verifier.add_key(vk.kid, bytes.fromhex(vk.public_key))
            except Exception:
                pass

        return keys_list

    def activate(
        self,
        license_key: str,
        identifier: Optional[str] = None,
        identifier_type: str = "device",
        label: Optional[str] = None,
        idempotency_key: Optional[str] = None,
        timeout: Optional[float] = None,
    ) -> ActivationResult:
        """
        Activates a license key for this client.
        The server authoritatively enforces activation quotas and returns a signed offline token.
        """
        device_id = identifier or self.device_id
        payload = {
            "license_key": license_key.strip(),
            "identifier": device_id,
            "identifier_type": identifier_type,
        }
        if label:
            payload["label"] = label

        headers = {}
        if idempotency_key:
            headers["Idempotency-Key"] = idempotency_key

        data = self._request("POST", "/license/activate", payload=payload, headers=headers, timeout=timeout)

        token = data.get("token", "")
        offline_claims = None
        if token and self.token_verifier.keyring:
            try:
                offline_claims = self.token_verifier.verify(token, expected_identifier=device_id)
            except Exception:
                pass

        return ActivationResult(
            license_id=data.get("license_id", ""),
            token=token,
            status=data.get("status", "active"),
            product_id=data.get("product_id", ""),
            plan_id=data.get("plan_id"),
            valid_until=data.get("valid_until"),
            features=data.get("features", {}),
            already_activated=data.get("already_activated", False),
            offline_claims=offline_claims,
        )

    def verify_online(
        self,
        license_key: str,
        identifier: Optional[str] = None,
        timeout: Optional[float] = None,
    ) -> VerifyResult:
        """
        Performs an authoritative online license status check.
        Returns the current license state and a fresh signed offline verification token.
        """
        device_id = identifier or self.device_id
        payload = {
            "license_key": license_key.strip(),
            "identifier": device_id,
        }
        data = self._request("POST", "/license/verify", payload=payload, timeout=timeout)
        token = data.get("token", "")

        offline_claims = None
        if token and self.token_verifier.keyring:
            try:
                offline_claims = self.token_verifier.verify(token, expected_identifier=device_id)
            except Exception:
                pass

        return VerifyResult(
            license_id=data.get("license_id", ""),
            status=data.get("status", "active"),
            token=token,
            features=data.get("features", {}),
            valid_until=data.get("valid_until"),
            offline_claims=offline_claims,
        )

    def deactivate(
        self,
        license_key: str,
        identifier: Optional[str] = None,
        timeout: Optional[float] = None,
    ) -> None:
        """
        Deactivates an active device slot for this license key.
        """
        device_id = identifier or self.device_id
        payload = {
            "license_key": license_key.strip(),
            "identifier": device_id,
        }
        self._request("POST", "/license/deactivate", payload=payload, timeout=timeout)

    def get_entitlements(self, license_key: str, timeout: Optional[float] = None) -> EntitlementResult:
        """
        Retrieves the active feature flags and entitlements for a license.
        """
        payload = {"license_key": license_key.strip()}
        data = self._request("POST", "/license/entitlements", payload=payload, timeout=timeout)
        return EntitlementResult(
            license_id=data.get("license_id", ""),
            status=data.get("status", "active"),
            features=data.get("features", {}),
        )

    def record_usage(
        self,
        license_key: str,
        feature: str,
        count: int = 1,
        idempotency_key: Optional[str] = None,
        timeout: Optional[float] = None,
    ) -> dict[str, Any]:
        """
        Records metered usage against a license quota.
        """
        payload = {
            "license_key": license_key.strip(),
            "feature": feature,
            "count": count,
        }
        headers = {}
        if idempotency_key:
            headers["Idempotency-Key"] = idempotency_key
        return self._request("POST", "/license/usage", payload=payload, headers=headers, timeout=timeout)

    # ─── Release & Artifact Operations ───

    def get_release_signing_keys(self, product_slug: str, timeout: Optional[float] = None) -> list[VerificationKey]:
        """
        Discovers the public artifact and manifest verification keys for a product.
        Updates the internal ManifestVerifier keyring.
        """
        data = self._request("GET", f"/releases/{product_slug}/signing-key", timeout=timeout)
        keys_list = []
        for k in data.get("keys", []):
            vk = VerificationKey(
                kid=k.get("kid", ""),
                algorithm=k.get("algorithm", "ed25519"),
                public_key=k.get("public_key_b64", ""),
                format="base64",
                status=k.get("status", "active"),
            )
            keys_list.append(vk)
            try:
                import base64
                self.manifest_verifier.add_key(vk.kid, base64.b64decode(vk.public_key))
            except Exception:
                pass
        return keys_list

    # ─── Local Offline & Integrity Operations ───

    def verify_offline_token(
        self,
        token: str,
        now: Optional[int] = None,
        clock_skew: int = 60,
        expected_product_id: Optional[str] = None,
        expected_identifier: Optional[str] = None,
        required_features: Optional[list[str]] = None,
    ) -> OfflineTokenClaims:
        """
        Verifies a signed offline license token locally without network calls.
        """
        device_id = expected_identifier or self.device_id
        return self.token_verifier.verify(
            raw_token=token,
            now=now,
            clock_skew=clock_skew,
            expected_product_id=expected_product_id,
            expected_identifier=device_id,
            required_features=required_features,
        )

    def verify_integrity_manifest(self, envelope: dict, install_dir: str) -> Manifest:
        """
        Cryptographically verifies a signed manifest envelope and validates all installed files on disk.
        """
        manifest = self.manifest_verifier.verify_envelope(envelope)
        self.manifest_verifier.verify_installed_files(manifest, install_dir)
        return manifest
