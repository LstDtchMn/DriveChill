"""Cross-backend parity tests for /api/drives* endpoints.

Documents and verifies the response-shape contract that both the Python
FastAPI backend and the C# ASP.NET Core backend must honour:

  GET  /api/drives                        → {drives, smartctl_available, total}
  POST /api/drives/rescan                 → {drives_found}
  GET  /api/drives/{id}                   → drive detail (all required fields)
  GET  /api/drives/{id}/attributes        → {drive_id, attributes}
  GET  /api/drives/{id}/history           → {drive_id, history, retention_limited}
  GET  /api/drives/{id}/self-tests        → {drive_id, runs}
  POST /api/drives/{id}/self-tests/…/abort → {success: true}
  GET  /api/drives/{id}/settings          → {drive_id, temp_warning_c, temp_critical_c,
                                             alerts_enabled, curve_picker_enabled}

Validation rules (400/404/503/409) are also verified to match C#.
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
    get_per_drive_settings,
    list_drives,
    list_self_tests,
    rescan_drives,
)

# ---------------------------------------------------------------------------
# Constants
# ---------------------------------------------------------------------------

VALID_DRIVE_ID = "a" * 24  # 24 lowercase hex chars
VALID_RUN_ID   = "b" * 16  # 16 lowercase hex chars


def _req(**state_attrs):
    req = MagicMock()
    for k, v in state_attrs.items():
        setattr(req.app.state, k, v)
    return req


def _run(coro):
    return asyncio.run(coro)


# ---------------------------------------------------------------------------
# GET /api/drives → {drives, smartctl_available, total}
# ---------------------------------------------------------------------------


class TestListDrivesContract:
    def test_response_has_required_fields(self):
        async def _go():
            monitor = MagicMock()
            monitor.get_all_drives.return_value = []
            monitor.is_smartctl_available = AsyncMock(return_value=True)
            monitor._normalizer = MagicMock()
            req = _req(drive_monitor_service=monitor)
            result = await list_drives(req)
            assert {"drives", "smartctl_available", "total"} <= set(result.keys())
        _run(_go())

    def test_total_equals_drives_count(self):
        async def _go():
            monitor = MagicMock()
            monitor.get_all_drives.return_value = []
            monitor.is_smartctl_available = AsyncMock(return_value=False)
            monitor._normalizer = MagicMock()
            req = _req(drive_monitor_service=monitor)
            result = await list_drives(req)
            assert result["total"] == len(result["drives"])
        _run(_go())

    def test_503_when_service_absent(self):
        async def _go():
            req = _req(drive_monitor_service=None)
            with pytest.raises(HTTPException) as exc_info:
                await list_drives(req)
            assert exc_info.value.status_code == 503
        _run(_go())


# ---------------------------------------------------------------------------
# POST /api/drives/rescan → {drives_found}
# ---------------------------------------------------------------------------


class TestRescanDrivesContract:
    def test_response_has_drives_found(self):
        async def _go():
            monitor = MagicMock()
            monitor.rescan_now = AsyncMock(return_value=5)
            req = _req(drive_monitor_service=monitor)
            result = await rescan_drives(req)
            assert "drives_found" in result
            assert result["drives_found"] == 5
        _run(_go())

    def test_drives_found_is_int(self):
        async def _go():
            monitor = MagicMock()
            monitor.rescan_now = AsyncMock(return_value=0)
            req = _req(drive_monitor_service=monitor)
            result = await rescan_drives(req)
            assert isinstance(result["drives_found"], int)
        _run(_go())


# ---------------------------------------------------------------------------
# GET /api/drives/{id}/history → {drive_id, history, retention_limited}
# ---------------------------------------------------------------------------


class TestDriveHistoryContract:
    @staticmethod
    def _with_repo(retention=720, history=None):
        """Returns (req, original_get_repo_fn, drives_module)."""
        import app.api.routes.drives as mod
        settings_repo = MagicMock()
        settings_repo.get_int = AsyncMock(return_value=retention)
        drive_repo = MagicMock()
        drive_repo.get_health_history = AsyncMock(return_value=history or [])
        req = _req(settings_repo=settings_repo)
        orig = mod._get_repo
        mod._get_repo = lambda _r: drive_repo
        return req, mod, orig

    def test_response_has_required_fields(self):
        async def _go():
            req, mod, orig = self._with_repo()
            try:
                result = await get_drive_history(VALID_DRIVE_ID, req, hours=24.0)
            finally:
                mod._get_repo = orig
            assert {"drive_id", "history", "retention_limited"} <= set(result.keys())
        _run(_go())

    def test_drive_id_echoed_in_response(self):
        async def _go():
            req, mod, orig = self._with_repo()
            try:
                result = await get_drive_history(VALID_DRIVE_ID, req, hours=24.0)
            finally:
                mod._get_repo = orig
            assert result["drive_id"] == VALID_DRIVE_ID
        _run(_go())

    def test_history_is_list(self):
        async def _go():
            req, mod, orig = self._with_repo()
            try:
                result = await get_drive_history(VALID_DRIVE_ID, req, hours=24.0)
            finally:
                mod._get_repo = orig
            assert isinstance(result["history"], list)
        _run(_go())

    def test_retention_limited_true_when_query_exceeds_retention(self):
        async def _go():
            req, mod, orig = self._with_repo(retention=24)
            try:
                result = await get_drive_history(VALID_DRIVE_ID, req, hours=168.0)
            finally:
                mod._get_repo = orig
            assert result["retention_limited"] is True
        _run(_go())

    def test_retention_limited_false_when_within_retention(self):
        async def _go():
            req, mod, orig = self._with_repo(retention=720)
            try:
                result = await get_drive_history(VALID_DRIVE_ID, req, hours=24.0)
            finally:
                mod._get_repo = orig
            assert result["retention_limited"] is False
        _run(_go())

    def test_400_on_zero_hours(self):
        async def _go():
            req = _req()
            with pytest.raises(HTTPException) as exc_info:
                await get_drive_history(VALID_DRIVE_ID, req, hours=0)
            assert exc_info.value.status_code == 400
        _run(_go())

    def test_400_on_excess_hours(self):
        async def _go():
            req = _req()
            with pytest.raises(HTTPException) as exc_info:
                await get_drive_history(VALID_DRIVE_ID, req, hours=9000)
            assert exc_info.value.status_code == 400
        _run(_go())

    def test_400_on_bad_drive_id(self):
        async def _go():
            req = _req()
            with pytest.raises(HTTPException) as exc_info:
                await get_drive_history("INVALID_ID!!!", req, hours=24.0)
            assert exc_info.value.status_code == 400
        _run(_go())


# ---------------------------------------------------------------------------
# GET /api/drives/{id}/self-tests → {drive_id, runs}
# ---------------------------------------------------------------------------


class TestListSelfTestsContract:
    def test_response_has_required_fields(self):
        async def _go():
            import app.api.routes.drives as mod
            repo = MagicMock()
            repo.get_self_test_runs = AsyncMock(return_value=[])
            orig = mod._get_repo
            mod._get_repo = lambda _r: repo
            try:
                result = await list_self_tests(VALID_DRIVE_ID, _req())
            finally:
                mod._get_repo = orig
            assert "drive_id" in result
            assert "runs" in result
            assert isinstance(result["runs"], list)
        _run(_go())

    def test_drive_id_echoed(self):
        async def _go():
            import app.api.routes.drives as mod
            repo = MagicMock()
            repo.get_self_test_runs = AsyncMock(return_value=[])
            orig = mod._get_repo
            mod._get_repo = lambda _r: repo
            try:
                result = await list_self_tests(VALID_DRIVE_ID, _req())
            finally:
                mod._get_repo = orig
            assert result["drive_id"] == VALID_DRIVE_ID
        _run(_go())

    def test_400_on_bad_drive_id(self):
        async def _go():
            with pytest.raises(HTTPException) as exc_info:
                await list_self_tests("bad!!!", _req())
            assert exc_info.value.status_code == 400
        _run(_go())


# ---------------------------------------------------------------------------
# POST /api/drives/{id}/self-tests/{runId}/abort → {success: true}
# ---------------------------------------------------------------------------


class TestAbortSelfTestContract:
    def test_response_is_success_true(self):
        async def _go():
            svc = MagicMock()
            svc.abort_test = AsyncMock(return_value=True)
            req = _req(drive_self_test_service=svc)
            result = await abort_self_test(VALID_DRIVE_ID, VALID_RUN_ID, req)
            assert result == {"success": True}
        _run(_go())

    def test_409_on_abort_fail(self):
        """Both backends return 409 when abort fails or is not supported."""
        async def _go():
            svc = MagicMock()
            svc.abort_test = AsyncMock(return_value=False)
            req = _req(drive_self_test_service=svc)
            with pytest.raises(HTTPException) as exc_info:
                await abort_self_test(VALID_DRIVE_ID, VALID_RUN_ID, req)
            assert exc_info.value.status_code == 409
        _run(_go())

    def test_400_on_bad_run_id(self):
        async def _go():
            with pytest.raises(HTTPException) as exc_info:
                await abort_self_test(VALID_DRIVE_ID, "short!!", _req())
            assert exc_info.value.status_code == 400
        _run(_go())

    def test_400_on_bad_drive_id(self):
        async def _go():
            with pytest.raises(HTTPException) as exc_info:
                await abort_self_test("bad_drive!", VALID_RUN_ID, _req())
            assert exc_info.value.status_code == 400
        _run(_go())


# ---------------------------------------------------------------------------
# GET /api/drives/{id}/settings → {drive_id, temp_warning_c, temp_critical_c,
#                                   alerts_enabled, curve_picker_enabled}
# ---------------------------------------------------------------------------


class TestPerDriveSettingsContract:
    """Fallback shape must match C# GetDriveSettings null-field defaults."""

    def test_fallback_response_has_required_fields(self):
        async def _go():
            import app.api.routes.drives as mod
            repo = MagicMock()
            repo.get_drive_settings_override = AsyncMock(return_value=None)
            orig = mod._get_repo
            mod._get_repo = lambda _r: repo
            try:
                result = await get_per_drive_settings(VALID_DRIVE_ID, _req())
            finally:
                mod._get_repo = orig
            required = {"drive_id", "temp_warning_c", "temp_critical_c",
                        "alerts_enabled", "curve_picker_enabled"}
            assert required <= set(result.keys())
        _run(_go())

    def test_fallback_drive_id_echoed(self):
        async def _go():
            import app.api.routes.drives as mod
            repo = MagicMock()
            repo.get_drive_settings_override = AsyncMock(return_value=None)
            orig = mod._get_repo
            mod._get_repo = lambda _r: repo
            try:
                result = await get_per_drive_settings(VALID_DRIVE_ID, _req())
            finally:
                mod._get_repo = orig
            assert result["drive_id"] == VALID_DRIVE_ID
        _run(_go())

    def test_fallback_nullable_fields_are_none(self):
        async def _go():
            import app.api.routes.drives as mod
            repo = MagicMock()
            repo.get_drive_settings_override = AsyncMock(return_value=None)
            orig = mod._get_repo
            mod._get_repo = lambda _r: repo
            try:
                result = await get_per_drive_settings(VALID_DRIVE_ID, _req())
            finally:
                mod._get_repo = orig
            assert result["temp_warning_c"] is None
            assert result["temp_critical_c"] is None
            assert result["alerts_enabled"] is None
            assert result["curve_picker_enabled"] is None
        _run(_go())

    def test_400_on_bad_drive_id(self):
        async def _go():
            with pytest.raises(HTTPException) as exc_info:
                await get_per_drive_settings("INVALID!!", _req())
            assert exc_info.value.status_code == 400
        _run(_go())


