"""Tests for API key scope enforcement in auth dependencies."""

from __future__ import annotations

import asyncio
from types import SimpleNamespace
from unittest.mock import patch

import aiosqlite
import pytest
from fastapi import HTTPException
from starlette.requests import Request

from app.api.dependencies.auth import require_auth, require_csrf
from app.db.migration_runner import run_migrations
from app.services.auth_service import AuthService


def _build_request(
    auth_service: AuthService,
    *,
    method: str,
    path: str,
    api_key: str | None = None,
) -> Request:
    headers: list[tuple[bytes, bytes]] = []
    if api_key:
        headers.append((b"authorization", f"Bearer {api_key}".encode("utf-8")))
    scope = {
        "type": "http",
        "http_version": "1.1",
        "method": method,
        "scheme": "http",
        "path": path,
        "raw_path": path.encode("utf-8"),
        "query_string": b"",
        "headers": headers,
        "client": ("127.0.0.1", 12345),
        "server": ("127.0.0.1", 8085),
        "app": SimpleNamespace(state=SimpleNamespace(auth_service=auth_service)),
    }

    async def _receive() -> dict:
        return {"type": "http.request", "body": b"", "more_body": False}

    return Request(scope, _receive)


class TestApiKeyScopeEnforcement:
    def test_read_scope_allows_get_sensor(self, tmp_db) -> None:
        async def _run() -> None:
            await run_migrations(tmp_db)
            db = await aiosqlite.connect(str(tmp_db))
            svc = AuthService(db)
            _, token = await svc.create_api_key("RO Sensors", scopes=["read:sensors"])
            req = _build_request(svc, method="GET", path="/api/sensors", api_key=token)

            with patch("app.api.dependencies.auth._auth_enabled", return_value=True):
                info = await require_auth(req, drivechill_session=None)
            assert info is not None
            assert info["auth_type"] == "api_key"

            await db.close()

        asyncio.run(_run())

    def test_read_scope_rejects_fan_write(self, tmp_db) -> None:
        async def _run() -> None:
            await run_migrations(tmp_db)
            db = await aiosqlite.connect(str(tmp_db))
            svc = AuthService(db)
            _, token = await svc.create_api_key("RO Sensors", scopes=["read:sensors"])
            req = _build_request(svc, method="POST", path="/api/fans/release", api_key=token)

            with patch("app.api.dependencies.auth._auth_enabled", return_value=True):
                with pytest.raises(HTTPException) as exc:
                    await require_auth(req, drivechill_session=None)
            assert exc.value.status_code == 403
            assert "write:fans" in str(exc.value.detail)

            await db.close()

        asyncio.run(_run())

    def test_write_scope_allows_mutating_endpoint(self, tmp_db) -> None:
        async def _run() -> None:
            await run_migrations(tmp_db)
            db = await aiosqlite.connect(str(tmp_db))
            svc = AuthService(db)
            _, token = await svc.create_api_key("Fan Writer", scopes=["write:fans"])
            req = _build_request(svc, method="POST", path="/api/fans/release", api_key=token)

            with patch("app.api.dependencies.auth._auth_enabled", return_value=True):
                info = await require_auth(req, drivechill_session=None)
            assert info is not None
            assert info["auth_type"] == "api_key"

            await db.close()

        asyncio.run(_run())

    def test_csrf_dependency_enforces_scope_when_auth_dep_absent(self, tmp_db) -> None:
        async def _run() -> None:
            await run_migrations(tmp_db)
            db = await aiosqlite.connect(str(tmp_db))
            svc = AuthService(db)
            _, token = await svc.create_api_key("Sensors", scopes=["read:sensors"])
            req = _build_request(svc, method="POST", path="/api/auth/logout", api_key=token)

            with patch("app.api.dependencies.auth._auth_enabled", return_value=True):
                with pytest.raises(HTTPException) as exc:
                    await require_csrf(req, drivechill_session=None, x_csrf_token=None)
            assert exc.value.status_code == 403

            await db.close()

        asyncio.run(_run())
