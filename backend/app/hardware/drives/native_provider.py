"""Native OS drive data provider (platform-specific, best-effort)."""
from __future__ import annotations

import asyncio
import logging
import platform

from app.hardware.drives.base import DriveProvider
from app.models.drives import DriveRawData, DriveSelfTestRun, SelfTestType

logger = logging.getLogger(__name__)


class NativeDriveProvider(DriveProvider):
    """
    Best-effort native drive data provider.

    On Windows the WMI / Win32_DiskDrive path is not available without
    pywin32/WMI being installed, so this provider returns an empty list.

    On Linux, /sys/class/block is read for basic drive inventory.
    Temperature is not available through native kernel sysfs without
    vendor-specific hwmon entries, so temperature is left as None here;
    the smartctl provider fills it.

    This provider is intentionally minimal — it is the cheap fallback for
    basic inventory when smartctl is unavailable or blocked.
    """

    @property
    def provider_name(self) -> str:
        return "native"

    async def is_available(self) -> bool:
        return platform.system() in ("Linux", "Windows")

    async def discover(self) -> list[DriveRawData]:
        sys = platform.system()
        if sys == "Linux":
            return await self._discover_linux()
        # Windows native enumeration requires WMI/pywin32; fall back to empty
        return []

    async def _discover_linux(self) -> list[DriveRawData]:
        """Offload blocking sysfs reads to a thread pool."""
        return await asyncio.to_thread(self._discover_linux_sync)

    def _discover_linux_sync(self) -> list[DriveRawData]:
        """Read /sys/class/block for block device basics (synchronous)."""
        import hashlib
        from pathlib import Path
        from app.models.drives import BusType, MediaType, DriveCapabilitySet

        drives: list[DriveRawData] = []
        block_dir = Path("/sys/class/block")
        if not block_dir.exists():
            return drives

        for dev_link in sorted(block_dir.iterdir()):
            name = dev_link.name
            # Only top-level devices (sda, hda, nvme0n1) not partitions
            if not (
                (name.startswith("sd") and name[2:].isalpha())
                or (name.startswith("hd") and name[2:].isalpha())
                or (name.startswith("nvme") and "n" in name and "p" not in name)
            ):
                continue

            device_path = f"/dev/{name}"
            resolved = dev_link.resolve()
            model = ""
            vendor = ""
            capacity_bytes = 0

            try:
                model_file = resolved / "device" / "model"
                if model_file.exists():
                    model = model_file.read_text(errors="replace").strip()
            except OSError:
                pass

            try:
                vendor_file = resolved / "device" / "vendor"
                if vendor_file.exists():
                    vendor = vendor_file.read_text(errors="replace").strip()
            except OSError:
                pass

            try:
                size_file = resolved / "size"
                if size_file.exists():
                    sectors = int(size_file.read_text().strip())
                    capacity_bytes = sectors * 512
            except (OSError, ValueError):
                pass

            bus_type = BusType.NVME if name.startswith("nvme") else BusType.SATA
            media_type = MediaType.NVME if bus_type == BusType.NVME else MediaType.UNKNOWN

            full_name = f"{vendor} {model}".strip() or name
            drive_id = hashlib.sha256(
                f"native|{name}|{model}".encode()
            ).hexdigest()[:24]

            drives.append(
                DriveRawData(
                    id=drive_id,
                    name=full_name,
                    model=model,
                    serial="",
                    device_path=device_path,
                    bus_type=bus_type,
                    media_type=media_type,
                    capacity_bytes=capacity_bytes,
                    firmware_version="",
                    capabilities=DriveCapabilitySet(),
                )
            )

        return drives

    async def refresh(self, device_path: str) -> DriveRawData | None:
        # Native provider has no cheap per-device refresh path
        return None

    async def start_self_test(self, device_path: str, test_type: SelfTestType) -> str | None:
        # Native provider cannot start self-tests
        return None

    async def get_self_test_status(self, device_path: str) -> DriveSelfTestRun | None:
        return None

    async def abort_self_test(self, device_path: str) -> bool:
        return False
