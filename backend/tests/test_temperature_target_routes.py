"""Route-level tests for temperature target API endpoints.

Covers:
- CRUD happy paths (create, read, update, delete, toggle)
- Validation: sensor_id format, sensor existence, fan_ids non-empty, fan existence
- PUT sensor_id relaxation (unchanged sensor_id allowed even if offline)
"""

from __future__ import annotations

import asyncio
import sys
from pathlib import Path
from unittest.mock import AsyncMock, MagicMock, PropertyMock

import pytest

_backend_dir = Path(__file__).parent.parent
if str(_backend_dir) not in sys.path:
    sys.path.insert(0, str(_backend_dir))

from fastapi import HTTPException

from app.api.routes.temperature_targets import (
    _validate_fan_ids,
    _validate_sensor_id,
    create_target,
    delete_target,
    get_target,
    list_targets,
    toggle_target,
    update_target,
)
from app.models.temperature_targets import (
    TemperatureTarget,
    TemperatureTargetCreate,
    TemperatureTargetToggle,
    TemperatureTargetUpdate,
)


# ── Helpers ───────────────────────────────────────────────────────────────────


def _make_target(**overrides) -> TemperatureTarget:
    defaults = dict(
        id="abc123",
        name="Bay Fan",
        drive_id="xyz",
        sensor_id="hdd_temp_xyz",
        fan_ids=["fan_1"],
        target_temp_c=45.0,
        tolerance_c=5.0,
        min_fan_speed=20.0,
        enabled=True,
    )
    defaults.update(overrides)
    return TemperatureTarget(**defaults)


def _mock_request(
    targets: list[TemperatureTarget] | None = None,
    sensor_ids: set[str] | None = None,
    fan_ids: set[str] | None = None,
) -> MagicMock:
    """Build a mock Request with temperature_target_service, sensor_service, fan_service."""
    req = MagicMock()

    svc = AsyncMock()
    type(svc).targets = PropertyMock(return_value=targets or [])
    req.app.state.temperature_target_service = svc

    sensor_svc = MagicMock()
    if sensor_ids is not None:
        readings = []
        for sid in sensor_ids:
            r = MagicMock()
            r.id = sid
            readings.append(r)
        type(sensor_svc).latest = PropertyMock(return_value=readings)
    else:
        type(sensor_svc).latest = PropertyMock(return_value=[])
    req.app.state.sensor_service = sensor_svc

    fan_svc = MagicMock()
    fan_svc.last_applied_speeds = {fid: 50.0 for fid in (fan_ids or set())}
    req.app.state.fan_service = fan_svc

    return req


# ── _validate_sensor_id ──────────────────────────────────────────────────────


class TestValidateSensorId:
    def test_accepts_valid_hdd_temp_id(self):
        req = _mock_request(sensor_ids={"hdd_temp_abc"})
        _validate_sensor_id("hdd_temp_abc", req, is_new=True)  # must not raise

    def test_rejects_non_hdd_prefix(self):
        req = _mock_request()
        with pytest.raises(HTTPException) as exc_info:
            _validate_sensor_id("cpu_temp_0", req, is_new=True)
        assert exc_info.value.status_code == 422
        assert "hdd_temp_*" in exc_info.value.detail

    def test_rejects_unknown_sensor_on_create(self):
        req = _mock_request(sensor_ids={"hdd_temp_abc"})
        with pytest.raises(HTTPException) as exc_info:
            _validate_sensor_id("hdd_temp_unknown", req, is_new=True)
        assert exc_info.value.status_code == 422
        assert "sensor not found" in exc_info.value.detail

    def test_allows_unknown_sensor_when_not_new(self):
        """PUT with unchanged sensor_id: drive may be offline → allow."""
        req = _mock_request(sensor_ids={"hdd_temp_abc"})
        _validate_sensor_id("hdd_temp_unknown", req, is_new=False)  # must not raise

    def test_allows_when_no_sensors_known(self):
        """If sensor service has no readings yet, skip existence check."""
        req = _mock_request(sensor_ids=set())
        _validate_sensor_id("hdd_temp_xyz", req, is_new=True)  # must not raise