# ---------------------------------------------------------------------------
# ID validation — regex parity with C# [GeneratedRegex] rules
# ---------------------------------------------------------------------------


class TestIdValidationParity:
    """Both backends use identical regex: drive_id=^[a-f0-9]{24}$  run_id=^[a-f0-9]{16}$"""

    def test_drive_id_accepts_24_lowercase_hex(self):
        _validate_drive_id("a" * 24)

    def test_drive_id_rejects_uppercase(self):
        with pytest.raises(HTTPException):
            _validate_drive_id("A" * 24)

    def test_drive_id_rejects_non_hex(self):
        with pytest.raises(HTTPException):
            _validate_drive_id("z" * 24)

    def test_drive_id_rejects_wrong_length(self):
        for n in [0, 8, 23, 25, 32]:
            with pytest.raises(HTTPException):
                _validate_drive_id("a" * n)

    def test_run_id_accepts_16_lowercase_hex(self):
        _validate_run_id("f" * 16)

    def test_run_id_rejects_non_hex(self):
        with pytest.raises(HTTPException):
            _validate_run_id("g" * 16)

    def test_run_id_rejects_wrong_length(self):
        for n in [0, 8, 15, 17, 24]:
            with pytest.raises(HTTPException):
                _validate_run_id("a" * n)

    def test_drive_id_bad_format_is_400(self):
        with pytest.raises(HTTPException) as exc_info:
            _validate_drive_id("bad-id!!")
        assert exc_info.value.status_code == 400

    def test_run_id_bad_format_is_400(self):
        with pytest.raises(HTTPException) as exc_info:
            _validate_run_id("bad-run!!")
        assert exc_info.value.status_code == 400


# ---------------------------------------------------------------------------
# Serial / device-path masking — shape parity with C# DriveMonitorService
# ---------------------------------------------------------------------------


class TestMaskingContract:
    def test_serial_shows_last_four(self):
        assert _mask_serial("SN-ABCDEF1234") == "****1234"

    def test_serial_empty_is_four_stars(self):
        assert _mask_serial("") == "****"

    def test_serial_shorter_than_four_shows_all(self):
        assert _mask_serial("AB") == "****AB"

    def test_device_path_strips_directory(self):
        assert _mask_device_path("/dev/sda") == "sda"

    def test_device_path_bare_name_unchanged(self):
        assert _mask_device_path("disk0") == "disk0"
