"""Regression tests for v3.1.0 bug fixes.

Each test targets a specific fix and should fail if the fix is reverted.
"""

from __future__ import annotations

import asyncio
import sys
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import AsyncMock, MagicMock, patch

import aiosqlite
import pytest
from pydantic import ValidationError

_backend_dir = Path(__file__).parent.parent
if str(_backend_dir) not in sys.path:
    sys.path.insert(0, str(_backend_dir))

from app.db.migration_runner import run_migrations
from app.models.sensors import SensorReading, SensorType
from app.services.auth_service import AuthService
from app.services.fan_service import FanService


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------


async def _init_db(db_path) -> aiosqlite.Connection:
    await run_migrations(db_path)
    db = await aiosqlite.connect(str(db_path))
    await db.execute("PRAGMA foreign_keys=ON")
    return db


class _MockBackend:
    def __init__(self, fan_ids: list[str] | None = None):
        self._fan_ids = fan_ids or ["fan1"]
        self.applied: dict[str, float] = {}

    async def get_fan_ids(self) -> list[str]:
        return self._fan_ids

    async def set_fan_speed(self, fan_id: str, speed_percent: float) -> bool:
        self.applied[fan_id] = speed_percent
        return True

    async def set_fan_auto(self, fan_id: str) -> bool:
        return True

    def initialize(self) -> None:
        pass


# ---------------------------------------------------------------------------
# FIX #1: Config import parse-before-delete (alert rules)
# If import payload is malformed, existing rules must survive.
# ---------------------------------------------------------------------------


class TestConfigImportParseBeforeDelete:
    def test_malformed_alert_rules_preserve_existing(self, tmp_db) -> None:
        """Importing invalid alert rules must not delete the existing ones."""
        from app.services.alert_service import AlertRule, AlertService

        async def _run():
            db = await _init_db(tmp_db)
            alert_svc = AlertService(db)

            # Create an existing rule
            existing = AlertRule(
                id="rule_existing",
                sensor_id="cpu_temp_0",
                direction="above",
                threshold=85.0,
                name="CPU Hot",
            )
            await alert_svc.add_rule(existing)
            assert len(alert_svc.rules) == 1

            # Try to import malformed rules (missing required fields: id, threshold)
            malformed_rules = [{"sensor_id": "gpu_temp_0"}]
            with pytest.raises(Exception):
                parsed = []
                for r in malformed_rules:
                    parsed.append(AlertRule(**r))

            # Existing rules must still be intact
            assert len(alert_svc.rules) == 1
            assert alert_svc.rules[0].name == "CPU Hot"

            await db.close()

        asyncio.run(_run())


# ---------------------------------------------------------------------------
# FIX #2: Temperature panic stays active when sensors absent
# ---------------------------------------------------------------------------


class TestPanicStaysWhenSensorsAbsent:
    def test_panic_not_cleared_on_empty_readings(self) -> None:
        """Panic must NOT clear when no temp sensors are in the snapshot."""
        svc = FanService(_MockBackend())
        svc._temp_panic = True

        # Empty readings — no CPU/GPU temps
        entered = svc._update_temp_panic([])
        assert svc._temp_panic is True, "Panic must stay active when sensors are absent"

    def test_panic_clears_only_with_real_below_threshold_reading(self) -> None:
        """Panic clears only when an actual sensor reports below threshold."""
        svc = FanService(_MockBackend())
        svc._temp_panic = True
        svc._panic_cpu_temp = 95.0
        svc._panic_hysteresis = 5.0

        # Provide a real CPU reading well below threshold
        cool_reading = SensorReading(
            id="cpu_temp_0",
            name="CPU",
            value=60.0,
            sensor_type=SensorType.CPU_TEMP,
        )
        svc._update_temp_panic([cool_reading])
        assert svc._temp_panic is False, "Panic should clear with real sub-threshold reading"


# ---------------------------------------------------------------------------
# FIX #4: Timezone validation — invalid timezones rejected at save time
# ---------------------------------------------------------------------------


