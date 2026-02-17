"""Linux backend using psutil and lm-sensors for hardware monitoring."""

import asyncio
import json
import shutil

import psutil

from app.hardware.base import HardwareBackend
from app.models.sensors import SensorReading, SensorType


class LmSensorsBackend(HardwareBackend):
    """Hardware backend for Linux using psutil and lm-sensors."""

    def __init__(self) -> None:
        self._has_sensors_cmd = False
        self._fan_ids: list[str] = []

    async def initialize(self) -> None:
        self._has_sensors_cmd = shutil.which("sensors") is not None

    async def shutdown(self) -> None:
        pass

    async def get_sensor_readings(self) -> list[SensorReading]:
        readings: list[SensorReading] = []
        self._fan_ids = []

        # CPU temperatures from psutil
        temps = psutil.sensors_temperatures()
        for chip_name, entries in temps.items():
            for i, entry in enumerate(entries):
                label = entry.label or f"{chip_name}_{i}"
                sensor_id = f"temp_{chip_name}_{i}"

                # Classify based on label/chip name
                if "gpu" in chip_name.lower() or "gpu" in label.lower():
                    s_type = SensorType.GPU_TEMP
                elif "nvme" in chip_name.lower() or "drive" in label.lower():
                    s_type = SensorType.HDD_TEMP
                else:
                    s_type = SensorType.CPU_TEMP

                readings.append(
                    SensorReading(
                        id=sensor_id,
                        name=label,
                        sensor_type=s_type,
                        value=entry.current,
                        max_value=entry.critical,
                        unit="°C",
                    )
                )

        # Fan speeds from psutil
        fans = psutil.sensors_fans()
        for chip_name, entries in fans.items():
            for i, entry in enumerate(entries):
                label = entry.label or f"{chip_name}_fan_{i}"
                fan_id = f"fan_{chip_name}_{i}"
                self._fan_ids.append(fan_id)
                readings.append(
                    SensorReading(
                        id=fan_id,
                        name=label,
                        sensor_type=SensorType.FAN_RPM,
                        value=entry.current,
                        unit="RPM",
                    )
                )

        # CPU load
        cpu_pct = psutil.cpu_percent(interval=None)
        readings.append(
            SensorReading(
                id="cpu_load_0",
                name="CPU Usage",
                sensor_type=SensorType.CPU_LOAD,
                value=cpu_pct,
                min_value=0,
                max_value=100,
                unit="%",
            )
        )

        # HDD temps via lm-sensors JSON output (if available)
        if self._has_sensors_cmd:
            await self._read_lm_sensors(readings)

        return readings

    async def _read_lm_sensors(self, readings: list[SensorReading]) -> None:
        """Parse lm-sensors JSON output for additional data."""
        try:
            proc = await asyncio.create_subprocess_exec(
                "sensors", "-j",
                stdout=asyncio.subprocess.PIPE,
                stderr=asyncio.subprocess.DEVNULL,
            )
            stdout, _ = await asyncio.wait_for(proc.communicate(), timeout=5.0)
            data = json.loads(stdout.decode())

            existing_ids = {r.id for r in readings}

            for chip, chip_data in data.items():
                if not isinstance(chip_data, dict):
                    continue
                for feature, feature_data in chip_data.items():
                    if not isinstance(feature_data, dict):
                        continue
                    for key, value in feature_data.items():
                        if "input" in key.lower() and isinstance(value, (int, float)):
                            sensor_id = f"lms_{chip}_{feature}"
                            if sensor_id not in existing_ids:
                                s_type = SensorType.CASE_TEMP
                                if "temp" in key.lower():
                                    s_type = SensorType.CASE_TEMP
                                readings.append(
                                    SensorReading(
                                        id=sensor_id,
                                        name=f"{chip} {feature}",
                                        sensor_type=s_type,
                                        value=value,
                                        unit="°C",
                                    )
                                )
        except (asyncio.TimeoutError, json.JSONDecodeError, OSError):
            pass

    async def set_fan_speed(self, fan_id: str, speed_percent: float) -> bool:
        # Linux fan control typically requires writing to /sys/class/hwmon
        # This needs root privileges and is hardware-specific
        # Placeholder for future implementation
        return False

    async def get_fan_ids(self) -> list[str]:
        return list(self._fan_ids)

    def get_backend_name(self) -> str:
        return "lm-sensors (Linux)"
