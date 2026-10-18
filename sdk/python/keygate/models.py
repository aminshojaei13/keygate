"""
Keygate Data Models and Typed Structures.
"""

from dataclasses import dataclass, field
from typing import Any, Optional


@dataclass
class VerificationKey:
    """Public key information returned by /license/pubkey or release key discovery."""
    kid: str
    algorithm: str
    public_key: str
    format: str = "hex"
    status: str = "active"


@dataclass
class OfflineTokenClaims:
    """Decoded and verified offline license token claims."""
    license_id: str
    product_id: str
    status: str
    issued_at: int
    expires_at: int
    plan_id: Optional[str] = None
    identifier: Optional[str] = None
    fingerprint: Optional[str] = None
    features: dict[str, Any] = field(default_factory=dict)
    valid_until: Optional[int] = None
    updates_until: Optional[int] = None
    grace_days: int = 0
    nonce: Optional[str] = None
    version: str = "1"
    key_id: Optional[str] = None


@dataclass
class ActivationResult:
    """Response returned upon successful license activation."""
    license_id: str
    token: str
    status: str
    product_id: str
    plan_id: Optional[str] = None
    valid_until: Optional[str] = None
    features: dict[str, Any] = field(default_factory=dict)
    already_activated: bool = False
    offline_claims: Optional[OfflineTokenClaims] = None


@dataclass
class VerifyResult:
    """Response returned upon successful online license verification."""
    license_id: str
    status: str
    token: str
    features: dict[str, Any] = field(default_factory=dict)
    valid_until: Optional[str] = None
    offline_claims: Optional[OfflineTokenClaims] = None


@dataclass
class EntitlementResult:
    """Response returned by /license/entitlements."""
    license_id: str
    status: str
    features: dict[str, Any] = field(default_factory=dict)


@dataclass
class ManifestFileEntry:
    """Protected file entry in an application integrity manifest."""
    path: str
    sha256: str
    size: int
    executable: Optional[bool] = None


@dataclass
class Manifest:
    """Application release integrity manifest."""
    manifest_version: str
    product_id: str
    release_id: str
    created_at: str
    key_id: str
    files: list[ManifestFileEntry]
    platform: Optional[str] = None
    architecture: Optional[str] = None
    metadata: dict[str, str] = field(default_factory=dict)
