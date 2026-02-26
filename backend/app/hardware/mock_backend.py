import math
import random
import time

from app.hardware.base import HardwareBackend
from app.models.sensors import SensorReading, SensorType


class MockBackend(HardwareBackend):
    """Mock backend for development and testing. Generates realistic-looking sensor data."""

    def __init__(self) -> None:
        self._start_time = time.time()
        self._fan_speeds: dict[str, float] = {
            "fan_cpu": 45.0,
            "fan_gpu": 40.0,
            "fan_case_front": 35.0,
            "fan_case_rear": 35.0,
        }

    async def initialize(self) -> None:
        pass

    async def shutdown(self) -> None:
        pass

    def _wave(self, base: float, amplitude: float, period: float, offset: float = 0) -> float:
        """Generate a smooth oscillating value."""
        elapsed = time.time() - self._start_time
        noise = random.uniform(-1.5, 1.5)
        return base + amplitude * math.sin(2 * math.pi * elapsed / period + offset) + noise

    async def get_sensor_readings(self) -> list[SensorReading]:
        readings = [
            # CPU
            SensorReading(
                id="cpu_temp_0",
                name="CPU Package",
                sensor_type=SensorType.CPU_TEMP,
                value=round(self._wave(55, 15, 120), 1),
                min_value=30,
                max_value=100,
                unit="°C",
            ),
            SensorReading(
                id="cpu_temp_1",
                name="CPU Core 0",
                sensor_type=SensorType.CPU_TEMP,
                value=round(self._wave(53, 14, 115, 0.5), 1),
                min_value=30,
                max_value=100,
                unit="°C",
            ),
            SensorReading(
                id="cpu_temp_2",
                name="CPU Core 1",
                sensor_type=SensorType.CPU_TEMP,
                value=round(self._wave(54, 13, 110, 1.0), 1),
                min_value=30,
                max_value=100,
                unit="°C",
            ),
            SensorReading(
                id="cpu_load_0",
                name="CPU Usage",
                sensor_type=SensorType.CPU_LOAD,
                value=round(self._wave(35, 25, 90), 1),
                min_value=0,
                max_value=100,
                unit="%",
            ),
            # GPU
            SensorReading(
                id="gpu_temp_0",
                name="GPU Temperature",
                sensor_type=SensorType.GPU_TEMP,
                value=round(self._wave(48, 18, 150, 2.0), 1),
                min_value=25,
                max_value=95,
                unit="°C",
            ),
            SensorReading(
                id="gpu_load_0",
                name="GPU Usage",
                sensor_type=SensorType.GPU_LOAD,
                value=round(self._wave(30, 30, 100, 1.5), 1),
                min_value=0,
                max_value=100,
                unit="%",
            ),
            # HDDs
            SensorReading(
                id="hdd_temp_sda",
                name="SSD (C:)",
                sensor_type=SensorType.HDD_TEMP,
                value=round(self._wave(38, 5, 200), 1),
                min_value=20,
                max_value=70,
                unit="°C",
            ),
            SensorReading(
                id="hdd_temp_sdb",
                name="HDD (D:)",
                sensor_type=SensorType.HDD_TEMP,
                value=round(self._wave(36, 4, 180, 3.0), 1),
                min_value=20,
                max_value=70,
                unit="°C",
            ),
            # Case
            SensorReading(
                id="case_temp_0",
                name="Case Ambient",
                sensor_type=SensorType.CASE_TEMP,
                value=round(self._wave(32, 3, 300), 1),
                min_value=20,
                max_value=55,
                unit="°C",
            ),
        ]

        # Fan readings based on current set speeds
        for fan_id, speed_pct in self._fan_speeds.items():
            max_rpm = 1800 if "cpu" in fan_id else 1200
            rpm = round(max_rpm * (speed_pct / 100) + random.uniform(-20, 20))
            readings.append(
                SensorReading(
                    id=f"{fan_id}_rpm",
                    name=fan_id.replace("_", " ").title() + " RPM",
                    sensor_type=SensorType.FAN_RPM,
                    value=max(0, rpm),
                    min_value=0,
                    max_value=max_rpm,
                    unit="RPM",
                )
            )
            readings.append(
                SensorReading(
                    id=f"{fan_id}_pct",
                    name=fan_id.replace("_", " ").title() + " Speed",
                    sensor_type=SensorType.FAN_PERCENT,
                    value=round(speed_pct, 1),
                    min_value=0,
                    max_value=100,
                    unit="%",
                )
            )

        return readings

    async def set_fan_speed(self, fan_id: str, speed_percent: float) -> bool:
        if fan_id in self._fan_speeds:
            self._fan_speeds[fan_id] = max(0, min(100, speed_percent))
            return True
        return False

    async def release_fan_control(self) -> None:
        """Simulate returning fans to BIOS auto mode by resetting to defaults."""
        self._fan_speeds = {fan_id: 0.0 for fan_id in self._fan_speeds}

    async def get_fan_ids(self) -> list[str]:
        return list(self._fan_speeds.keys())

    def get_backend_name(self) -> str:
        return "Mock (Development)"
