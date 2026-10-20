"""
Keygate Python Reference SDK.
Authoritative client integration library for Keygate Licensing and Application Integrity.
"""

from .client import KeygateClient
from .device import DeviceIdentifier
from .errors import (
    APIError,
    ActivationLimitError,
    KeygateError,
    LicenseNotFoundError,
    ManifestVerificationError,
    NetworkError,
    OfflineVerificationError,
    TimeoutError,
)
from .manifest import ManifestVerifier
from .models import (
    ActivationResult,
    EntitlementResult,
    Manifest,
    ManifestFileEntry,
    OfflineTokenClaims,
    VerificationKey,
    VerifyResult,
)
from .offline import OfflineTokenVerifier

__version__ = "1.2.0"
__all__ = [
    "KeygateClient",
    "DeviceIdentifier",
    "OfflineTokenVerifier",
    "ManifestVerifier",
    "KeygateError",
    "APIError",
    "ActivationLimitError",
    "LicenseNotFoundError",
    "NetworkError",
    "TimeoutError",
    "OfflineVerificationError",
    "ManifestVerificationError",
    "ActivationResult",
    "VerifyResult",
    "EntitlementResult",
    "OfflineTokenClaims",
    "VerificationKey",
    "Manifest",
    "ManifestFileEntry",
]
