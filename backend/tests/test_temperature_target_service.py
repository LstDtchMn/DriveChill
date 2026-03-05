"""Unit tests for TemperatureTargetService and compute_proportional_speed."""

from __future__ import annotations

import asyncio
import sys
from pathlib import Path
from unittest.mock import AsyncMock, MagicMock

import pytest

_backend_dir = Path(__file__).parent.parent
if str(_backend_dir) not in sys.path:
    sys.path.insert(0, str(_backend_dir))

from app.services.temperature_target_service import (
    TemperatureTargetService,
    compute_proportional_speed,
)
from app.models.temperature_targets import TemperatureTarget


# ── compute_proportional_speed ────────────────────────────────────────────────


class TestComputeProportionalSpeed:
    """Pure-function tests for the proportional control algorithm."""

    def test_below_band_returns_floor_speed(self):
        # temp=30, target=45, tolerance=5 → low=40, temp < low → floor
        speed = compute_proportional_speed(30.0, 45.0, 5.0, 20.0)
        assert speed == 20.0

    def test_above_band_returns_100(self):
        # temp=55, target=45, tolerance=5 → high=50, temp >= high → 100
        speed = compute_proportional_speed(55.0, 45.0, 5.0, 20.0)
        assert speed == 100.0

    def test_at_low_boundary_returns_floor(self):
        # temp=40 == low boundary → floor
        speed = compute_proportional_speed(40.0, 45.0, 5.0, 20.0)
        assert speed == 20.0

    def test_at_high_boundary_returns_100(self):
        # temp=50 == high boundary → 100
        speed = compute_proportional_speed(50.0, 45.0, 5.0, 20.0)
        assert speed == 100.0

    def test_midpoint_interpolation(self):
        # temp=45 (midpoint), target=45, tolerance=5
        # low=40, high=50, t = (45-40)/(10) = 0.5
        # speed = 20 + 0.5 * (100 - 20) = 20 + 40 = 60
        speed = compute_proportional_speed(45.0, 45.0, 5.0, 20.0)
        assert speed == pytest.approx(60.0)

    def test_quarter_point(self):
        # temp=42.5, target=45, tolerance=5
        # low=40, t = (42.5-40)/10 = 0.25
        # speed = 20 + 0.25 * 80 = 40
        speed = compute_proportional_speed(42.5, 45.0, 5.0, 20.0)
        assert speed == pytest.approx(40.0)

    def test_zero_floor_speed(self):
        # min_fan_speed=0 → below band returns 0
        speed = compute_proportional_speed(30.0, 45.0, 5.0, 0.0)
        assert speed == 0.0

    def test_zero_floor_midpoint(self):
        # min=0, midpoint: speed = 0 + 0.5 * 100 = 50
        speed = compute_proportional_speed(45.0, 45.0, 5.0, 0.0)
        assert speed == pytest.approx(50.0)


# ── TemperatureTargetService.evaluate ─────────────────────────────────────────


def _make_target(
    sensor_id: str = "hdd_temp_abc",
    fan_ids: list[str] | None = None,
    target_temp_c: float = 45.0,
    tolerance_c: float = 5.0,
    min_fan_speed: float = 20.0,
    enabled: bool = True,
) -> TemperatureTarget:
    return TemperatureTarget(
        id="t1",
        name="test",
        sensor_id=sensor_id,
        fan_ids=fan_ids or ["fan_1"],
        target_temp_c=target_temp_c,
        tolerance_c=tolerance_c,
        min_fan_speed=min_fan_speed,
        enabled=enabled,
    )


def _make_service(targets: list[TemperatureTarget]) -> TemperatureTargetService:
    repo = MagicMock()
    svc = TemperatureTargetService(repo)
    svc._targets = list(targets)
    return svc


