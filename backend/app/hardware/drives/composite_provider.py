"""Composite drive provider: merges native and smartctl data."""
from __future__ import annotations

import logging

from app.hardware.drives.base import DriveProvider, ProviderError
from app.hardware.drives.native_provider import NativeDriveProvider
from app.hardware.drives.smartctl_provider import SmartctlProvider
from app.models.drives import DriveRawData, DriveSelfTestRun, SelfTestType

logger = logging.getLogger(__name__)


class CompositeDriveProvider(DriveProvider):
    """
    Merges native provider data with smartctl provider data.

    Resolution order:
    1. smartctl is authoritative for health, attributes, self-tests
    2. Native fills gaps for basic inventory if smartctl lacks a device
    3. Fields from smartctl always override native equivalents
    """

    def __init__(
        self,
        native: NativeDriveProvider,
        smartctl: SmartctlProvider,
        prefer_smartctl: bool = True,
    ) -> None:
        self._native = native
        self._smartctl = smartctl
        self._prefer_smartctl = prefer_smartctl

    @property
    def provider_name(self) -> str:
        return "composite"

    async def is_available(self) -> bool:
        native_ok = await self._native.is_available()
        smartctl_ok = await self._smartctl.is_available()
        return native_ok or smartctl_ok

    async def discover(self) -> list[DriveRawData]:
        smartctl_drives: list[DriveRawData] = []
        native_drives: list[DriveRawData] = []

        if self._prefer_smartctl:
            try:
                smartctl_drives = await self._smartctl.discover()
            except ProviderError as exc:
                logger.warning("smartctl discover failed: %s", exc.message)

        try:
            native_drives = await self._native.discover()
        except Exception as exc:
            logger.debug("native discover failed: %s", exc)

        # Build index of smartctl drives by device_path
        smartctl_index: dict[str, DriveRawData] = {
            d.device_path: d for d in smartctl_drives
        }

        # Merge: add native drives that smartctl didn't discover
        merged: list[DriveRawData] = list(smartctl_drives)
        for nat in native_drives:
            if nat.device_path not in smartctl_index:
                merged.append(nat)

        return merged

    async def refresh(self, device_path: str) -> DriveRawData | None:
        if self._prefer_smartctl and await self._smartctl.is_available():
            result = await self._smartctl.refresh(device_path)
            if result is not None:
                return result
        return await self._native.refresh(device_path)

    async def start_self_test(self, device_path: str, test_type: SelfTestType) -> str | None:
        return await self._smartctl.start_self_test(device_path, test_type)

    async def get_self_test_status(self, device_path: str) -> DriveSelfTestRun | None:
        return await self._smartctl.get_self_test_status(device_path)

    async def abort_self_test(self, device_path: str) -> bool:
        return await self._smartctl.abort_self_test(device_path)

    async def smartctl_available(self) -> bool:
        return await self._smartctl.is_available()

    async def native_available(self) -> bool:
        return await self._native.is_available()
