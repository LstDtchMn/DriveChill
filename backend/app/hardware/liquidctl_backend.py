"""USB liquid-cooling controller backend via liquidctl.

Discovers and controls USB cooling devices (NZXT Kraken, Corsair Commander Pro,
Aquacomputer D5 Next, etc.) using the liquidctl CLI in JSON mode.

Devices appear as normal controllable fans and temperature sensors.
"""
from __future__ import annotations

import asyncio
import json
import logging
import shutil
from dataclasses import dataclass, field

from app.hardware.base import HardwareBackend
from app.models.sensors import SensorReading, SensorType

logger = logging.getLogger(__name__)


@dataclass
class LiquidctlDevice:
    """A discovered liquidctl device."""
    address: str         # USB bus:port or serial
    description: str     # e.g. "NZXT Kraken X63"
    vendor_id: str
    product_id: str
    fan_channels: list[str] = field(default_factory=list)  # e.g. ["fan1", "pump"]
    temp_channels: list[str] = field(default_factory=list)  # e.g. ["liquid"]


class LiquidctlBackend(HardwareBackend):
    """Hardware backend using liquidctl for USB cooling devices.

    Can be used standalone or composed with another backend (e.g. LmSensorsBackend)
    for a complete sensor set.
    """

    def __init__(self, liquidctl_path: str | None = None) -> None:
        self._bin = liquidctl_path or "liquidctl"
        self._available = False
        self._devices: list[LiquidctlDevice] = []
        self._fan_speeds: dict[str, float] = {}  # fan_id -> last set percent
        self._last_status: dict[str, dict[str, float]] = {}  # device_addr -> {key: value}

    async def initialize(self) -> None:
        self._available = shutil.which(self._bin) is not None
        if not self._available:
            logger.info("liquidctl not found; USB controller support disabled")
            return

        # Initialize all connected devices
        await self._run_cmd("initialize")
        # Discover devices
        await self._discover_devices()

    async def _discover_devices(self) -> None:
        """List connected devices and probe their capabilities."""
        self._devices.clear()
        output = await self._run_json("list")
        if not output:
            return

        for entry in output:
            desc = entry.get("description", "Unknown Device")
            addr = entry.get("address", entry.get("bus", "?"))
            vid = entry.get("vendor_id", "")
            pid = entry.get("product_id", "")

            device = LiquidctlDevice(
                address=str(addr),
                description=desc,
                vendor_id=str(vid),
                product_id=str(pid),
            )

            # Read initial status to discover channels
            status = await self._get_device_status(device)
            for key, value in status.items():
                key_lower = key.lower()
                if "fan" in key_lower or "pump" in key_lower:
                    if "speed" in key_lower or "rpm" in key_lower or "duty" in key_lower:
                        channel = _extract_channel_name(key)
                        if channel and channel not in device.fan_channels:
                            device.fan_channels.append(channel)
                elif "temp" in key_lower or "liquid" in key_lower:
                    channel = _extract_channel_name(key)
                    if channel and channel not in device.temp_channels:
                        device.temp_channels.append(channel)

            self._devices.append(device)
            logger.info("Discovered liquidctl device: %s (%s) fans=%s temps=%s",
                        desc, addr, device.fan_channels, device.temp_channels)

    async def _get_device_status(self, device: LiquidctlDevice) -> dict[str, float]:
        """Get status readings from a specific device, targeted by address."""
        output = await self._run_json("status", "--address", device.address)
        result: dict[str, float] = {}
        if not output:
            return result

        # liquidctl status JSON is a list of [key, value, unit] triples
        for item in output:
            if isinstance(item, list) and len(item) >= 2:
                key = str(item[0])
                try:
                    val = float(item[1])
                    result[key] = val
                except (ValueError, TypeError):
                    pass
            elif isinstance(item, dict):
                key = item.get("key", "")
                try:
                    val = float(item.get("value", 0))
                    result[key] = val
                except (ValueError, TypeError):
                    pass

        return result

    async def shutdown(self) -> None:
        if not self._available:
            return
        # Release control — no special action needed for most devices
        logger.info("Shutting down liquidctl backend")

    async def get_sensor_readings(self) -> list[SensorReading]:
        readings: list[SensorReading] = []
        if not self._available:
            return readings

        for device in self._devices:
            status = await self._get_device_status(device)
            self._last_status[device.address] = status
            dev_prefix = _make_id_prefix(device)

            for key, value in status.items():
                key_lower = key.lower()

                if ("temp" in key_lower or "liquid" in key_lower) and "°" not in key:
                    sensor_id = f"{dev_prefix}_temp_{_sanitize(key)}"
                    readings.append(SensorReading(
                        id=sensor_id,
                        name=f"{device.description} {key}",
                        sensor_type=SensorType.CASE_TEMP,
                        value=value,
                        unit="°C",
                    ))
                elif "rpm" in key_lower or ("speed" in key_lower and "fan" in key_lower):
                    sensor_id = f"{dev_prefix}_fan_{_sanitize(key)}"
                    readings.append(SensorReading(
                        id=sensor_id,
                        name=f"{device.description} {key}",
                        sensor_type=SensorType.FAN_RPM,
                        value=value,
                        unit="RPM",
                    ))
                elif "duty" in key_lower:
                    sensor_id = f"{dev_prefix}_duty_{_sanitize(key)}"
                    readings.append(SensorReading(
                        id=sensor_id,
                        name=f"{device.description} {key}",
                        sensor_type=SensorType.FAN_PERCENT,
                        value=value,
                        min_value=0,
                        max_value=100,
                        unit="%",
                    ))

        return readings

    async def set_fan_speed(self, fan_id: str, speed_percent: float) -> bool:
        """Set fan/pump speed on a liquidctl device."""
        if not self._available:
            return False

        speed_percent = max(0.0, min(100.0, speed_percent))

        # Parse fan_id to find device and channel
        for device in self._devices:
            dev_prefix = _make_id_prefix(device)
            for channel in device.fan_channels:
                expected_id = f"{dev_prefix}_{channel}"
                if fan_id == expected_id:
                    success = await self._set_device_fan(device, channel, int(speed_percent))
                    if success:
                        self._fan_speeds[fan_id] = speed_percent
                    return success

        return False

    async def _set_device_fan(self, device: LiquidctlDevice, channel: str,
                              duty: int) -> bool:
        """Set a fixed duty on a device channel, targeted by address."""
        try:
            await self._run_cmd("set", channel, "duty", str(duty),
                                "--address", device.address)
            return True
        except Exception as exc:
            logger.warning("Failed to set %s %s to %d%%: %s",
                           device.description, channel, duty, exc)
            return False

    async def get_fan_ids(self) -> list[str]:
        result: list[str] = []
        for device in self._devices:
            dev_prefix = _make_id_prefix(device)
            for channel in device.fan_channels:
                result.append(f"{dev_prefix}_{channel}")
        return result

    def get_backend_name(self) -> str:
        if not self._available:
            return "liquidctl (not available)"
        n = len(self._devices)
        return f"liquidctl ({n} device{'s' if n != 1 else ''})"

    # ── Subprocess helpers ────────────────────────────────────────────────

    async def _run_cmd(self, *args: str) -> str:
        """Run a liquidctl command, return stdout."""
        cmd = [self._bin] + [a for a in args if a]
        try:
            proc = await asyncio.create_subprocess_exec(
                *cmd,
                stdout=asyncio.subprocess.PIPE,
                stderr=asyncio.subprocess.PIPE,
            )
            stdout, stderr = await asyncio.wait_for(proc.communicate(), timeout=10.0)
            if proc.returncode != 0:
                logger.debug("liquidctl %s returned %d: %s",
                             args[0] if args else "?", proc.returncode,
                             stderr.decode(errors="replace").strip())
            return stdout.decode(errors="replace")
        except (asyncio.TimeoutError, OSError) as exc:
            logger.warning("liquidctl command failed: %s", exc)
            return ""

    async def _run_json(self, *args: str) -> list:
        """Run a liquidctl command with --json, parse output."""
        raw = await self._run_cmd(*args, "--json")
        if not raw.strip():
            return []
        try:
            data = json.loads(raw)
            return data if isinstance(data, list) else [data]
        except json.JSONDecodeError:
            logger.debug("Failed to parse liquidctl JSON: %s", raw[:200])
            return []


def _make_id_prefix(device: LiquidctlDevice) -> str:
    """Create a stable ID prefix that is unique even for identical device models.

    Incorporates the USB address (bus:port or serial) so two Kraken X63s on
    different ports get distinct sensor/fan IDs and won't collide.
    """
    return f"lctl_{_sanitize(device.description)}_{_sanitize(device.address)}"


def _sanitize(name: str) -> str:
    """Sanitize a string for use as a sensor/fan ID component."""
    return name.lower().replace(" ", "_").replace("-", "_").replace("/", "_").replace(".", "")


def _extract_channel_name(key: str) -> str | None:
    """Extract a channel name like 'fan1' or 'pump' from a status key."""
    key_lower = key.lower().strip()
    for prefix in ("fan", "pump"):
        if key_lower.startswith(prefix):
            # "Fan 1 speed" -> "fan1", "Pump duty" -> "pump"
            parts = key_lower.split()
            if len(parts) >= 2 and parts[1].isdigit():
                return f"{prefix}{parts[1]}"
            return prefix
    return None
