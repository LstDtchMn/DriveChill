import asyncio

from app.hardware.base import HardwareBackend
from app.models.fan_curves import FanCurve
from app.models.sensors import SensorReading, SensorType
from app.services.curve_engine import evaluate_curve


TEMP_SENSOR_TYPES = {SensorType.CPU_TEMP, SensorType.GPU_TEMP, SensorType.HDD_TEMP, SensorType.CASE_TEMP}


class FanService:
    """Applies fan curves to control fan speeds based on sensor readings."""

    def __init__(self, backend: HardwareBackend) -> None:
        self._backend = backend
        self._curves: list[FanCurve] = []
        self._running = False
        self._task: asyncio.Task | None = None

    @property
    def curves(self) -> list[FanCurve]:
        return list(self._curves)

    def set_curves(self, curves: list[FanCurve]) -> None:
        self._curves = list(curves)

    def update_curve(self, curve: FanCurve) -> None:
        """Update or add a single curve."""
        for i, c in enumerate(self._curves):
            if c.id == curve.id:
                self._curves[i] = curve
                return
        self._curves.append(curve)

    def remove_curve(self, curve_id: str) -> None:
        self._curves = [c for c in self._curves if c.id != curve_id]

    async def apply_curves(self, readings: list[SensorReading]) -> dict[str, float]:
        """Evaluate all curves against current readings and set fan speeds.

        Returns a dict of fan_id -> applied speed percent.
        """
        # Build a lookup: sensor_id -> current value
        sensor_values: dict[str, float] = {}
        for r in readings:
            if r.sensor_type in TEMP_SENSOR_TYPES:
                sensor_values[r.id] = r.value

        applied: dict[str, float] = {}

        for curve in self._curves:
            temp = sensor_values.get(curve.sensor_id)
            if temp is None:
                continue

            speed = evaluate_curve(curve, temp)
            if speed < 0:
                continue

            # If multiple curves target the same fan, use the highest speed
            if curve.fan_id in applied:
                applied[curve.fan_id] = max(applied[curve.fan_id], speed)
            else:
                applied[curve.fan_id] = speed

        # Apply to hardware
        for fan_id, speed in applied.items():
            await self._backend.set_fan_speed(fan_id, speed)

        return applied
