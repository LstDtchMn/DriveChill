"""Linux hwmon fan-write backend.

Extends the lm-sensors read path with sysfs pwm write support.
Requires write access to /sys/class/hwmon/*/pwm* nodes (typically root).
"""
from __future__ import annotations

import asyncio
import logging
import os
from dataclasses import dataclass, field
from pathlib import Path

from app.hardware.lm_sensors_backend import LmSensorsBackend

logger = logging.getLogger(__name__)

HWMON_ROOT = Path("/sys/class/hwmon")

# pwm_enable values
PWM_ENABLE_MANUAL = "1"
PWM_ENABLE_AUTO = "2"  # most common auto value; some chips use 0 or 5


WRITE_TIMEOUT_SECONDS = 2.0
MAX_RETRY_ON_EIO = 1


@dataclass
class HwmonFanNode:
    """A discovered writable PWM fan node."""
    hwmon_path: Path          # e.g. /sys/class/hwmon/hwmon3
    chip_name: str            # e.g. "nct6775"
    index: int                # 1-based pwm index
    pwm_path: Path            # e.g. /sys/class/hwmon/hwmon3/pwm1
    enable_path: Path | None  # e.g. /sys/class/hwmon/hwmon3/pwm1_enable
    fan_id: str               # e.g. "hwmon_nct6775_pwm1"
    original_enable: str | None = None  # saved for restore on release
    status: str = "ok"        # ok | degraded | error


