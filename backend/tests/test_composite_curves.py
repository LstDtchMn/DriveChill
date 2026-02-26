"""Tests for composite curve evaluation (multi-sensor MAX temperature input)."""

from __future__ import annotations

from app.models.fan_curves import FanCurve, FanCurvePoint
from app.services.curve_engine import evaluate_curve, resolve_composite_temp


SIMPLE_POINTS = [
    FanCurvePoint(temp=30, speed=20),
    FanCurvePoint(temp=60, speed=50),
    FanCurvePoint(temp=90, speed=100),
]


def _make_curve(sensor_id: str = "cpu_temp_0", sensor_ids: list[str] | None = None) -> FanCurve:
    return FanCurve(
        id="c1", name="Test", sensor_id=sensor_id, fan_id="fan_1",
        points=SIMPLE_POINTS, sensor_ids=sensor_ids or [],
    )


class TestResolveCompositeTemp:

    def test_single_sensor_fallback(self) -> None:
        """When sensor_ids is empty, falls back to sensor_id."""
        curve = _make_curve(sensor_id="cpu_temp_0")
        vals = {"cpu_temp_0": 65.0, "gpu_temp_0": 80.0}
        assert resolve_composite_temp(curve, vals) == 65.0

    def test_composite_max_of_two(self) -> None:
        """Composite uses MAX of listed sensors."""
        curve = _make_curve(sensor_ids=["cpu_temp_0", "gpu_temp_0"])
        vals = {"cpu_temp_0": 60.0, "gpu_temp_0": 80.0}
        assert resolve_composite_temp(curve, vals) == 80.0

    def test_composite_max_all_equal(self) -> None:
        curve = _make_curve(sensor_ids=["cpu_temp_0", "gpu_temp_0"])
        vals = {"cpu_temp_0": 70.0, "gpu_temp_0": 70.0}
        assert resolve_composite_temp(curve, vals) == 70.0

    def test_composite_one_sensor_missing(self) -> None:
        """If one sensor is missing, uses the available one."""
        curve = _make_curve(sensor_ids=["cpu_temp_0", "gpu_temp_0"])
        vals = {"cpu_temp_0": 55.0}
        assert resolve_composite_temp(curve, vals) == 55.0

    def test_composite_all_sensors_missing_fallback(self) -> None:
        """All composite sensors missing, falls back to primary sensor_id."""
        curve = _make_curve(sensor_id="cpu_temp_0", sensor_ids=["gpu_temp_0", "hdd_temp_0"])
        vals = {"cpu_temp_0": 45.0}
        assert resolve_composite_temp(curve, vals) == 45.0

    def test_composite_and_primary_both_missing(self) -> None:
        """If all sensors are missing, returns None."""
        curve = _make_curve(sensor_id="cpu_temp_0", sensor_ids=["gpu_temp_0"])
        vals = {"hdd_temp_0": 50.0}
        assert resolve_composite_temp(curve, vals) is None

    def test_composite_three_sensors(self) -> None:
        curve = _make_curve(sensor_ids=["a", "b", "c"])
        vals = {"a": 50.0, "b": 75.0, "c": 60.0}
        assert resolve_composite_temp(curve, vals) == 75.0


class TestEvaluateCurveIntegration:

    def test_single_sensor_evaluation(self) -> None:
        """Backward-compat: single-sensor curve evaluates correctly."""
        curve = _make_curve(sensor_id="cpu_temp_0")
        speed = evaluate_curve(curve, 60.0)
        assert speed == 50.0  # exactly at the 60°C point

    def test_composite_evaluation_end_to_end(self) -> None:
        """Composite temp → evaluate_curve produces correct speed."""
        curve = _make_curve(sensor_ids=["cpu_temp_0", "gpu_temp_0"])
        vals = {"cpu_temp_0": 60.0, "gpu_temp_0": 90.0}
        temp = resolve_composite_temp(curve, vals)
        assert temp == 90.0
        speed = evaluate_curve(curve, temp)
        assert speed == 100.0  # 90°C = top of curve

    def test_disabled_curve_returns_minus_one(self) -> None:
        curve = _make_curve()
        curve.enabled = False
        assert evaluate_curve(curve, 50.0) == -1

    def test_sensor_ids_field_defaults_empty(self) -> None:
        """New FanCurve objects default to empty sensor_ids."""
        curve = FanCurve(id="x", name="X", sensor_id="s", fan_id="f")
        assert curve.sensor_ids == []