class TestEvaluate:
    def test_single_target_below_band(self):
        svc = _make_service([_make_target()])
        result = svc.evaluate({"hdd_temp_abc": 30.0})
        assert result == {"fan_1": 20.0}

    def test_single_target_above_band(self):
        svc = _make_service([_make_target()])
        result = svc.evaluate({"hdd_temp_abc": 55.0})
        assert result == {"fan_1": 100.0}

    def test_disabled_target_ignored(self):
        svc = _make_service([_make_target(enabled=False)])
        result = svc.evaluate({"hdd_temp_abc": 55.0})
        assert result == {}

    def test_missing_sensor_skipped(self):
        svc = _make_service([_make_target(sensor_id="hdd_temp_xyz")])
        result = svc.evaluate({"hdd_temp_abc": 55.0})
        assert result == {}

    def test_multiple_targets_same_fan_takes_max(self):
        """Multi-drive bay: two drives share the same fan → hottest wins."""
        t1 = TemperatureTarget(
            id="t1", name="cool drive", sensor_id="hdd_temp_a",
            fan_ids=["fan_1"], target_temp_c=45.0, tolerance_c=5.0,
            min_fan_speed=20.0, enabled=True,
        )
        t2 = TemperatureTarget(
            id="t2", name="hot drive", sensor_id="hdd_temp_b",
            fan_ids=["fan_1"], target_temp_c=45.0, tolerance_c=5.0,
            min_fan_speed=20.0, enabled=True,
        )
        svc = _make_service([t1, t2])
        # Drive A at 30°C (below band → 20%), drive B at 55°C (above band → 100%)
        result = svc.evaluate({"hdd_temp_a": 30.0, "hdd_temp_b": 55.0})
        assert result == {"fan_1": 100.0}

    def test_multiple_fans_per_target(self):
        t = _make_target(fan_ids=["fan_1", "fan_2"])
        svc = _make_service([t])
        result = svc.evaluate({"hdd_temp_abc": 45.0})
        expected_speed = pytest.approx(60.0)
        assert result["fan_1"] == expected_speed
        assert result["fan_2"] == expected_speed

    def test_no_targets_returns_empty(self):
        svc = _make_service([])
        result = svc.evaluate({"hdd_temp_abc": 45.0})
        assert result == {}


# ── CRUD methods ──────────────────────────────────────────────────────────────


class TestCrud:
    def test_add_persists_and_caches(self):
        repo = AsyncMock()
        target = _make_target()
        repo.create.return_value = target

        svc = TemperatureTargetService(repo)
        result = asyncio.run(svc.add(target))

        assert result is target
        repo.create.assert_awaited_once_with(target)
        assert target in svc.targets

    def test_update_persists_and_caches(self):
        repo = AsyncMock()
        original = _make_target()
        updated = _make_target()
        updated.target_temp_c = 50.0
        repo.update.return_value = updated

        svc = TemperatureTargetService(repo)
        svc._targets = [original]

        result = asyncio.run(svc.update("t1", target_temp_c=50.0))
        assert result is updated
        assert svc.targets[0].target_temp_c == 50.0

    def test_remove_persists_and_caches(self):
        repo = AsyncMock()
        repo.delete.return_value = True

        svc = TemperatureTargetService(repo)
        svc._targets = [_make_target()]

        assert asyncio.run(svc.remove("t1")) is True
        assert len(svc.targets) == 0

    def test_remove_nonexistent_returns_false(self):
        repo = AsyncMock()
        repo.delete.return_value = False

        svc = TemperatureTargetService(repo)
        assert asyncio.run(svc.remove("nonexistent")) is False

    def test_set_enabled_delegates_to_update(self):
        repo = AsyncMock()
        target = _make_target(enabled=True)
        disabled = _make_target(enabled=False)
        repo.update.return_value = disabled

        svc = TemperatureTargetService(repo)
        svc._targets = [target]

        result = asyncio.run(svc.set_enabled("t1", False))
        assert result is disabled
        repo.update.assert_awaited_once_with("t1", enabled=False)
