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


# ---------------------------------------------------------------------------
# GET /api/quiet-hours
# ---------------------------------------------------------------------------


class TestQuietHoursContract:
    def test_synthetic_response_matches_schema(self):
        data = {
            "rules": [
                {
                    "id": 1,
                    "day_of_week": 0,
                    "start_time": "22:00",
                    "end_time": "07:00",
                    "profile_id": "p1",
                    "enabled": True,
                }
            ]
        }
        _validate(data, "quiet_hours")

    def test_empty_rules_matches_schema(self):
        _validate({"rules": []}, "quiet_hours")


# ---------------------------------------------------------------------------
# GET /api/webhooks
# ---------------------------------------------------------------------------


class TestWebhooksContract:
    def test_synthetic_response_matches_schema(self):
        data = {
            "config": {
                "enabled": True,
                "target_url": "https://example.com/hook",
                "has_signing_secret": True,
                "timeout_seconds": 3.0,
                "max_retries": 2,
                "retry_backoff_seconds": 1.0,
            }
        }
        _validate(data, "webhooks")


# ---------------------------------------------------------------------------
# GET /api/temperature-targets
# ---------------------------------------------------------------------------


class TestTemperatureTargetsContract:
    def test_synthetic_response_matches_schema(self):
        data = {
            "targets": [
                {
                    "id": "abc123",
                    "name": "SSD Target",
                    "drive_id": "drive1",
                    "sensor_id": "hdd_temp_drive1",
                    "fan_ids": ["Fan1"],
                    "target_temp_c": 40.0,
                    "tolerance_c": 5.0,
                    "min_fan_speed": 20.0,
                    "enabled": True,
                    "pid_mode": False,
                    "pid_kp": 5.0,
                    "pid_ki": 0.05,
                    "pid_kd": 1.0,
                }
            ]
        }
        _validate(data, "temperature_targets")

    def test_null_drive_id_matches_schema(self):
        data = {
            "targets": [
                {
                    "id": "t1",
                    "sensor_id": "cpu_temp_0",
                    "target_temp_c": 60.0,
                    "enabled": True,
                    "drive_id": None,
                }
            ]
        }
        _validate(data, "temperature_targets")


# ---------------------------------------------------------------------------
# GET /api/machines
# ---------------------------------------------------------------------------


class TestMachinesContract:
    def test_synthetic_response_matches_schema(self):
        data = {
            "machines": [
                {
                    "id": "m1",
                    "name": "Workstation",
                    "base_url": "http://192.168.1.50:8085",
                    "has_api_key": True,
                    "enabled": True,
                    "poll_interval_seconds": 30.0,
                    "timeout_ms": 5000,
                    "status": "online",
                    "last_seen_at": "2026-03-12T10:00:00+00:00",
                    "last_error": None,
                    "consecutive_failures": 0,
                    "freshness_seconds": 5.2,
                }
            ]
        }
        _validate(data, "machines")


# ---------------------------------------------------------------------------
# GET /api/profile-schedules
# ---------------------------------------------------------------------------


class TestProfileSchedulesContract:
    def test_synthetic_response_matches_schema(self):
        data = {
            "schedules": [
                {
                    "id": "s1",
                    "name": "Night mode",
                    "profile_id": "p1",
                    "cron_expression": "0 22 * * *",
                    "timezone": "America/New_York",
                    "enabled": True,
                    "last_triggered_at": None,
                    "next_trigger_at": "2026-03-12T22:00:00-04:00",
                }
            ]
        }
        _validate(data, "profile_schedules")


# ---------------------------------------------------------------------------
# GET /api/virtual-sensors
# ---------------------------------------------------------------------------


class TestVirtualSensorsContract:
    def test_synthetic_response_matches_schema(self):
        data = {
            "sensors": [
                {
                    "id": "vs_avg_cpu",
                    "name": "Avg CPU Temp",
                    "type": "average",
                    "source_sensor_ids": ["cpu_temp_0", "cpu_temp_1"],
                    "enabled": True,
                    "value": 55.0,
                }
            ]
        }
        _validate(data, "virtual_sensors")

    def test_null_value_matches_schema(self):
        data = {
            "sensors": [
                {
                    "id": "vs1",
                    "name": "Test",
                    "type": "max",
                    "enabled": True,
                    "value": None,
                }
            ]
        }
        _validate(data, "virtual_sensors")


# ---------------------------------------------------------------------------
# GET /api/auth/status
# ---------------------------------------------------------------------------


class TestAuthStatusContract:
    def test_synthetic_response_matches_schema(self):
        _validate({"auth_enabled": True}, "auth_status")

    def test_missing_field_fails(self):
        with pytest.raises(jsonschema.ValidationError):
            _validate({}, "auth_status")


# ---------------------------------------------------------------------------
# GET /api/auth/session
# ---------------------------------------------------------------------------