# ── _validate_fan_ids ─────────────────────────────────────────────────────────


class TestValidateFanIds:
    def test_accepts_valid_fan_ids(self):
        req = _mock_request(fan_ids={"fan_1", "fan_2"})
        _validate_fan_ids(["fan_1"], req)  # must not raise

    def test_rejects_empty_list(self):
        req = _mock_request()
        with pytest.raises(HTTPException) as exc_info:
            _validate_fan_ids([], req)
        assert exc_info.value.status_code == 422
        assert "must not be empty" in exc_info.value.detail

    def test_rejects_unknown_fan_id(self):
        req = _mock_request(fan_ids={"fan_1"})
        with pytest.raises(HTTPException) as exc_info:
            _validate_fan_ids(["fan_1", "fan_999"], req)
        assert exc_info.value.status_code == 422
        assert "fan not found" in exc_info.value.detail

    def test_allows_when_no_fans_known(self):
        """If fan service has no known fans yet, skip existence check."""
        req = _mock_request(fan_ids=set())
        _validate_fan_ids(["fan_1"], req)  # must not raise


# ── list_targets ──────────────────────────────────────────────────────────────


class TestListTargets:
    def test_returns_wrapped_list(self):
        t = _make_target()
        req = _mock_request(targets=[t])
        result = asyncio.run(list_targets(req))
        assert "targets" in result
        assert len(result["targets"]) == 1
        assert result["targets"][0]["id"] == "abc123"


# ── create_target ─────────────────────────────────────────────────────────────


class TestCreateTarget:
    def test_create_returns_target(self):
        req = _mock_request(
            sensor_ids={"hdd_temp_xyz"},
            fan_ids={"fan_1"},
        )
        body = TemperatureTargetCreate(
            name="Test",
            drive_id="xyz",
            sensor_id="hdd_temp_xyz",
            fan_ids=["fan_1"],
            target_temp_c=45.0,
        )
        created = _make_target()
        req.app.state.temperature_target_service.add.return_value = created

        result = asyncio.run(create_target(req, body))
        assert result["id"] == "abc123"

    def test_create_invalid_sensor_rejects(self):
        req = _mock_request(sensor_ids={"hdd_temp_abc"}, fan_ids={"fan_1"})
        body = TemperatureTargetCreate(
            name="Test",
            sensor_id="cpu_temp_0",
            fan_ids=["fan_1"],
            target_temp_c=45.0,
        )
        with pytest.raises(HTTPException) as exc_info:
            asyncio.run(create_target(req, body))
        assert exc_info.value.status_code == 422

    def test_create_empty_fan_ids_rejects(self):
        req = _mock_request(sensor_ids={"hdd_temp_xyz"})
        body = TemperatureTargetCreate(
            name="Test",
            sensor_id="hdd_temp_xyz",
            fan_ids=[],
            target_temp_c=45.0,
        )
        with pytest.raises(HTTPException) as exc_info:
            asyncio.run(create_target(req, body))
        assert exc_info.value.status_code == 422


# ── get_target ────────────────────────────────────────────────────────────────


class TestGetTarget:
    def test_returns_existing(self):
        t = _make_target()
        req = _mock_request(targets=[t])
        result = asyncio.run(get_target(req, "abc123"))
        assert result["id"] == "abc123"

    def test_404_for_missing(self):
        req = _mock_request(targets=[])
        with pytest.raises(HTTPException) as exc_info:
            asyncio.run(get_target(req, "nonexistent"))
        assert exc_info.value.status_code == 404


# ── update_target ─────────────────────────────────────────────────────────────


