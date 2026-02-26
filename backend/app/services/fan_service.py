from __future__ import annotations

import asyncio
import logging
from dataclasses import dataclass
from typing import TYPE_CHECKING

from app.hardware.base import HardwareBackend
from app.models.fan_curves import FanCurve
from app.models.sensors import SensorReading, SensorType
from app.services.curve_engine import evaluate_curve

if TYPE_CHECKING:
    from app.services.sensor_service import SensorService
    from app.services.alert_service import AlertService

logger = logging.getLogger(__name__)

TEMP_SENSOR_TYPES = {SensorType.CPU_TEMP, SensorType.GPU_TEMP, SensorType.HDD_TEMP, SensorType.CASE_TEMP}


def _find_temp_sensor_id(readings: list[SensorReading]) -> str:
    """Return the first CPU temp sensor ID found, or a safe default.

    M-3: single definition used by apply_profile() so main.py and the
    profiles route don't each carry their own copy of this logic.
    """
    for r in readings:
        if r.sensor_type == SensorType.CPU_TEMP:
            return r.id
    logger.warning("No CPU temperature sensor found; defaulting to 'cpu_temp_0'")
    return "cpu_temp_0"


@dataclass
class _HysteresisState:
    """Per-(fan, sensor) hysteresis tracking."""
    last_speed: float = -1.0    # last accepted speed
    decision_temp: float = 0.0  # temp at which last_speed was decided


