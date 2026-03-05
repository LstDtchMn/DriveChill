"""Abstract base class for drive hardware providers."""
from __future__ import annotations

from abc import ABC, abstractmethod

from app.models.drives import DriveRawData, DriveSelfTestRun, SelfTestType


class DriveProvider(ABC):
    """
    Abstract interface for a drive data source.

    Implementations must not raise on missing smartctl or platform
    incompatibility — instead return empty lists and set capabilities
    appropriately (degraded mode).
    """

    @abstractmethod
    async def discover(self) -> list[DriveRawData]:
        """Discover all available drives and return their raw data."""

    @abstractmethod
    async def refresh(self, device_path: str) -> DriveRawData | None:
        """
        Re-poll a single drive for its current health and temperature.
        Returns None if the drive is no longer accessible.
        """

    @abstractmethod
    async def start_self_test(self, device_path: str, test_type: SelfTestType) -> str | None:
        """
        Start a SMART self-test.
        Returns a provider run reference string, or None if unsupported.
        Raises ProviderError on execution failure.
        """

    @abstractmethod
    async def get_self_test_status(self, device_path: str) -> DriveSelfTestRun | None:
        """
        Poll the current self-test status for a drive.
        Returns None if no self-test is active or results are unavailable.
        """

    @abstractmethod
    async def abort_self_test(self, device_path: str) -> bool:
        """
        Abort any running self-test.
        Returns True if abort was issued, False if unsupported.
        """

    @abstractmethod
    async def is_available(self) -> bool:
        """Return True if this provider can supply useful data."""

    @property
    @abstractmethod
    def provider_name(self) -> str:
        """Human-readable provider name for diagnostics."""


class ProviderError(RuntimeError):
    """Normalized error from a drive provider."""

    def __init__(self, code: str, message: str) -> None:
        super().__init__(message)
        self.code = code  # e.g. "smartctl_unavailable", "permission_denied"
        self.message = message

    # Normalized error codes
    SMARTCTL_UNAVAILABLE = "smartctl_unavailable"
    PERMISSION_DENIED = "permission_denied"
    UNSUPPORTED_OPERATION = "unsupported_operation"
    DRIVE_NOT_FOUND = "drive_not_found"
    PROVIDER_TIMEOUT = "provider_timeout"
    PARSE_ERROR = "parse_error"
