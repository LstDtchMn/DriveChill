"""Route-level tests for drive monitoring API endpoints.

Covers:
- Input validation (drive_id format, run_id format, hours bounds)
- Serialisation helpers (_mask_serial, _mask_device_path)
- Rescan and refresh route logic
- Self-test start (ValueErrors → 400) and abort (409 on failure) routes
- Drive history retention-limit flag
"""
from __future__ import annotations

import asyncio
import sys
from pathlib import Path
from unittest.mock import AsyncMock, MagicMock

import pytest

_backend_dir = Path(__file__).parent.parent
if str(_backend_dir) not in sys.path:
    sys.path.insert(0, str(_backend_dir))

from fastapi import HTTPException

from app.api.routes.drives import (
    _mask_device_path,
    _mask_serial,
    _validate_drive_id,
    _validate_run_id,
    abort_self_test,
    get_drive_history,
    refresh_drive,
    rescan_drives,
    start_self_test,
)
from app.models.drives import SelfTestType

# ── Helpers ───────────────────────────────────────────────────────────────────

VALID_DRIVE_ID = "a" * 24   # 24 lowercase hex chars
VALID_RUN_ID   = "b" * 16   # 16 lowercase hex chars


def _make_request(state: MagicMock) -> MagicMock:
    req = MagicMock()
    req.app.state = state
    return req


# ── _validate_drive_id ────────────────────────────────────────────────────────

class TestValidateDriveId:
    def test_accepts_24_hex_chars(self):
        _validate_drive_id(VALID_DRIVE_ID)  # must not raise

    def test_rejects_short_id(self):
        with pytest.raises(HTTPException) as exc_info:
            _validate_drive_id("abc123")
        assert exc_info.value.status_code == 400

    def test_rejects_non_hex_chars(self):
        with pytest.raises(HTTPException) as exc_info:
            _validate_drive_id("z" * 24)
        assert exc_info.value.status_code == 400

    def test_rejects_uppercase_hex(self):
        with pytest.raises(HTTPException):
            _validate_drive_id("A" * 24)

    def test_rejects_26_chars(self):
        with pytest.raises(HTTPException):
            _validate_drive_id("a" * 26)


# ── _validate_run_id ──────────────────────────────────────────────────────────

class TestValidateRunId:
    def test_accepts_16_hex_chars(self):
        _validate_run_id(VALID_RUN_ID)

    def test_rejects_short(self):
        with pytest.raises(HTTPException) as exc_info:
            _validate_run_id("abc")
        assert exc_info.value.status_code == 400

    def test_rejects_non_hex(self):
        with pytest.raises(HTTPException):
            _validate_run_id("g" * 16)

    def test_rejects_18_chars(self):
        with pytest.raises(HTTPException):
            _validate_run_id("a" * 18)


# ── _mask_serial / _mask_device_path ─────────────────────────────────────────

class TestMaskSerial:
    def test_shows_last_four(self):
        assert _mask_serial("SN123456") == "****3456"

    def test_empty_string(self):
        assert _mask_serial("") == "****"

    def test_short_serial(self):
        assert _mask_serial("AB") == "****AB"


class TestMaskDevicePath:
    def test_unix_path(self):
        assert _mask_device_path("/dev/sda") == "sda"

    def test_bare_name(self):
        assert _mask_device_path("disk0") == "disk0"


# ── rescan_drives ─────────────────────────────────────────────────────────────

class TestRescanDrives:
    def test_returns_count(self):
        async def _run():
            monitor = MagicMock()
            monitor.rescan_now = AsyncMock(return_value=3)
            state = MagicMock()
            state.drive_monitor_service = monitor
            req = _make_request(state)
            result = await rescan_drives(req)
            assert result == {"drives_found": 3}
            monitor.rescan_now.assert_called_once()
        asyncio.run(_run())

    def test_503_when_monitor_absent(self):
        async def _run():
            state = MagicMock()
            state.drive_monitor_service = None
            req = _make_request(state)
            with pytest.raises(HTTPException) as exc_info:
                await rescan_drives(req)
            assert exc_info.value.status_code == 503
        asyncio.run(_run())


# ── refresh_drive ─────────────────────────────────────────────────────────────

class TestRefreshDrive:
    def test_400_on_bad_drive_id(self):
        async def _run():
            state = MagicMock()
            req = _make_request(state)
            with pytest.raises(HTTPException) as exc_info:
                await refresh_drive("bad-id!!", req)
            assert exc_info.value.status_code == 400
        asyncio.run(_run())

    def test_404_when_drive_not_found(self):
        async def _run():
            monitor = MagicMock()
            monitor.get_drive.return_value = None
            monitor._normalizer = MagicMock()
            state = MagicMock()
            state.drive_monitor_service = monitor
            req = _make_request(state)
            with pytest.raises(HTTPException) as exc_info:
                await refresh_drive(VALID_DRIVE_ID, req)
            assert exc_info.value.status_code == 404
        asyncio.run(_run())


# ── start_self_test ───────────────────────────────────────────────────────────

