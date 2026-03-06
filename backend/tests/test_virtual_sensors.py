"""Tests for the virtual sensor service and resolution logic."""

import math

import pytest

from app.services.virtual_sensor_service import (
    VirtualSensorDef,
    VirtualSensorService,
    VIRTUAL_SENSOR_TYPES,
)


@pytest.fixture
def svc():
    return VirtualSensorService()


def _def(id: str, type: str, source_ids: list[str], **kwargs) -> VirtualSensorDef:
    return VirtualSensorDef(id=id, name=id, type=type, source_ids=source_ids, **kwargs)


class TestVirtualSensorTypes:
    def test_all_types_listed(self):
        assert VIRTUAL_SENSOR_TYPES == {"max", "min", "avg", "weighted", "delta", "moving_avg"}


class TestMaxSensor:
    def test_max_of_multiple(self, svc):
        svc.load([_def("vs_max", "max", ["a", "b", "c"])])
        result = svc.resolve_all({"a": 50.0, "b": 70.0, "c": 60.0})
        assert result["vs_max"] == 70.0

    def test_max_skips_missing(self, svc):
        svc.load([_def("vs_max", "max", ["a", "b"])])
        result = svc.resolve_all({"a": 50.0})
        assert result["vs_max"] == 50.0

    def test_max_with_offset(self, svc):
        svc.load([_def("vs_max", "max", ["a", "b"], offset=5.0)])
        result = svc.resolve_all({"a": 40.0, "b": 60.0})
        assert result["vs_max"] == 65.0


class TestMinSensor:
    def test_min_of_multiple(self, svc):
        svc.load([_def("vs_min", "min", ["a", "b", "c"])])
        result = svc.resolve_all({"a": 50.0, "b": 30.0, "c": 60.0})
        assert result["vs_min"] == 30.0


class TestAvgSensor:
    def test_avg_of_multiple(self, svc):
        svc.load([_def("vs_avg", "avg", ["a", "b"])])
        result = svc.resolve_all({"a": 40.0, "b": 60.0})
        assert result["vs_avg"] == 50.0


class TestWeightedSensor:
    def test_weighted_with_weights(self, svc):
        svc.load([_def("vs_w", "weighted", ["a", "b"], weights=[0.75, 0.25])])
        result = svc.resolve_all({"a": 40.0, "b": 80.0})
        # (40*0.75 + 80*0.25) / (0.75 + 0.25) = (30 + 20) / 1.0 = 50
        assert result["vs_w"] == 50.0

    def test_weighted_fallback_no_weights(self, svc):
        svc.load([_def("vs_w", "weighted", ["a", "b"])])
        result = svc.resolve_all({"a": 40.0, "b": 60.0})
        # Falls back to avg
        assert result["vs_w"] == 50.0

    def test_weighted_partial_sources(self, svc):
        svc.load([_def("vs_w", "weighted", ["a", "b"], weights=[0.5, 0.5])])
        result = svc.resolve_all({"a": 80.0})
        # Only 'a' available: 80*0.5 / 0.5 = 80
        assert result["vs_w"] == 80.0


class TestDeltaSensor:
    def test_delta_two_sources(self, svc):
        svc.load([_def("vs_d", "delta", ["hot", "cold"])])
        result = svc.resolve_all({"hot": 45.0, "cold": 25.0})
        assert result["vs_d"] == 20.0

    def test_delta_with_offset(self, svc):
        svc.load([_def("vs_d", "delta", ["hot", "cold"], offset=-2.0)])
        result = svc.resolve_all({"hot": 45.0, "cold": 25.0})
        assert result["vs_d"] == 18.0

    def test_delta_missing_source_returns_none(self, svc):
        svc.load([_def("vs_d", "delta", ["hot", "cold"])])
        result = svc.resolve_all({"hot": 45.0})
        assert "vs_d" not in result

    def test_delta_needs_two_sources(self, svc):
        svc.load([_def("vs_d", "delta", ["only_one"])])
        result = svc.resolve_all({"only_one": 45.0})
        assert "vs_d" not in result


class TestMovingAvgSensor:
    def test_moving_avg_initial_equals_instant(self, svc):
        svc.load([_def("vs_ema", "moving_avg", ["a"], window_seconds=30.0)])
        result = svc.resolve_all({"a": 50.0})
        assert result["vs_ema"] == 50.0

    def test_moving_avg_smooths_over_time(self, svc):
        svc.load([_def("vs_ema", "moving_avg", ["a"], window_seconds=30.0)])
        # First call seeds the EMA
        svc.resolve_all({"a": 50.0})
        # Manually advance the EMA state time to simulate time passing
        state = svc._ema_state["vs_ema"]
        import time
        state.last_update = time.monotonic() - 10.0  # 10 seconds ago
        result = svc.resolve_all({"a": 100.0})
        # EMA should be between 50 and 100
        assert 50.0 < result["vs_ema"] < 100.0


class TestEdgeCases:
    def test_nan_source_skipped(self, svc):
        svc.load([_def("vs_max", "max", ["a", "b"])])
        result = svc.resolve_all({"a": float("nan"), "b": 50.0})
        assert result["vs_max"] == 50.0

    def test_inf_source_skipped(self, svc):
        svc.load([_def("vs_max", "max", ["a", "b"])])
        result = svc.resolve_all({"a": float("inf"), "b": 50.0})
        assert result["vs_max"] == 50.0

    def test_all_sources_missing_produces_no_result(self, svc):
        svc.load([_def("vs_max", "max", ["x", "y"])])
        result = svc.resolve_all({"a": 50.0})
        assert "vs_max" not in result

    def test_disabled_sensor_not_resolved(self, svc):
        svc.load([_def("vs_max", "max", ["a"], enabled=False)])
        result = svc.resolve_all({"a": 50.0})
        assert "vs_max" not in result

    def test_virtual_sensor_can_reference_another(self, svc):
        svc.load([
            _def("vs_max", "max", ["a", "b"]),
            _def("vs_offset", "max", ["vs_max"], offset=10.0),
        ])
        result = svc.resolve_all({"a": 50.0, "b": 60.0})
        assert result["vs_max"] == 60.0
        assert result["vs_offset"] == 70.0

    def test_unknown_type_produces_no_result(self, svc):
        svc.load([_def("vs_bad", "unknown_type", ["a"])])
        result = svc.resolve_all({"a": 50.0})
        assert "vs_bad" not in result

    def test_load_replaces_definitions(self, svc):
        svc.load([_def("vs1", "max", ["a"])])
        svc.load([_def("vs2", "min", ["b"])])
        assert len(svc.definitions) == 1
        assert svc.definitions[0].id == "vs2"

    def test_real_sensor_values_preserved(self, svc):
        svc.load([_def("vs_max", "max", ["a"])])
        result = svc.resolve_all({"a": 50.0, "b": 30.0})
        assert result["a"] == 50.0
        assert result["b"] == 30.0
        assert result["vs_max"] == 50.0
