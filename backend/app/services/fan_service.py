from __future__ import annotations

import asyncio
import logging
from dataclasses import dataclass
from typing import TYPE_CHECKING

from app.hardware.base import HardwareBackend
from app.models.fan_curves import FanCurve
from app.models.sensors import SensorReading, SensorType

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

    Safe-mode states
    ----------------
    ``_sensor_panic``
        SensorService reported >N consecutive failures; all fans forced to
        100% until a good reading arrives.
    ``_temp_panic``
        A temperature reading exceeded the panic threshold; all fans forced
        to 100% until temps drop below ``threshold - hysteresis``.
    ``_released``
        User clicked "Release Fan Control"; all fans handed back to
        BIOS/firmware auto mode.  Cleared only when a profile is
        explicitly applied via ``apply_profile()``.
    """

    def __init__(
        self,
        backend: HardwareBackend,
        deadband: float = 3.0,
        panic_cpu_temp: float = 95.0,
        panic_gpu_temp: float = 90.0,
        panic_hysteresis: float = 5.0,
    ) -> None:
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

        # Safe-mode state
        self._released: bool = False
        self._sensor_panic: bool = False
        self._temp_panic: bool = False
        self._panic_cpu_temp: float = panic_cpu_temp
        self._panic_gpu_temp: float = panic_gpu_temp
        self._panic_hysteresis: float = panic_hysteresis

    # ------------------------------------------------------------------
    # Configuration
    # ------------------------------------------------------------------

    def configure_panic_thresholds(
        self,
        cpu_temp: float,
        gpu_temp: float,
        hysteresis: float,
    ) -> None:
        """Update panic threshold configuration (hot-reloadable)."""
        self._panic_cpu_temp = cpu_temp
        self._panic_gpu_temp = gpu_temp
        self._panic_hysteresis = hysteresis

    # ------------------------------------------------------------------
    # Curve management
    # ------------------------------------------------------------------

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
    # Safe-mode status
    # ------------------------------------------------------------------

    @property
    def safe_mode_status(self) -> dict:
        """Current safe-mode / panic state for WebSocket broadcasting."""
        if self._released:
            reason: str | None = "released"
        elif self._temp_panic:
            reason = "temp_panic"
        elif self._sensor_panic:
            reason = "sensor_failure"
        else:
            reason = None
        return {
            "active": self._sensor_panic or self._temp_panic,
            "released": self._released,
            "reason": reason,
        }

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

                # None sentinel: SensorService consecutive failure escalation.
                if snapshot is None:
                    if not self._sensor_panic:
                        self._sensor_panic = True
                        logger.error(
                            "Entering sensor-failure panic mode — all fans set to 100%%"
                        )
                    if not self._released:
                        await self._emergency_all_fans(100.0)
                    continue

                # Real snapshot received: clear sensor panic.
                if self._sensor_panic:
                    self._sensor_panic = False
                    logger.info(
                        "Sensor readings restored — exiting sensor-failure panic mode"
                    )

                # Check temperature panic thresholds.
                entered_temp_panic = self._update_temp_panic(snapshot.readings)
                if entered_temp_panic:
                    logger.error(
                        "Entering temperature panic mode — all fans set to 100%%"
                    )

                # User released fan control — don't apply any curves.
                if self._released:
                    self._last_applied = {}
                    continue

                if self._temp_panic:
                    await self._emergency_all_fans(100.0)
                    continue

                # Normal curve evaluation.
                applied = await self.apply_curves(snapshot.readings)
                self._last_applied = applied

                # Check alerts in the same loop.
                if self._alert_service:
                    self._alert_service.check(snapshot.readings)

            except asyncio.CancelledError:
                raise
            except Exception:
                logger.exception("Fan control loop error")

    async def _emergency_all_fans(self, speed: float) -> None:
        """Set ALL known fans to the given speed (used for panic mode)."""
        fan_ids = await self._backend.get_fan_ids()
        for fan_id in fan_ids:
            try:
                await self._backend.set_fan_speed(fan_id, speed)
            except Exception:
                logger.exception("Failed to set emergency speed on fan %s", fan_id)
        self._last_applied = {fan_id: speed for fan_id in fan_ids}

    # ------------------------------------------------------------------
    # Temperature panic logic
    # ------------------------------------------------------------------

    def _update_temp_panic(self, readings: list[SensorReading]) -> bool:
        """Check if we should enter or exit temperature panic mode.

        Returns True if we just *entered* panic (for log message de-dup).
        """
        was_panic = self._temp_panic

        if self._temp_panic:
            # Currently in panic — exit only when ALL panic-triggering sensors
            # are below threshold minus hysteresis.
            cpu_clear = True
            gpu_clear = True
            for r in readings:
                if r.sensor_type == SensorType.CPU_TEMP:
                    if r.value >= self._panic_cpu_temp - self._panic_hysteresis:
                        cpu_clear = False
                elif r.sensor_type == SensorType.GPU_TEMP:
                    if r.value >= self._panic_gpu_temp - self._panic_hysteresis:
                        gpu_clear = False
            if cpu_clear and gpu_clear:
                self._temp_panic = False
                logger.info(
                    "Temperature panic cleared — resuming normal fan control"
                )
        else:
            # Not in panic — enter if any sensor exceeds its threshold.
            for r in readings:
                if r.sensor_type == SensorType.CPU_TEMP and r.value >= self._panic_cpu_temp:
                    self._temp_panic = True
                    logger.error(
                        "CPU temp %.1f°C >= panic threshold %.1f°C",
                        r.value, self._panic_cpu_temp,
                    )
                    break
                if r.sensor_type == SensorType.GPU_TEMP and r.value >= self._panic_gpu_temp:
                    self._temp_panic = True
                    logger.error(
                        "GPU temp %.1f°C >= panic threshold %.1f°C",
                        r.value, self._panic_gpu_temp,
                    )
                    break

        return self._temp_panic and not was_panic

    # ------------------------------------------------------------------
    # Release / resume fan control
    # ------------------------------------------------------------------

    async def release_fan_control(self) -> None:
        """Hand all fans back to BIOS/firmware automatic control.

        Sets ``_released=True`` so the control loop won't apply any curves.
        Calls ``backend.release_fan_control()`` so hardware exits software
        PWM mode immediately.
        """
        self._released = True
        self._last_applied = {}
        try:
            await self._backend.release_fan_control()
            logger.info("Fan control released to BIOS/firmware auto mode")
        except Exception:
            logger.exception("Backend release_fan_control() failed")

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
        Clears the released flag so the control loop resumes curve-based control.
        """
        from app.models.profiles import ProfilePreset, PRESET_CURVES

        # Applying a profile always re-enables software fan control.
        self._released = False

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
        from app.services.curve_engine import evaluate_curve

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
