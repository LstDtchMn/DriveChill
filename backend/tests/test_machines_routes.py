"""Tests for machine registry route handlers."""
from __future__ import annotations

import asyncio
from unittest.mock import AsyncMock, MagicMock, patch

import pytest


def _req(**state_attrs):
    req = MagicMock()
    for k, v in state_attrs.items():
        setattr(req.app.state, k, v)
    return req


def _run(coro):
    return asyncio.run(coro)


def _machine_row(id: str = "m1", **overrides) -> dict:
    row = {
        "id": id,
        "name": "TestMachine",
        "base_url": "https://192.168.1.50:8085",
        "api_key": None,
        "api_key_id": None,
        "has_api_key": False,
        "enabled": True,
        "poll_interval_seconds": 2.0,
        "timeout_ms": 1200,
        "status": "online",
        "last_seen_at": "2026-03-12T10:00:00+00:00",
        "last_error": None,
        "consecutive_failures": 0,
        "created_at": "2026-03-12T09:00:00+00:00",
        "updated_at": "2026-03-12T10:00:00+00:00",
        "capabilities_json": "[]",
        "last_command_at": None,
    }
    row.update(overrides)
    return row


# ---------------------------------------------------------------------------
# Pydantic model validation
# ---------------------------------------------------------------------------


class TestCreateMachineValidation:
    def test_rejects_ftp_scheme(self):
        from app.api.routes.machines import CreateMachineRequest
        with pytest.raises(Exception):
            CreateMachineRequest(name="Bad", base_url="ftp://evil.com")

    def test_rejects_file_scheme(self):
        from app.api.routes.machines import CreateMachineRequest
        with pytest.raises(Exception):
            CreateMachineRequest(name="Bad", base_url="file:///etc/passwd")

    def test_accepts_https(self):
        from app.api.routes.machines import CreateMachineRequest
        m = CreateMachineRequest(name="Good", base_url="https://example.com:8085")
        assert m.base_url == "https://example.com:8085"

    def test_accepts_http(self):
        from app.api.routes.machines import CreateMachineRequest
        m = CreateMachineRequest(name="Good", base_url="http://192.168.1.50:8085")
        assert m.base_url == "http://192.168.1.50:8085"

    def test_strips_trailing_slash(self):
        from app.api.routes.machines import CreateMachineRequest
        m = CreateMachineRequest(name="T", base_url="https://example.com/")
        assert not m.base_url.endswith("/")

    def test_rejects_empty_name(self):
        from app.api.routes.machines import CreateMachineRequest
        with pytest.raises(Exception):
            CreateMachineRequest(name="", base_url="https://example.com")

    def test_poll_interval_bounds(self):
        from app.api.routes.machines import CreateMachineRequest
        with pytest.raises(Exception):
            CreateMachineRequest(name="T", base_url="https://x.com", poll_interval_seconds=0.1)
        with pytest.raises(Exception):
            CreateMachineRequest(name="T", base_url="https://x.com", poll_interval_seconds=60.0)


class TestUpdateMachineValidation:
    def test_rejects_invalid_scheme(self):
        from app.api.routes.machines import UpdateMachineRequest
        with pytest.raises(Exception):
            UpdateMachineRequest(base_url="ftp://evil.com")

    def test_none_base_url_allowed(self):
        from app.api.routes.machines import UpdateMachineRequest
        m = UpdateMachineRequest(name="Updated")
        assert m.base_url is None


# ---------------------------------------------------------------------------
# List machines
# ---------------------------------------------------------------------------


class TestListMachines:
    def test_returns_empty_list(self):
        from app.api.routes.machines import list_machines

        repo = AsyncMock()
        repo.list_all = AsyncMock(return_value=[])
        monitor = MagicMock()
        monitor.get_snapshot = MagicMock(return_value=None)
        req = _req(machine_repo=repo, machine_monitor_service=monitor)
        result = _run(list_machines(req))
        assert result["machines"] == []

    def test_returns_decorated_machines(self):
        from app.api.routes.machines import list_machines

        repo = AsyncMock()
        repo.list_all = AsyncMock(return_value=[_machine_row()])
        monitor = MagicMock()
        monitor.get_snapshot = MagicMock(return_value={
            "timestamp": "2026-03-12T10:00:00+00:00",
        })
        req = _req(machine_repo=repo, machine_monitor_service=monitor)
        result = _run(list_machines(req))
        assert len(result["machines"]) == 1
        m = result["machines"][0]
        assert "api_key" not in m  # redacted
        assert "has_api_key" in m


# ---------------------------------------------------------------------------
# Create machine
# ---------------------------------------------------------------------------


class TestCreateMachine:
    @patch("app.api.routes.machines.validate_outbound_url_async", new_callable=AsyncMock)
    def test_creates_machine(self, mock_validate):
        from app.api.routes.machines import CreateMachineRequest, create_machine

        mock_validate.return_value = (True, None)
        repo = AsyncMock()
        repo.create = AsyncMock(return_value=_machine_row(id="new-id"))
        req = _req(machine_repo=repo)
        body = CreateMachineRequest(name="New", base_url="https://192.168.1.50:8085")
        result = _run(create_machine(body, req))
        assert "machine" in result
        repo.create.assert_called_once()

    @patch("app.api.routes.machines.validate_outbound_url_async", new_callable=AsyncMock)
    def test_rejects_ssrf(self, mock_validate):
        from app.api.routes.machines import CreateMachineRequest, create_machine
        from fastapi import HTTPException

        mock_validate.return_value = (False, "Private IP not allowed")
        repo = AsyncMock()
        req = _req(machine_repo=repo)
        body = CreateMachineRequest(name="Evil", base_url="http://169.254.169.254")
        with pytest.raises(HTTPException) as exc_info:
            _run(create_machine(body, req))
        assert exc_info.value.status_code == 422


# ---------------------------------------------------------------------------
# Delete machine
# ---------------------------------------------------------------------------


class TestDeleteMachine:
    def test_deletes_existing(self):
        from app.api.routes.machines import delete_machine

        repo = AsyncMock()
        repo.delete = AsyncMock(return_value=True)
        monitor = MagicMock()
        monitor.forget_machine = MagicMock()
        req = _req(machine_repo=repo, machine_monitor_service=monitor)
        result = _run(delete_machine("m1", req))
        assert result["success"] is True
        monitor.forget_machine.assert_called_once_with("m1")

    def test_returns_404_for_unknown(self):
        from app.api.routes.machines import delete_machine
        from fastapi import HTTPException

        repo = AsyncMock()
        repo.delete = AsyncMock(return_value=False)
        monitor = MagicMock()
        req = _req(machine_repo=repo, machine_monitor_service=monitor)
        with pytest.raises(HTTPException) as exc_info:
            _run(delete_machine("nonexistent", req))
        assert exc_info.value.status_code == 404


# ---------------------------------------------------------------------------
# Safe ID regex
# ---------------------------------------------------------------------------


class TestSafeIdRegex:
    def test_valid_ids(self):
        from app.api.routes.machines import _SAFE_ID_RE
        for id_ in ["cpu-fan-1", "profile_abc", "ABC123", "a" * 128]:
            assert _SAFE_ID_RE.match(id_), f"Should match: {id_}"

    def test_rejects_path_traversal(self):
        from app.api.routes.machines import _SAFE_ID_RE
        for id_ in ["../../admin", "../etc", "a/b", "a b", ""]:
            assert not _SAFE_ID_RE.match(id_), f"Should reject: {id_}"

    def test_rejects_too_long(self):
        from app.api.routes.machines import _SAFE_ID_RE
        assert not _SAFE_ID_RE.match("a" * 129)
