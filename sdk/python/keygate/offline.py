"""
Keygate Offline License Token Verification Engine.
Implements the language-independent Keygate Offline License Verification Protocol (v1).
"""

import base64
import hashlib
import json
import time
from typing import Any, Optional

from .errors import OfflineVerificationError
from .models import OfflineTokenClaims


# RFC 8032 Ed25519 pure-Python verification implementation
_B = 256
_Q = 2**255 - 19
_L = 2**252 + 27742317777372353535851937790883648493
_D = -121665 * pow(121666, _Q - 2, _Q) % _Q
_I = pow(2, (_Q - 1) // 4, _Q)

def _xrecover(y: int) -> int:
    xx = (y * y - 1) * pow(_D * y * y + 1, _Q - 2, _Q) % _Q
    x = pow(xx, (_Q + 3) // 8, _Q)
    if (x * x - xx) % _Q != 0:
        x = (x * _I) % _Q
    if x % 2 != 0:
        x = _Q - x
    return x

_BY = 4 * pow(5, _Q - 2, _Q) % _Q
_BX = _xrecover(_BY)
_BASE_POINT = [_BX % _Q, _BY % _Q]

def _edwards(P: list[int], Q: list[int]) -> list[int]:
    x1, y1 = P
    x2, y2 = Q
    x3 = (x1 * y2 + x2 * y1) * pow(1 + _D * x1 * x2 * y1 * y2, _Q - 2, _Q) % _Q
    y3 = (y1 * y2 + x1 * x2) * pow(1 - _D * x1 * x2 * y1 * y2, _Q - 2, _Q) % _Q
    return [x3, y3]

def _scalarmult(P: list[int], e: int) -> list[int]:
    if e == 0:
        return [0, 1]
    Q = _scalarmult(P, e // 2)
    Q = _edwards(Q, Q)
    if e & 1:
        Q = _edwards(Q, P)
    return Q

def _ed25519_verify_pure(pubkey: bytes, message: bytes, signature: bytes) -> bool:
    if len(pubkey) != 32 or len(signature) != 64:
        return False
    Rraw, Sraw = signature[:32], signature[32:]
    S = int.from_bytes(Sraw, "little")
    if S >= _L:
        return False
    Ay = int.from_bytes(pubkey, "little") & ((1 << 255) - 1)
    Ax = _xrecover(Ay)
    if (int.from_bytes(pubkey, "little") >> 255) != (Ax & 1):
        Ax = _Q - Ax
    A = [Ax, Ay]

    Ry = int.from_bytes(Rraw, "little") & ((1 << 255) - 1)
    Rx = _xrecover(Ry)
    if (int.from_bytes(Rraw, "little") >> 255) != (Rx & 1):
        Rx = _Q - Rx
    R = [Rx, Ry]

    k = int.from_bytes(hashlib.sha512(Rraw + pubkey + message).digest(), "little") % _L
    return _scalarmult(_BASE_POINT, S) == _edwards(R, _scalarmult(A, k))


def base64url_decode(s: str) -> bytes:
    """Decodes standard or unpadded base64url."""
    s = s.strip().replace("-", "+").replace("_", "/")
    pad = len(s) % 4
    if pad:
        s += "=" * (4 - pad)
    return base64.b64decode(s.encode("ascii"))


class OfflineTokenVerifier:
    """
    Validates Keygate offline license tokens locally without network calls.
    """

    def __init__(self, keyring: Optional[dict[str, bytes]] = None, single_pubkey: Optional[bytes] = None):
        self.keyring: dict[str, bytes] = dict(keyring or {})
        if single_pubkey:
            if len(single_pubkey) != 32:
                raise ValueError("public key must be exactly 32 bytes")
            # Auto-assign kid from key thumbprint
            kid = "key_" + hashlib.sha256(single_pubkey).hexdigest()[:16]
            self.keyring[kid] = single_pubkey
            self.default_kid = kid
        else:
            self.default_kid = next(iter(self.keyring.keys())) if self.keyring else None

    def add_key(self, kid: str, pubkey_bytes: bytes) -> None:
        """Adds a trusted public key to the verification keyring."""
        if len(pubkey_bytes) != 32:
            raise ValueError("public key must be exactly 32 bytes")
        self.keyring[kid] = pubkey_bytes
        if not self.default_kid:
            self.default_kid = kid

    def verify(
        self,
        raw_token: str,
        now: Optional[int] = None,
        clock_skew: int = 60,
        expected_product_id: Optional[str] = None,
        expected_identifier: Optional[str] = None,
        required_features: Optional[list[str]] = None,
        max_offline_duration: int = 0,
    ) -> OfflineTokenClaims:
        """
        Validates an offline token against the trusted keyring and constraints.
        Raises OfflineVerificationError if any check fails.
        """
        if not raw_token or not isinstance(raw_token, str):
            raise OfflineVerificationError("invalid token format: must be non-empty string", "INVALID_FORMAT")

        parts = raw_token.split(".")
        if len(parts) != 2 or not parts[0] or not parts[1]:
            raise OfflineVerificationError("invalid token format: expected exactly two dot-separated segments", "INVALID_FORMAT")

        b64_payload, b64_sig = parts[0], parts[1]

        try:
            sig_bytes = base64url_decode(b64_sig)
        except Exception as e:
            raise OfflineVerificationError(f"signature decode error: {e}", "SIGNATURE_DECODE_ERROR")

        if len(sig_bytes) != 64:
            raise OfflineVerificationError(f"invalid signature length: expected 64 bytes, got {len(sig_bytes)}", "INVALID_SIGNATURE")

        try:
            payload_bytes = base64url_decode(b64_payload)
            claims_dict = json.loads(payload_bytes.decode("utf-8"))
        except Exception as e:
            raise OfflineVerificationError(f"payload unmarshal error: {e}", "PAYLOAD_DECODE_ERROR")

        # 1. Version Check
        ver = claims_dict.get("ver")
        if ver is not None and ver != "1":
            raise OfflineVerificationError(f"unsupported token version: {ver}", "UNSUPPORTED_VERSION")

        # 2. Key Resolution
        kid = claims_dict.get("kid")
        pubkey = None
        if kid:
            if kid not in self.keyring:
                raise OfflineVerificationError(f"unknown signing key ID: {kid}", "UNKNOWN_SIGNING_KEY")
            pubkey = self.keyring[kid]
        else:
            # Fallback to single/default key for legacy tokens
            if self.default_kid and self.default_kid in self.keyring:
                pubkey = self.keyring[self.default_kid]
            elif len(self.keyring) == 1:
                pubkey = next(iter(self.keyring.values()))

        if not pubkey or len(pubkey) != 32:
            raise OfflineVerificationError("no valid public key available for verification", "KEY_NOT_INITIALISED")

        # 3. Cryptographic Signature Verification
        # Verified over the ASCII bytes of base64url(payload)
        if not _ed25519_verify_pure(pubkey, b64_payload.encode("ascii"), sig_bytes):
            raise OfflineVerificationError("cryptographic signature verification failed", "INVALID_SIGNATURE")

        ref_time = now if now is not None else int(time.time())

        # 4. Duration Checks
        iat = claims_dict.get("iat", 0)
        exp = claims_dict.get("exp", 0)

        if max_offline_duration > 0 and iat > 0 and exp > 0:
            if (exp - iat) > max_offline_duration:
                raise OfflineVerificationError("token offline duration exceeds permitted limit", "DURATION_EXCEEDED")

        if iat > (ref_time + clock_skew):
            raise OfflineVerificationError(f"token issued in the future (iat={iat}, now={ref_time})", "TOKEN_ISSUED_IN_FUTURE")

        if exp > 0 and (ref_time - clock_skew) > exp:
            raise OfflineVerificationError(f"token expired (exp={exp}, now={ref_time})", "TOKEN_EXPIRED")

        # 5. License Term & Grace Period Check
        vun = claims_dict.get("vun", 0)
        if vun > 0:
            grc_days = claims_dict.get("grc", 0)
            term_deadline = vun + (grc_days * 86400)
            if (ref_time - clock_skew) > term_deadline:
                raise OfflineVerificationError(f"license subscription term expired (vun={vun}, grace_days={grc_days})", "LICENSE_TERM_EXPIRED")

        # 6. Product ID Validation
        pid = claims_dict.get("pid", "")
        if expected_product_id and pid != expected_product_id:
            raise OfflineVerificationError(f"product ID mismatch: got '{pid}', expected '{expected_product_id}'", "PRODUCT_MISMATCH")

        # 7. Device Binding Validation
        if expected_identifier:
            did = claims_dict.get("did")
            if not did:
                raise OfflineVerificationError("token has no device identifier (did); an online verify is required", "DEVICE_MISMATCH")
            if did != expected_identifier:
                raise OfflineVerificationError(f"device identifier mismatch: got '{did}', expected '{expected_identifier}'", "DEVICE_MISMATCH")
            fpr = claims_dict.get("fpr")
            if fpr:
                expected_fpr = hashlib.sha256(f"{expected_identifier}:{pid}".encode("utf-8")).hexdigest()[:16]
                if fpr != expected_fpr:
                    raise OfflineVerificationError(f"device fingerprint mismatch: got '{fpr}', expected '{expected_fpr}'", "FINGERPRINT_MISMATCH")

        # 8. Entitlements Check
        features = claims_dict.get("ftr", {})
        if required_features:
            for req in required_features:
                if not features.get(req):
                    raise OfflineVerificationError(f"missing required entitlement: '{req}'", "MISSING_ENTITLEMENT")

        return OfflineTokenClaims(
            version=ver or "1",
            key_id=kid,
            license_id=claims_dict.get("lid", ""),
            product_id=pid,
            plan_id=claims_dict.get("pln"),
            status=claims_dict.get("sts", ""),
            identifier=claims_dict.get("did"),
            fingerprint=claims_dict.get("fpr"),
            features=features,
            issued_at=iat,
            expires_at=exp,
            valid_until=vun if vun > 0 else None,
            updates_until=claims_dict.get("upd"),
            grace_days=claims_dict.get("grc", 0),
            nonce=claims_dict.get("nce"),
        )
