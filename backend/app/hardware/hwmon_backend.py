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
        else:
            logger.info("hwmon fan write: no writable pwm nodes found")

    @property
    def fan_write_supported(self) -> bool:
        return self._write_supported

    async def set_fan_speed(self, fan_id: str, speed_percent: float) -> bool:
        node = self._fans.get(fan_id)
        if node is None:
            return False

        speed_percent = max(0.0, min(100.0, speed_percent))
        pwm_value = round(speed_percent * 255 / 100)

        try:
            # Ensure manual mode before writing
            if node.enable_path:
                await _write_sysfs_async(node.enable_path, PWM_ENABLE_MANUAL)
            await _write_sysfs_async(node.pwm_path, str(pwm_value))
            return True
        except OSError as exc:
            logger.warning("Failed to set fan %s to %d%%: %s", fan_id, speed_percent, exc)
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

    def get_backend_name(self) -> str:
        if self._write_supported:
            return f"hwmon (Linux, {len(self._fans)} writable fans)"
        return "lm-sensors (Linux, read-only)"


def _read_sysfs(path: Path) -> str | None:
    """Read a single-line sysfs file, returning stripped content or None."""
    try:
        return path.read_text().strip()
    except OSError:
        return None


async def _write_sysfs_async(path: Path, value: str) -> None:
    """Write a value to a sysfs node asynchronously."""
    loop = asyncio.get_running_loop()
    await loop.run_in_executor(None, path.write_text, value)