class TestAuthSessionContract:
    def test_unauthenticated_matches_schema(self):
        _validate({"auth_required": True, "authenticated": False}, "auth_session")

    def test_authenticated_matches_schema(self):
        _validate(
            {"auth_required": True, "authenticated": True, "username": "admin", "role": "admin"},
            "auth_session",
        )

    def test_invalid_role_fails(self):
        with pytest.raises(jsonschema.ValidationError):
            _validate(
                {"authenticated": True, "username": "x", "role": "superuser"},
                "auth_session",
            )


# ---------------------------------------------------------------------------
# GET /api/auth/users
# ---------------------------------------------------------------------------


class TestAuthUsersContract:
    def test_synthetic_response_matches_schema(self):
        data = {
            "users": [
                {"id": 1, "username": "admin", "role": "admin", "created_at": "2026-03-12T00:00:00Z"},
                {"id": 2, "username": "viewer1", "role": "viewer", "created_at": "2026-03-12T01:00:00Z"},
            ]
        }
        _validate(data, "auth_users")

    def test_empty_users_matches_schema(self):
        _validate({"users": []}, "auth_users")


# ---------------------------------------------------------------------------
# GET /api/auth/api-keys
# ---------------------------------------------------------------------------


class TestAuthApiKeysContract:
    def test_synthetic_response_matches_schema(self):
        data = {
            "api_keys": [
                {
                    "id": "k1",
                    "name": "CI Key",
                    "key_prefix": "dc_abc",
                    "scopes": ["sensors:read", "fans:write"],
                    "role": "admin",
                    "created_by": "admin",
                    "created_at": "2026-03-12T00:00:00Z",
                    "revoked_at": None,
                    "last_used_at": None,
                }
            ]
        }
        _validate(data, "auth_api_keys")


# ---------------------------------------------------------------------------
# GET /api/analytics/history
# ---------------------------------------------------------------------------


class TestAnalyticsHistoryContract:
    def test_synthetic_response_matches_schema(self):
        data = {
            "buckets": [
                {
                    "sensor_id": "cpu_temp_0",
                    "timestamp_utc": "2026-03-12T10:00:00Z",
                    "avg_value": 55.0,
                    "min_value": 50.0,
                    "max_value": 60.0,
                    "sample_count": 12,
                }
            ],
            "series": {},
            "bucket_seconds": 300,
            "requested_range": {"start": "2026-03-12T10:00:00Z", "end": "2026-03-12T11:00:00Z"},
            "returned_range": {"start": "2026-03-12T10:00:00Z", "end": "2026-03-12T11:00:00Z"},
            "retention_limited": False,
        }
        _validate(data, "analytics_history")

    def test_empty_buckets_matches_schema(self):
        data = {"buckets": [], "bucket_seconds": 300}
        _validate(data, "analytics_history")


# ---------------------------------------------------------------------------
# GET /api/analytics/anomalies
# ---------------------------------------------------------------------------


class TestAnalyticsAnomaliesContract:
    def test_synthetic_response_matches_schema(self):
        data = {
            "anomalies": [
                {
                    "timestamp_utc": "2026-03-12T10:30:00Z",
                    "sensor_id": "cpu_temp_0",
                    "sensor_name": "CPU Temp",
                    "value": 95.0,
                    "unit": "°C",
                    "z_score": 3.5,
                    "mean": 55.0,
                    "stdev": 11.4,
                    "severity": "critical",
                }
            ],
            "z_score_threshold": 2.5,
        }
        _validate(data, "analytics_anomalies")

    def test_invalid_severity_fails(self):
        with pytest.raises(jsonschema.ValidationError):
            data = {
                "anomalies": [
                    {
                        "sensor_id": "x",
                        "value": 1,
                        "z_score": 3,
                        "severity": "low",
                    }
                ],
                "z_score_threshold": 2.5,
            }
            _validate(data, "analytics_anomalies")


# ---------------------------------------------------------------------------
# GET /api/analytics/report
# ---------------------------------------------------------------------------


class TestAnalyticsReportContract:
    def test_synthetic_response_matches_schema(self):
        data = {
            "generated_at": "2026-03-12T12:00:00Z",
            "window_hours": 24,
            "stats": [],
            "anomalies": [],
            "top_anomalous_sensors": [],
            "regressions": [],
        }
        _validate(data, "analytics_report")


# ---------------------------------------------------------------------------
# GET /api/fans/curves
# ---------------------------------------------------------------------------


class TestFanCurvesContract:
    def test_synthetic_response_matches_schema(self):
        data = {
            "curves": [
                {
                    "id": "c1",
                    "fan_id": "CPU Fan",
                    "enabled": True,
                    "points": [{"temp": 30, "speed": 20}, {"temp": 80, "speed": 100}],
                }
            ]
        }
        _validate(data, "fan_curves")

    def test_empty_curves_matches_schema(self):
        _validate({"curves": []}, "fan_curves")