class HwmonBackend(LmSensorsBackend):
    """Linux backend with hwmon PWM fan write support.

    Inherits sensor reading from LmSensorsBackend, adds:
    - Discovery of writable pwm* nodes under /sys/class/hwmon/
    - set_fan_speed() via sysfs writes
    - release_fan_control() restores pwm_enable to original value
    """

    def __init__(self, hwmon_root: Path | None = None) -> None:
        super().__init__()
        self._hwmon_root = hwmon_root or HWMON_ROOT
        self._fans: dict[str, HwmonFanNode] = {}
        self._write_supported = False

    async def initialize(self) -> None:
        await super().initialize()
        await self._discover_pwm_fans()

    async def _discover_pwm_fans(self) -> None:
        """Scan /sys/class/hwmon for writable pwm nodes."""
        self._fans.clear()
        root = self._hwmon_root

        if not root.is_dir():
            logger.info("hwmon root %s not found; fan writes disabled", root)
            return

        for hwmon_dir in sorted(root.iterdir()):
            if not hwmon_dir.is_dir():
                continue

            chip_name = _read_sysfs(hwmon_dir / "name") or hwmon_dir.name

            # Look for pwm1, pwm2, ... (1-based index)
            for idx in range(1, 20):
                pwm_path = hwmon_dir / f"pwm{idx}"
                if not pwm_path.exists():
                    break

                enable_path = hwmon_dir / f"pwm{idx}_enable"
                enable_path = enable_path if enable_path.exists() else None

                # Check writability
                if not os.access(pwm_path, os.W_OK):
                    logger.debug("pwm node %s not writable, skipping", pwm_path)
                    continue

                fan_id = f"hwmon_{chip_name}_pwm{idx}"

                # Save original enable state for restore
                original_enable = None
                if enable_path:
                    original_enable = _read_sysfs(enable_path)

                node = HwmonFanNode(
                    hwmon_path=hwmon_dir,
                    chip_name=chip_name,
                    index=idx,
                    pwm_path=pwm_path,
                    enable_path=enable_path,
                    fan_id=fan_id,
                    original_enable=original_enable,
                )
                self._fans[fan_id] = node
                logger.info("Discovered writable fan: %s (%s)", fan_id, pwm_path)

        self._write_supported = len(self._fans) > 0
        if self._write_supported:
            logger.info("hwmon fan write: %d controllable fans found", len(self._fans))
            # Permission pre-check: verify we can actually write
            await self._verify_write_permissions()
        else:
            logger.info("hwmon fan write: no writable pwm nodes found")

    async def _verify_write_permissions(self) -> None:
        """Test-write current value back to each fan to verify permissions early."""
        for fan_id, node in list(self._fans.items()):
            try:
                current = _read_sysfs(node.pwm_path)
                if current is not None:
                    await _write_sysfs_async(node.pwm_path, current)
                    logger.debug("Permission check passed for %s", fan_id)
            except PermissionError:
                node.status = "error"
                logger.error(
                    "Fan %s: permission denied on %s during startup check. "
                    "Fan will be unavailable for control. "
                    "Fix: sudo chmod g+w %s && sudo chgrp $(id -gn) %s",
                    fan_id, node.pwm_path, node.pwm_path, node.pwm_path,
                )
            except OSError as exc:
                node.status = "degraded"
                logger.warning("Fan %s: startup write check failed: %s", fan_id, exc)

    @property
    def fan_write_supported(self) -> bool:
        return self._write_supported

    async def set_fan_speed(self, fan_id: str, speed_percent: float) -> bool:
        node = self._fans.get(fan_id)
        if node is None:
            return False
        if node.status == "error":
            return False

        speed_percent = max(0.0, min(100.0, speed_percent))
        pwm_value = round(speed_percent * 255 / 100)

        for attempt in range(1 + MAX_RETRY_ON_EIO):
            try:
                # Ensure manual mode before writing
                if node.enable_path:
                    await _write_sysfs_async(node.enable_path, PWM_ENABLE_MANUAL)
                await _write_sysfs_async(node.pwm_path, str(pwm_value))
                # Successful write — restore status if previously degraded
                if node.status == "degraded":
                    node.status = "ok"
                    logger.info("Fan %s recovered from degraded state", fan_id)
                return True
            except OSError as exc:
                is_eio = getattr(exc, "errno", None) == 5  # EIO
                if is_eio and attempt < MAX_RETRY_ON_EIO:
                    logger.debug("Fan %s got EIO, retrying (%d/%d)", fan_id, attempt + 1, MAX_RETRY_ON_EIO)
                    await asyncio.sleep(0.1)
                    continue
                # Mark degraded on permission errors or repeated failures
                if getattr(exc, "errno", None) == 13:  # EACCES
                    node.status = "error"
                    logger.error(
                        "Fan %s: permission denied writing to %s. "
                        "Ensure the DriveChill process has write access to sysfs pwm nodes. "
                        "Try: sudo chmod g+w %s && sudo chgrp $(id -gn) %s",
                        fan_id, node.pwm_path, node.pwm_path, node.pwm_path,
                    )
                else:
                    node.status = "degraded"
                    logger.warning("Failed to set fan %s to %d%%: %s", fan_id, speed_percent, exc)
                return False
        return False

    async def release_fan_control(self) -> None:
        """Restore all fans to their original enable mode (typically auto)."""
        for node in self._fans.values():
            if node.enable_path and node.original_enable:
                try:
                    await _write_sysfs_async(node.enable_path, node.original_enable)
                    logger.info("Released fan %s to enable=%s", node.fan_id, node.original_enable)
                except OSError as exc:
                    logger.warning("Failed to release fan %s: %s", node.fan_id, exc)

    def release_fan_control_sync(self) -> None:
        """Synchronous release for atexit/signal handlers."""
        for node in self._fans.values():
            if node.enable_path and node.original_enable:
                try:
                    node.enable_path.write_text(node.original_enable)
                except OSError:
                    pass

    async def get_fan_ids(self) -> list[str]:
        # Combine parent's discovered fan IDs with hwmon-writable IDs
        parent_ids = await super().get_fan_ids()
        hwmon_ids = list(self._fans.keys())
        # Deduplicate while preserving order
        seen = set()
        result = []
        for fid in hwmon_ids + parent_ids:
            if fid not in seen:
                seen.add(fid)
                result.append(fid)
        return result

    async def shutdown(self) -> None:
        await self.release_fan_control()
        await super().shutdown()

    def get_fan_status(self) -> dict[str, dict]:
        """Return per-fan health status for frontend display.

        Returns:
            Dict keyed by fan_id with {status, chip_name, pwm_path} per fan.
        """
        result: dict[str, dict] = {}
        for fan_id, node in self._fans.items():
            result[fan_id] = {
                "status": node.status,
                "chip_name": node.chip_name,
                "pwm_path": str(node.pwm_path),
            }
        return result

    def get_backend_name(self) -> str:
        ok_count = sum(1 for n in self._fans.values() if n.status == "ok")
        degraded_count = sum(1 for n in self._fans.values() if n.status == "degraded")
        error_count = sum(1 for n in self._fans.values() if n.status == "error")
        if self._write_supported:
            parts = [f"{ok_count} ok"]
            if degraded_count:
                parts.append(f"{degraded_count} degraded")
            if error_count:
                parts.append(f"{error_count} error")
            return f"hwmon (Linux, {', '.join(parts)})"
        return "lm-sensors (Linux, read-only)"


def _read_sysfs(path: Path) -> str | None:
    """Read a single-line sysfs file, returning stripped content or None."""
    try:
        return path.read_text().strip()
    except OSError:
        return None


async def _write_sysfs_async(path: Path, value: str) -> None:
    """Write a value to a sysfs node asynchronously with timeout."""
    loop = asyncio.get_running_loop()
    try:
        await asyncio.wait_for(
            loop.run_in_executor(None, path.write_text, value),
            timeout=WRITE_TIMEOUT_SECONDS,
        )
    except asyncio.TimeoutError:
        raise OSError(f"Timeout writing to {path} after {WRITE_TIMEOUT_SECONDS}s")