class TestUpdateTarget:
    def test_update_returns_updated(self):
        existing = _make_target()
        updated = _make_target(target_temp_c=50.0)
        req = _mock_request(
            targets=[existing],
            sensor_ids={"hdd_temp_xyz"},
            fan_ids={"fan_1"},
        )
        req.app.state.temperature_target_service.update.return_value = updated

        body = TemperatureTargetUpdate(
            name="Bay Fan",
            drive_id="xyz",
            sensor_id="hdd_temp_xyz",
            fan_ids=["fan_1"],
            target_temp_c=50.0,
        )
        result = asyncio.run(update_target(req, "abc123", body))
        assert result["target_temp_c"] == 50.0

    def test_update_unchanged_sensor_allowed_offline(self):
        """PUT with same sensor_id doesn't require sensor to be online."""
        existing = _make_target(sensor_id="hdd_temp_xyz")
        updated = _make_target(sensor_id="hdd_temp_xyz", target_temp_c=50.0)
        # sensor_ids does NOT include hdd_temp_xyz → drive is offline
        req = _mock_request(
            targets=[existing],
            sensor_ids={"hdd_temp_other"},
            fan_ids={"fan_1"},
        )
        req.app.state.temperature_target_service.update.return_value = updated

        body = TemperatureTargetUpdate(
            name="Bay Fan",
            drive_id="xyz",
            sensor_id="hdd_temp_xyz",  # unchanged
            fan_ids=["fan_1"],
            target_temp_c=50.0,
        )
        result = asyncio.run(update_target(req, "abc123", body))
        assert result["target_temp_c"] == 50.0

    def test_update_changed_sensor_validated(self):
        """PUT with changed sensor_id requires the new sensor to exist."""
        existing = _make_target(sensor_id="hdd_temp_old")
        req = _mock_request(
            targets=[existing],
            sensor_ids={"hdd_temp_old"},
            fan_ids={"fan_1"},
        )

        body = TemperatureTargetUpdate(
            name="Bay Fan",
            sensor_id="hdd_temp_new",  # changed but doesn't exist
            fan_ids=["fan_1"],
            target_temp_c=50.0,
        )
        with pytest.raises(HTTPException) as exc_info:
            asyncio.run(update_target(req, "abc123", body))
        assert exc_info.value.status_code == 422
        assert "sensor not found" in exc_info.value.detail

    def test_update_404_for_missing(self):
        req = _mock_request(targets=[], sensor_ids={"hdd_temp_xyz"}, fan_ids={"fan_1"})

        body = TemperatureTargetUpdate(
            name="Bay Fan",
            sensor_id="hdd_temp_xyz",
            fan_ids=["fan_1"],
            target_temp_c=50.0,
        )
        with pytest.raises(HTTPException) as exc_info:
            asyncio.run(update_target(req, "nonexistent", body))
        assert exc_info.value.status_code == 404


# ── delete_target ─────────────────────────────────────────────────────────────


class TestDeleteTarget:
    def test_delete_returns_success(self):
        req = _mock_request()
        req.app.state.temperature_target_service.remove.return_value = True
        result = asyncio.run(delete_target(req, "abc123"))
        assert result == {"success": True}

    def test_delete_404_for_missing(self):
        req = _mock_request()
        req.app.state.temperature_target_service.remove.return_value = False
        with pytest.raises(HTTPException) as exc_info:
            asyncio.run(delete_target(req, "nonexistent"))
        assert exc_info.value.status_code == 404


# ── toggle_target ─────────────────────────────────────────────────────────────


class TestToggleTarget:
    def test_toggle_returns_full_object(self):
        toggled = _make_target(enabled=False)
        req = _mock_request()
        req.app.state.temperature_target_service.set_enabled.return_value = toggled

        body = TemperatureTargetToggle(enabled=False)
        result = asyncio.run(toggle_target(req, "abc123", body))
        assert result["enabled"] is False
        assert "id" in result  # full object, not just { enabled: ... }

    def test_toggle_404_for_missing(self):
        req = _mock_request()
        req.app.state.temperature_target_service.set_enabled.return_value = None

        body = TemperatureTargetToggle(enabled=True)
        with pytest.raises(HTTPException) as exc_info:
            asyncio.run(toggle_target(req, "nonexistent", body))
        assert exc_info.value.status_code == 404
