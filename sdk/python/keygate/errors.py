"""
Keygate SDK Exceptions and Error Models.
"""

from typing import Any, Optional


class KeygateError(Exception):
    """Base exception for all Keygate SDK operations."""

    def __init__(self, message: str, code: str = "SDK_ERROR", http_status: Optional[int] = None, details: Optional[dict[str, Any]] = None):
        super().__init__(f"[{code}] {message}" if code else message)
        self.message = message
        self.code = code
        self.http_status = http_status
        self.details = details or {}


class APIError(KeygateError):
    """Raised when the Keygate server returns an API error response."""
    pass


class NetworkError(KeygateError):
    """Raised on connection failure, DNS resolution failure, or transport timeout."""

    def __init__(self, message: str, cause: Optional[Exception] = None):
        super().__init__(message=message, code="NETWORK_ERROR")
        self.cause = cause


class TimeoutError(KeygateError):
    """Raised when an API request exceeds the configured client timeout."""

    def __init__(self, message: str = "Request timed out"):
        super().__init__(message=message, code="TIMEOUT")


class ActivationLimitError(APIError):
    """Raised when a license has reached its maximum permitted activation count."""

    def __init__(self, message: str, details: Optional[dict[str, Any]] = None):
        super().__init__(message=message, code="ACTIVATION_LIMIT", http_status=409, details=details)
        self.current = details.get("current", 0) if details else 0
        self.max = details.get("max", 0) if details else 0


class LicenseNotFoundError(APIError):
    """Raised when the license key does not exist or is invalid."""

    def __init__(self, message: str = "License not found"):
        super().__init__(message=message, code="LICENSE_NOT_FOUND", http_status=404)


class OfflineVerificationError(KeygateError):
    """Raised when offline entitlement token validation fails."""
    pass


class ManifestVerificationError(KeygateError):
    """Raised when application release integrity manifest verification fails."""
    pass