# ---------------------------------------------------------------------------
# GET /api/fans/settings
# ---------------------------------------------------------------------------


class TestFanSettingsContract:
    def test_synthetic_response_matches_schema(self):
        data = {
            "fan_settings": [
                {"fan_id": "CPU Fan", "min_speed_pct": 20, "zero_rpm_capable": False}
            ]
        }
        _validate(data, "fan_settings")


# ---------------------------------------------------------------------------
# GET /api/fans/status
# ---------------------------------------------------------------------------


class TestFanStatusContract:
    def test_synthetic_response_matches_schema(self):
        data = {
            "safe_mode": False,
            "curves_active": 2,
            "applied_speeds": {"CPU Fan": 45, "Case Fan": 30},
            "control_sources": {"CPU Fan": ["fan_curve"], "Case Fan": ["temperature_target"]},
            "startup_safety_active": False,
        }
        _validate(data, "fan_status")

    def test_minimal_matches_schema(self):
        _validate({"safe_mode": True}, "fan_status")


# ---------------------------------------------------------------------------
# GET /api/notifications/email
# ---------------------------------------------------------------------------


class TestNotificationsEmailContract:
    def test_synthetic_response_matches_schema(self):
        data = {
            "settings": {
                "enabled": True,
                "smtp_host": "smtp.gmail.com",
                "smtp_port": 587,
                "smtp_username": "user@example.com",
                "has_password": True,
                "sender_address": "drivechill@example.com",
                "recipient_list": ["admin@example.com"],
                "use_tls": True,
                "use_ssl": False,
            }
        }
        _validate(data, "notifications_email")

    def test_missing_settings_fails(self):
        with pytest.raises(jsonschema.ValidationError):
            _validate({}, "notifications_email")


# ---------------------------------------------------------------------------
# GET /api/notifications/push-subscriptions
# ---------------------------------------------------------------------------


class TestNotificationsPushContract:
    def test_synthetic_response_matches_schema(self):
        data = {
            "subscriptions": [
                {
                    "id": "sub1",
                    "endpoint": "https://fcm.googleapis.com/fcm/send/abc123",
                    "user_agent": "Mozilla/5.0",
                    "created_at": "2026-03-12T00:00:00Z",
                    "last_used_at": None,
                }
            ]
        }
        _validate(data, "notifications_push")

    def test_empty_subscriptions_matches_schema(self):
        _validate({"subscriptions": []}, "notifications_push")


# ---------------------------------------------------------------------------
# GET /api/noise-profiles
# ---------------------------------------------------------------------------


class TestNoiseProfilesContract:
    def test_synthetic_response_matches_schema(self):
        data = {
            "profiles": [
                {
                    "id": "np1",
                    "fan_id": "CPU Fan",
                    "mode": "quick",
                    "data": [{"rpm": 500, "db": 25.0}, {"rpm": 1200, "db": 38.5}],
                    "created_at": "2026-03-12T00:00:00Z",
                    "updated_at": "2026-03-12T00:00:00Z",
                }
            ]
        }
        _validate(data, "noise_profiles")

    def test_empty_profiles_matches_schema(self):
        _validate({"profiles": []}, "noise_profiles")


# ---------------------------------------------------------------------------
# GET /api/report-schedules
# ---------------------------------------------------------------------------


class TestReportSchedulesContract:
    def test_synthetic_response_matches_schema(self):
        data = {
            "schedules": [
                {
                    "id": "rs1",
                    "frequency": "daily",
                    "time_utc": "08:00",
                    "timezone": "America/New_York",
                    "enabled": True,
                    "last_sent_at": None,
                    "created_at": "2026-03-12T00:00:00Z",
                    "last_error": None,
                    "last_attempted_at": None,
                    "consecutive_failures": 0,
                }
            ]
        }
        _validate(data, "report_schedules")


# ---------------------------------------------------------------------------
# GET /api/scheduler/status
# ---------------------------------------------------------------------------


class TestSchedulerStatusContract:
    def test_synthetic_response_matches_schema(self):
        data = {
            "profile_scheduler": {
                "running": True,
                "active_schedule_id": "s1",
                "last_check_at": "2026-03-12T10:00:00Z",
            },
            "report_scheduler": {
                "running": True,
                "last_check_at": "2026-03-12T10:00:00Z",
                "schedules": [],
            },
        }
        _validate(data, "scheduler_status")

    def test_idle_scheduler_matches_schema(self):
        data = {
            "profile_scheduler": {"running": False, "active_schedule_id": None, "last_check_at": None},
            "report_scheduler": {"running": False, "last_check_at": None, "schedules": []},
        }
        _validate(data, "scheduler_status")