class FanService:
    """Applies fan curves to control fan speeds based on sensor readings.

    Runs an independent control loop subscribed to SensorService so that
    fan curves are always evaluated regardless of UI connections.

    Supports hysteresis (deadband) to prevent fan oscillation when
    temperatures hover near curve thresholds.
    """

    def __init__(self, backend: HardwareBackend, deadband: float = 3.0) -> None:
        self._backend = backend
        self._curves: list[FanCurve] = []
        self._task: asyncio.Task | None = None

        # Hysteresis state keyed by (fan_id, sensor_id)
        self._deadband = deadband  # degrees C
        self._hyst: dict[tuple[str, str], _HysteresisState] = {}

        # Last applied speeds — read by WebSocket for broadcasting
        self._last_applied: dict[str, float] = {}

        # Set by start() so the control loop can read sensor data
        self._sensor_service: SensorService | None = None
        self._alert_service: AlertService | None = None
        self._queue: asyncio.Queue | None = None

    @property
    def curves(self) -> list[FanCurve]:
        return list(self._curves)

    @property
    def last_applied_speeds(self) -> dict[str, float]:
        return dict(self._last_applied)

    @property
    def deadband(self) -> float:
        return self._deadband

    @deadband.setter
    def deadband(self, value: float) -> None:
        self._deadband = max(0.0, value)

    def _clear_hysteresis(self) -> None:
        self._hyst.clear()

    def set_curves(self, curves: list[FanCurve]) -> None:
        self._curves = list(curves)
        self._clear_hysteresis()

    def update_curve(self, curve: FanCurve) -> None:
        """Update or add a single curve.  Clears hysteresis for the
        affected fan so the new curve takes effect immediately."""
        for i, c in enumerate(self._curves):
            if c.id == curve.id:
                self._curves[i] = curve
                self._hyst.pop((c.fan_id, c.sensor_id), None)
                self._hyst.pop((curve.fan_id, curve.sensor_id), None)
                return
        self._curves.append(curve)

    def remove_curve(self, curve_id: str) -> None:
        for c in self._curves:
            if c.id == curve_id:
                self._hyst.pop((c.fan_id, c.sensor_id), None)
                break
        self._curves = [c for c in self._curves if c.id != curve_id]

    # ------------------------------------------------------------------
    # Independent control loop
    # ------------------------------------------------------------------

    async def start(self, sensor_service, alert_service=None) -> None:
        """Start the fan control loop, subscribing to sensor updates."""
        self._sensor_service = sensor_service
        self._alert_service = alert_service
        self._queue = sensor_service.subscribe()
        self._task = asyncio.create_task(self._control_loop())

    async def stop(self) -> None:
        if self._task:
            self._task.cancel()
            try:
                await self._task
            except asyncio.CancelledError:
                pass
        # H-9: _queue is always defined (set to None in __init__), so
        # hasattr was always True. Check the value instead.
        if self._sensor_service and self._queue is not None:
            self._sensor_service.unsubscribe(self._queue)

    async def _control_loop(self) -> None:
        """Evaluate curves on every sensor snapshot, independent of UI."""
        while True:
            try:
                snapshot = await self._queue.get()
                applied = await self.apply_curves(snapshot.readings)
                self._last_applied = applied

                # Check alerts in the same loop
                if self._alert_service:
                    self._alert_service.check(snapshot.readings)

            except asyncio.CancelledError:
                raise
            except Exception:
                logger.exception("Fan control loop error")

    # ------------------------------------------------------------------
    # Hysteresis
    # ------------------------------------------------------------------

    def _apply_hysteresis(self, fan_id: str, sensor_id: str,
                          current_temp: float, raw_speed: float) -> float:
        """Apply deadband hysteresis to prevent oscillation.

        Ramp up immediately.  Ramp down only when temp drops below the
        decision temp minus deadband.  Decision temp is updated only
        when a speed transition is accepted.
        """
        if self._deadband <= 0:
            return raw_speed

        key = (fan_id, sensor_id)
        st = self._hyst.get(key)

        if st is None:
            self._hyst[key] = _HysteresisState(
                last_speed=raw_speed, decision_temp=current_temp,
            )
            return raw_speed

        if raw_speed >= st.last_speed:
            st.last_speed = raw_speed
            st.decision_temp = current_temp
            return raw_speed

        # Ramp down requested: only allow if temp dropped far enough
        if current_temp <= st.decision_temp - self._deadband:
            st.last_speed = raw_speed
            st.decision_temp = current_temp
            return raw_speed

        # Within deadband — hold previous speed
        return st.last_speed

    # ------------------------------------------------------------------
    # Profile application (M-3: single authoritative implementation)
    # ------------------------------------------------------------------

    async def apply_profile(self, profile) -> None:  # type: ignore[type-arg]
        """Apply a Profile's fan curves.

        Handles both preset and custom profiles.  Call this instead of
        duplicating the preset-expansion logic in routes and main.py.
        """
        from app.models.profiles import ProfilePreset, PRESET_CURVES

        if profile.preset != ProfilePreset.CUSTOM and profile.preset in PRESET_CURVES:
            fan_ids = await self._backend.get_fan_ids()
            readings = self._sensor_service.latest if self._sensor_service else []
            temp_sensor_id = _find_temp_sensor_id(readings)
            self.set_curves([
                FanCurve(
                    id=f"{profile.id}_{fid}",
                    name=f"{profile.name} - {fid}",
                    sensor_id=temp_sensor_id,
                    fan_id=fid,
                    points=PRESET_CURVES[profile.preset],
                )
                for fid in fan_ids
            ])
        else:
            self.set_curves(profile.curves)

    # ------------------------------------------------------------------
    # Core evaluation
    # ------------------------------------------------------------------

    async def apply_curves(self, readings: list[SensorReading]) -> dict[str, float]:
        """Evaluate all curves against current readings and set fan speeds.

        Returns a dict of fan_id -> applied speed percent.
        """
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

            speed = self._apply_hysteresis(curve.fan_id, curve.sensor_id, temp, speed)

            if curve.fan_id in applied:
                applied[curve.fan_id] = max(applied[curve.fan_id], speed)
            else:
                applied[curve.fan_id] = speed

        for fan_id, speed in applied.items():
            await self._backend.set_fan_speed(fan_id, speed)

        return applied
