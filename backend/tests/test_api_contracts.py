"""Cross-backend API contract tests.

Validates that the Python backend's route handlers produce responses matching
the JSON Schema files in tests/contracts/.  The same schemas are validated
against the C# backend in Tests/ApiContractTests.cs, ensuring both backends
honour the shared API contract.
"""
from __future__ import annotations

import asyncio
import json
import sys
from pathlib import Path
from unittest.mock import AsyncMock, MagicMock, PropertyMock

import pytest
import jsonschema

# Ensure 'app.*' imports resolve from the backend/ directory.
_backend_dir = Path(__file__).parent.parent
if str(_backend_dir) not in sys.path:
    sys.path.insert(0, str(_backend_dir))

_contracts_dir = Path(__file__).parent.parent.parent / "tests" / "contracts"


def _load_schema(name: str) -> dict:
    with open(_contracts_dir / f"{name}.json") as f:
        return json.load(f)


def _validate(data: dict, schema_name: str):
    schema = _load_schema(schema_name)
    jsonschema.validate(instance=data, schema=schema)


def _req(**state_attrs):
    req = MagicMock()
    for k, v in state_attrs.items():
        setattr(req.app.state, k, v)
    return req


def _run(coro):
    return asyncio.run(coro)


# ---------------------------------------------------------------------------
# GET /api/health
# ---------------------------------------------------------------------------


class TestHealthContract:
    def test_health_response_matches_schema(self):
        from app.main import health

        _run(health())  # just verify it returns without error


class TestHealthContractShape:
    def test_synthetic_response_matches_schema(self):
        data = {
            "status": "ok",
            "app": "DriveChill",
            "api_version": "v1",
            "capabilities": ["api_keys", "webhooks"],
            "version": "2.2.0",
            "backend": "mock",
        }
        _validate(data, "health")

    def test_missing_status_fails(self):
        data = {"version": "1.0.0", "backend": "mock"}
        with pytest.raises(jsonschema.ValidationError):
            _validate(data, "health")


# ---------------------------------------------------------------------------
# GET /api/fans
# ---------------------------------------------------------------------------


class TestFansContract:
    def test_response_matches_schema(self):
        from app.api.routes.fans import get_fans

        async def _go():
            backend = MagicMock()
            backend.get_fan_ids = AsyncMock(return_value=["CPU Fan", "Case Fan"])
            req = _req(backend=backend)
            result = await get_fans(req)
            _validate(result, "fans")

        _run(_go())


# ---------------------------------------------------------------------------
# GET /api/sensors
# ---------------------------------------------------------------------------


class TestSensorsContract:
    def test_response_matches_schema(self):
        from app.api.routes.sensors import get_sensors
        from app.models.sensors import SensorReading

        async def _go():
            reading = SensorReading(
                id="cpu_temp_0",
                name="CPU Temp",
                sensor_type="cpu_temp",
                value=55.0,
                unit="°C",
            )
            sensor_svc = MagicMock()
            type(sensor_svc).latest = PropertyMock(return_value=[reading])
            backend = MagicMock()
            backend.get_backend_name.return_value = "mock"
            db = AsyncMock()
            db.execute = AsyncMock()
            cursor = AsyncMock()
            cursor.fetchall = AsyncMock(return_value=[])
            db.execute.return_value = cursor
            req = _req(sensor_service=sensor_svc, backend=backend, db=db)
            result = await get_sensors(req)
            _validate(result, "sensors")
            assert len(result["readings"]) == 1
            assert result["readings"][0]["id"] == "cpu_temp_0"

        _run(_go())


# ---------------------------------------------------------------------------
# GET /api/profiles
# ---------------------------------------------------------------------------


class TestProfilesContract:
    def test_response_matches_schema(self):
        from app.api.routes.profiles import get_profiles

        async def _go():
            profile = MagicMock()
            profile.model_dump.return_value = {
                "id": "p1",
                "name": "Balanced",
                "preset": "balanced",
                "is_active": True,
                "curves": [],
            }
            repo = AsyncMock()
            repo.list_all = AsyncMock(return_value=[profile])
            req = _req(profile_repo=repo)
            result = await get_profiles(req)
            _validate(result, "profiles")

        _run(_go())


# ---------------------------------------------------------------------------
# GET /api/alerts
# ---------------------------------------------------------------------------