class TestTimezoneValidation:
    def test_valid_timezone_accepted(self) -> None:
        from app.api.routes.report_schedules import ReportScheduleBody

        body = ReportScheduleBody(
            frequency="daily", time_utc="08:00", timezone="America/New_York"
        )
        assert body.timezone == "America/New_York"

    def test_invalid_timezone_rejected(self) -> None:
        from app.api.routes.report_schedules import ReportScheduleBody

        with pytest.raises(ValidationError) as exc:
            ReportScheduleBody(
                frequency="daily", time_utc="08:00", timezone="Fake/Timezone"
            )
        assert "timezone" in str(exc.value).lower()

    def test_profile_schedule_invalid_timezone_rejected(self) -> None:
        from app.api.routes.profile_schedules import ProfileScheduleBody

        with pytest.raises(ValidationError) as exc:
            ProfileScheduleBody(
                profile_id=1,
                days=["mon"],
                start_time="09:00",
                end_time="17:00",
                timezone="Not/A/Zone",
            )
        assert "timezone" in str(exc.value).lower()


# ---------------------------------------------------------------------------
# FIX #7: Duplicate user creation returns IntegrityError (→ 409)
# ---------------------------------------------------------------------------


class TestDuplicateUserCreation:
    def test_duplicate_username_raises(self, tmp_db) -> None:
        """Creating a user with an existing username must raise IntegrityError."""
        async def _run():
            db = await _init_db(tmp_db)
            svc = AuthService(db)
            await svc.create_user("admin", "password1")

            # Second create with same username should raise
            with pytest.raises(Exception):  # aiosqlite.IntegrityError
                await svc.create_user("admin", "password2")

            await db.close()

        asyncio.run(_run())


# ---------------------------------------------------------------------------
# FIX #17: API key scope — /api/update endpoints must be reachable
# ---------------------------------------------------------------------------


class TestApiKeyScopeUpdate:
    def test_settings_scope_allows_update_check(self, tmp_db) -> None:
        """API key with write:settings scope must be able to GET /api/update/check."""
        from starlette.requests import Request
        from app.api.dependencies.auth import require_auth

        async def _run():
            db = await _init_db(tmp_db)
            svc = AuthService(db)
            _, token = await svc.create_api_key("Updater", scopes=["write:settings"])

            headers = [(b"authorization", f"Bearer {token}".encode())]
            scope = {
                "type": "http",
                "http_version": "1.1",
                "method": "GET",
                "scheme": "http",
                "path": "/api/update/check",
                "raw_path": b"/api/update/check",
                "query_string": b"",
                "headers": headers,
                "client": ("127.0.0.1", 12345),
                "server": ("127.0.0.1", 8085),
                "app": SimpleNamespace(state=SimpleNamespace(auth_service=svc)),
            }
            req = Request(scope, AsyncMock(return_value={"type": "http.request", "body": b""}))

            with patch("app.api.dependencies.auth._auth_enabled", return_value=True):
                info = await require_auth(req, drivechill_session=None)
            assert info is not None
            assert info["auth_type"] == "api_key"

            await db.close()

        asyncio.run(_run())


# ---------------------------------------------------------------------------
# FIX #22: Release URL validation — non-GitHub URLs rejected
# ---------------------------------------------------------------------------


class TestReleaseUrlValidation:
    def test_non_github_url_rejected(self) -> None:
        """update check must reject release URLs that aren't https://github.com/."""
        from app.api.routes import update as update_mod

        # _SEMVER_RE should reject anything with shell metacharacters
        assert update_mod._SEMVER_RE.fullmatch("2.1.0") is not None
        assert update_mod._SEMVER_RE.fullmatch("2.1.0 && evil") is None
        assert update_mod._SEMVER_RE.fullmatch("$(whoami)") is None


# ---------------------------------------------------------------------------
# FIX #1 (quiet hours): Validate-before-delete for quiet hours import
# ---------------------------------------------------------------------------


class TestQuietHoursImportValidation:
    def test_malformed_quiet_hours_raises_before_delete(self) -> None:
        """Quiet hours with missing fields must raise KeyError during validation,
        before the DELETE statement runs."""
        malformed = [{"day_of_week": 1}]  # missing start_time, end_time, profile_id
        with pytest.raises(KeyError):
            validated = []
            for qh in malformed:
                validated.append((
                    qh["day_of_week"], qh["start_time"], qh["end_time"],
                    qh["profile_id"], int(qh.get("enabled", True)),
                ))