class TestStartSelfTest:
    def test_400_on_bad_drive_id(self):
        async def _run():
            from app.api.routes.drives import StartSelfTestRequest
            state = MagicMock()
            req = _make_request(state)
            body = StartSelfTestRequest(type=SelfTestType.SHORT)
            with pytest.raises(HTTPException) as exc_info:
                await start_self_test("bad-id!!", req, body)
            assert exc_info.value.status_code == 400
        asyncio.run(_run())

    def test_400_when_service_raises_value_error(self):
        async def _run():
            from app.api.routes.drives import StartSelfTestRequest
            svc = MagicMock()
            svc.start_test = AsyncMock(side_effect=ValueError("Drive does not support self-test"))
            state = MagicMock()
            state.drive_self_test_service = svc
            req = _make_request(state)
            body = StartSelfTestRequest(type=SelfTestType.SHORT)
            with pytest.raises(HTTPException) as exc_info:
                await start_self_test(VALID_DRIVE_ID, req, body)
            assert exc_info.value.status_code == 400
        asyncio.run(_run())

    def test_503_when_service_absent(self):
        async def _run():
            from app.api.routes.drives import StartSelfTestRequest
            state = MagicMock()
            state.drive_self_test_service = None
            req = _make_request(state)
            body = StartSelfTestRequest(type=SelfTestType.SHORT)
            with pytest.raises(HTTPException) as exc_info:
                await start_self_test(VALID_DRIVE_ID, req, body)
            assert exc_info.value.status_code == 503
        asyncio.run(_run())


# ── abort_self_test ───────────────────────────────────────────────────────────

class TestAbortSelfTest:
    def test_returns_success(self):
        async def _run():
            svc = MagicMock()
            svc.abort_test = AsyncMock(return_value=True)
            state = MagicMock()
            state.drive_self_test_service = svc
            req = _make_request(state)
            result = await abort_self_test(VALID_DRIVE_ID, VALID_RUN_ID, req)
            assert result == {"success": True}
        asyncio.run(_run())

    def test_409_when_abort_fails(self):
        async def _run():
            svc = MagicMock()
            svc.abort_test = AsyncMock(return_value=False)
            state = MagicMock()
            state.drive_self_test_service = svc
            req = _make_request(state)
            with pytest.raises(HTTPException) as exc_info:
                await abort_self_test(VALID_DRIVE_ID, VALID_RUN_ID, req)
            assert exc_info.value.status_code == 409
        asyncio.run(_run())

    def test_400_bad_run_id(self):
        async def _run():
            state = MagicMock()
            req = _make_request(state)
            with pytest.raises(HTTPException) as exc_info:
                await abort_self_test(VALID_DRIVE_ID, "bad_run_id!", req)
            assert exc_info.value.status_code == 400
        asyncio.run(_run())


# ── get_drive_history ─────────────────────────────────────────────────────────

class TestGetDriveHistory:
    def test_400_on_zero_hours(self):
        async def _run():
            state = MagicMock()
            req = _make_request(state)
            with pytest.raises(HTTPException) as exc_info:
                await get_drive_history(VALID_DRIVE_ID, req, hours=0)
            assert exc_info.value.status_code == 400
        asyncio.run(_run())

    def test_400_on_negative_hours(self):
        async def _run():
            state = MagicMock()
            req = _make_request(state)
            with pytest.raises(HTTPException) as exc_info:
                await get_drive_history(VALID_DRIVE_ID, req, hours=-1)
            assert exc_info.value.status_code == 400
        asyncio.run(_run())

    def test_400_on_excess_hours(self):
        async def _run():
            state = MagicMock()
            req = _make_request(state)
            with pytest.raises(HTTPException) as exc_info:
                await get_drive_history(VALID_DRIVE_ID, req, hours=9000)
            assert exc_info.value.status_code == 400
        asyncio.run(_run())

    def test_retention_limited_flag_set_when_query_exceeds_retention(self):
        async def _run():
            import app.api.routes.drives as drives_module

            settings_repo = MagicMock()
            settings_repo.get_int = AsyncMock(return_value=24)  # 24h retention

            drive_repo_mock = MagicMock()
            drive_repo_mock.get_health_history = AsyncMock(return_value=[])

            state = MagicMock()
            state.settings_repo = settings_repo

            req = _make_request(state)

            original = drives_module._get_repo
            drives_module._get_repo = lambda _r: drive_repo_mock
            try:
                result = await get_drive_history(VALID_DRIVE_ID, req, hours=168)
            finally:
                drives_module._get_repo = original

            # 24h retention < 168h requested → retention_limited must be True
            assert result["retention_limited"] is True
            assert result["drive_id"] == VALID_DRIVE_ID

        asyncio.run(_run())

    def test_retention_not_limited_when_query_within_retention(self):
        async def _run():
            import app.api.routes.drives as drives_module

            settings_repo = MagicMock()
            settings_repo.get_int = AsyncMock(return_value=720)  # 720h retention

            drive_repo_mock = MagicMock()
            drive_repo_mock.get_health_history = AsyncMock(
                return_value=[{"recorded_at": "2026-01-01T00:00:00", "temperature_c": 38}]
            )

            state = MagicMock()
            state.settings_repo = settings_repo

            req = _make_request(state)

            original = drives_module._get_repo
            drives_module._get_repo = lambda _r: drive_repo_mock
            try:
                result = await get_drive_history(VALID_DRIVE_ID, req, hours=24)
            finally:
                drives_module._get_repo = original

            # 720h retention >= 24h requested → retention_limited must be False
            assert result["retention_limited"] is False
            assert len(result["history"]) == 1

        asyncio.run(_run())

    def test_400_on_bad_drive_id_format(self):
        async def _run():
            state = MagicMock()
            req = _make_request(state)
            with pytest.raises(HTTPException) as exc_info:
                await get_drive_history("not-valid!!!", req, hours=24)
            assert exc_info.value.status_code == 400
        asyncio.run(_run())