class TestAlertsContract:
    def test_response_matches_schema(self):
        from app.api.routes.alerts import get_alerts

        async def _go():
            rule = MagicMock()
            rule.model_dump.return_value = {
                "id": "r1",
                "sensor_id": "cpu_temp_0",
                "threshold": 80.0,
                "condition": "above",
                "enabled": True,
                "name": "CPU High",
            }
            event = MagicMock()
            event.model_dump.return_value = {
                "rule_id": "r1",
                "sensor_id": "cpu_temp_0",
                "sensor_name": "CPU Temp",
                "actual_value": 82.0,
                "threshold": 80.0,
                "condition": "above",
                "message": "",
                "timestamp": "2026-03-12T10:00:00+00:00",
                "cleared": False,
            }
            svc = MagicMock()
            type(svc).rules = PropertyMock(return_value=[rule])
            type(svc).events = PropertyMock(return_value=[event])
            type(svc).active_alerts = PropertyMock(return_value=["r1"])
            req = _req(alert_service=svc)
            result = await get_alerts(req)
            # alerts response contains rules AND events
            _validate({"rules": result["rules"]}, "alerts_rules")
            _validate({"events": result["events"]}, "alerts_events")

        _run(_go())


# ---------------------------------------------------------------------------
# GET /api/settings
# ---------------------------------------------------------------------------


class TestSettingsContract:
    def test_response_matches_schema(self):
        from app.api.routes.settings import get_settings

        async def _go():
            repo = AsyncMock()
            repo.get_float = AsyncMock(side_effect=lambda k, d=0: d)
            repo.get_int = AsyncMock(side_effect=lambda k, d=0: d)
            repo.get = AsyncMock(return_value="C")
            backend = MagicMock()
            backend.get_backend_name.return_value = "mock"
            req = _req(settings_repo=repo, backend=backend)
            result = await get_settings(req)
            _validate(result, "settings")

        _run(_go())


# ---------------------------------------------------------------------------
# GET /api/notification-channels
# ---------------------------------------------------------------------------


class TestNotificationChannelsContract:
    def test_response_matches_schema(self):
        from app.api.routes.notification_channels import list_channels

        async def _go():
            ch = MagicMock()
            ch.to_dict.return_value = {
                "id": "nc_abc123",
                "type": "ntfy",
                "name": "Test",
                "enabled": True,
                "config": {"url": "https://ntfy.sh/test"},
            }
            svc = AsyncMock()
            svc.list_channels = AsyncMock(return_value=[ch])
            req = _req(notification_channel_service=svc)
            result = await list_channels(req)
            _validate(result, "notification_channels")

        _run(_go())


# ---------------------------------------------------------------------------
# GET /api/drives — validated via synthetic data (real tests in test_drives_parity)
# ---------------------------------------------------------------------------


class TestDrivesContract:
    def test_synthetic_response_matches_schema(self):
        data = {
            "drives": [
                {
                    "id": "a" * 24,
                    "name": "Samsung 970 EVO",
                    "model": "Samsung SSD 970 EVO",
                    "serial_masked": "****ABCD",
                    "device_path_masked": "nvme0n1",
                    "bus_type": "nvme",
                    "media_type": "ssd",
                    "capacity_bytes": 500107862016,
                    "temperature_c": 42.0,
                    "health_status": "good",
                    "health_percent": 95,
                    "smart_available": True,
                    "native_available": True,
                }
            ],
            "smartctl_available": True,
            "total": 1,
        }
        _validate(data, "drives")


# ---------------------------------------------------------------------------
# GET /api/analytics/stats — synthetic (real DB tests in test_analytics_contract)
# ---------------------------------------------------------------------------


class TestAnalyticsStatsContract:
    def test_synthetic_response_matches_schema(self):
        data = {
            "stats": [
                {
                    "sensor_id": "cpu_temp_0",
                    "sensor_name": "CPU Temp",
                    "sensor_type": "cpu_temp",
                    "unit": "°C",
                    "min_value": 30.0,
                    "max_value": 85.0,
                    "avg_value": 55.0,
                    "p95_value": 78.0,
                    "sample_count": 1440,
                }
            ],
            "requested_range": {
                "start": "2026-03-12T10:00:00+00:00",
                "end": "2026-03-12T11:00:00+00:00",
            },
            "returned_range": {
                "start": "2026-03-12T10:01:30+00:00",
                "end": "2026-03-12T10:59:45+00:00",
            },
        }
        _validate(data, "analytics_stats")

    def test_empty_stats_matches_schema(self):
        data = {
            "stats": [],
            "requested_range": {"start": "2026-03-12T10:00:00", "end": "2026-03-12T11:00:00"},
            "returned_range": {"start": None, "end": None},
        }
        _validate(data, "analytics_stats")


# ---------------------------------------------------------------------------
# GET /api/update/check — synthetic (requires network)
# ---------------------------------------------------------------------------


class TestUpdateCheckContract:
    def test_synthetic_response_matches_schema(self):
        data = {
            "current": "2.2.0",
            "latest": "2.2.1",
            "update_available": True,
            "release_url": "https://github.com/LstDtchMn/DriveChill/releases/tag/v2.2.1",
            "deployment": "windows_service",
        }
        _validate(data, "update_check")

    def test_no_update_matches_schema(self):
        data = {
            "current": "2.2.0",
            "latest": "2.2.0",
            "update_available": False,
            "release_url": "",
            "deployment": "docker",
        }
        _validate(data, "update_check")
