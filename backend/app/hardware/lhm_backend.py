"""LibreHardwareMonitor backend for Windows.

Communicates with LibreHardwareMonitor via its built-in HTTP server.
Requires LHM to be running with the web server enabled (default port 8086).
"""

import httpx

from app.hardware.base import HardwareBackend
from app.models.sensors import SensorReading, SensorType

LHM_URL = "http://localhost:8086"

# Map LHM sensor type strings to our SensorType enum
LHM_TYPE_MAP: dict[str, tuple[SensorType, str]] = {
    "Temperature": (SensorType.CPU_TEMP, "°C"),
    "Fan": (SensorType.FAN_RPM, "RPM"),
    "Control": (SensorType.FAN_PERCENT, "%"),
    "Load": (SensorType.CPU_LOAD, "%"),
}


def _classify_sensor(hardware_type: str, sensor_type_str: str) -> tuple[SensorType, str]:
    """Classify a sensor based on its hardware and sensor type strings."""
    if sensor_type_str == "Temperature":
        if "gpu" in hardware_type.lower():
            return SensorType.GPU_TEMP, "°C"
        if "storage" in hardware_type.lower() or "hdd" in hardware_type.lower():
            return SensorType.HDD_TEMP, "°C"
        return SensorType.CPU_TEMP, "°C"

    if sensor_type_str == "Load":
        if "gpu" in hardware_type.lower():
            return SensorType.GPU_LOAD, "%"
        return SensorType.CPU_LOAD, "%"

    return LHM_TYPE_MAP.get(sensor_type_str, (SensorType.CASE_TEMP, ""))


class LHMBackend(HardwareBackend):
    """Hardware backend using LibreHardwareMonitor's HTTP API."""

    def __init__(self, url: str = LHM_URL) -> None:
        self._url = url
        self._client: httpx.AsyncClient | None = None
        self._fan_ids: list[str] = []

    async def initialize(self) -> None:
        self._client = httpx.AsyncClient(base_url=self._url, timeout=5.0)
        # Test connection
        try:
            resp = await self._client.get("/data.json")
            resp.raise_for_status()
        except httpx.HTTPError as e:
            raise RuntimeError(
                f"Cannot connect to LibreHardwareMonitor at {self._url}. "
                "Ensure LHM is running with the web server enabled."
            ) from e

    async def shutdown(self) -> None:
        if self._client:
            await self._client.aclose()

    async def get_sensor_readings(self) -> list[SensorReading]:
        if not self._client:
            return []

        resp = await self._client.get("/data.json")
        resp.raise_for_status()
        data = resp.json()

        readings: list[SensorReading] = []
        self._fan_ids = []
        self._parse_node(data, "", readings)
        return readings

    def _parse_node(
        self, node: dict, hardware_type: str, readings: list[SensorReading]
    ) -> None:
        """Recursively parse LHM's JSON tree into flat sensor readings."""
        text = node.get("Text", "")
        node_id = node.get("id", 0)
        value_str = node.get("Value", "")
        sensor_type_str = node.get("SensorType", "")

        # Detect hardware type from tree structure
        image = node.get("ImageURL", "")
        if "cpu" in image.lower():
            hardware_type = "CPU"
        elif "gpu" in image.lower() or "nvidia" in image.lower() or "amd" in image.lower():
            hardware_type = "GPU"
        elif "hdd" in image.lower() or "ssd" in image.lower():
            hardware_type = "Storage"

        # If this node has a value and sensor type, create a reading
        if value_str and sensor_type_str and value_str != "-":
            try:
                value = float(value_str.split()[0])
            except (ValueError, IndexError):
                value = 0.0

            s_type, unit = _classify_sensor(hardware_type, sensor_type_str)
            sensor_id = f"lhm_{node_id}"

            readings.append(
                SensorReading(
                    id=sensor_id,
                    name=text,
                    sensor_type=s_type,
                    value=value,
                    unit=unit,
                )
            )

            if sensor_type_str == "Control":
                self._fan_ids.append(sensor_id)

        for child in node.get("Children", []):
            self._parse_node(child, hardware_type, readings)

    async def set_fan_speed(self, fan_id: str, speed_percent: float) -> bool:
        # LHM HTTP API doesn't natively support setting fan speeds.
        # This would require WMI or direct SuperIO access on Windows.
        # Placeholder for future implementation.
        return False

    async def get_fan_ids(self) -> list[str]:
        return list(self._fan_ids)

    def get_backend_name(self) -> str:
        return "LibreHardwareMonitor"
