"""Tests for fan speed ramp rate limiting."""

from __future__ import annotations

import asyncio
import sys
import time
from pathlib import Path
from unittest.mock import AsyncMock, MagicMock

import pytest

# Ensure backend imports resolve.
_backend_dir = Path(__file__).parent.parent
if str(_backend_dir) not in sys.path:
    sys.path.insert(0, str(_backend_dir))

from app.models.fan_curves import FanCurve, FanCurvePoint
from app.models.sensors import SensorReading, SensorType
from app.services.fan_service import FanService


def _make_backend(fan_ids: list[str] | None = None) -> AsyncMock:
    """Create a mock HardwareBackend."""
    backend = AsyncMock()
    backend.get_fan_ids = AsyncMock(return_value=fan_ids or ["fan_0"])
    backend.set_fan_speed = AsyncMock(return_value=True)
    backend.get_backend_name = MagicMock(return_value="mock")
    return backend


def _make_readings(cpu_temp: float = 60.0) -> list[SensorReading]:
    return [SensorReading(id="cpu_temp_0", name="CPU", value=cpu_temp,
                          sensor_type=SensorType.CPU_TEMP, unit="°C")]


def _make_curve(sensor_id: str = "cpu_temp_0", fan_id: str = "fan_0",
                points: list[tuple[float, float]] | None = None) -> FanCurve:
    pts = points or [(0, 20), (50, 30), (70, 60), (90, 100)]
    return FanCurve(
        id="curve_1", name="Test Curve", sensor_id=sensor_id, fan_id=fan_id,
        points=[FanCurvePoint(temp=t, speed=s) for t, s in pts],
    )


# ---------------------------------------------------------------------------
# Tests
# ---------------------------------------------------------------------------


def test_ramp_rate_disabled_instant_speed():
    """When ramp_rate=0 (disabled), speed should apply instantly."""
    async def _go():
        backend = _make_backend()
        svc = FanService(backend, ramp_rate_pct_per_sec=0.0)
        svc.set_curves([_make_curve()])

        readings = _make_readings(cpu_temp=90.0)
        result = await svc.apply_curves(readings)

        # Should apply full speed immediately (100% at 90°C)
        assert result["fan_0"] == pytest.approx(100.0, abs=0.5)
        backend.set_fan_speed.assert_called_once_with("fan_0", pytest.approx(100.0, abs=0.5))
    asyncio.run(_go())


def test_ramp_rate_clamps_large_increase():
    """A large speed jump should be clamped by the ramp rate."""
    async def _go():
        backend = _make_backend()
        svc = FanService(backend, ramp_rate_pct_per_sec=10.0)
        svc.set_curves([_make_curve()])

        # First call establishes ramp state at low speed.
        readings_low = _make_readings(cpu_temp=30.0)
        result1 = await svc.apply_curves(readings_low)
        first_speed = result1["fan_0"]

        # Small sleep to create elapsed time, then jump temp high.
        await asyncio.sleep(0.1)  # ~0.1s elapsed → max delta ~1%
        readings_high = _make_readings(cpu_temp=90.0)
        result2 = await svc.apply_curves(readings_high)

        # Speed should NOT jump to 100% instantly — clamped by ramp rate.
        assert result2["fan_0"] < 100.0
        assert result2["fan_0"] > first_speed  # But it should go up
    asyncio.run(_go())


def test_ramp_rate_clamps_decrease():
    """Speed ramp-down should also be limited."""
    async def _go():
        backend = _make_backend()
        svc = FanService(backend, ramp_rate_pct_per_sec=10.0, deadband=0.0)
        svc.set_curves([_make_curve()])

        # Start at high temp (100%).
        readings_high = _make_readings(cpu_temp=90.0)
        await svc.apply_curves(readings_high)

        await asyncio.sleep(0.1)

        # Drop to low temp immediately.
        readings_low = _make_readings(cpu_temp=30.0)
        result = await svc.apply_curves(readings_low)

        # Should NOT drop to ~20% instantly — clamped.
        assert result["fan_0"] > 30.0  # Still close to 100, not at ~20%
    asyncio.run(_go())


def test_panic_mode_bypasses_ramp_rate():
    """Emergency fan calls should bypass ramp rate limiting entirely."""
    async def _go():
        backend = _make_backend()
        svc = FanService(backend, ramp_rate_pct_per_sec=5.0)
        svc.set_curves([_make_curve()])

        # Start at low speed.
        await svc.apply_curves(_make_readings(cpu_temp=30.0))

        # Emergency all fans goes through _emergency_all_fans, not apply_curves,
        # so it should set 100% immediately regardless of ramp rate.
        await svc._emergency_all_fans(100.0)

        # Verify the backend got the full 100% call.
        last_call = backend.set_fan_speed.call_args_list[-1]
        assert last_call[0] == ("fan_0", 100.0)
    asyncio.run(_go())


def test_configure_ramp_rate_hot_reload():
    """Ramp rate should be changeable at runtime."""
    async def _go():
        backend = _make_backend()
        svc = FanService(backend, ramp_rate_pct_per_sec=0.0)

        assert svc.ramp_rate == 0.0

        svc.configure_ramp_rate(15.0)
        assert svc.ramp_rate == 15.0

        # Negative values should be clamped to 0.
        svc.configure_ramp_rate(-5.0)
        assert svc.ramp_rate == 0.0
    asyncio.run(_go())
