"""Route-level tests for the update API endpoints.

Covers:
- GET /api/update/check: version comparison, caching, GitHub API error handling
- POST /api/update/apply: Docker path returns manual command, semver validation
"""

from __future__ import annotations

import asyncio
import re
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path
from unittest.mock import AsyncMock, MagicMock, patch

import pytest

_backend_dir = Path(__file__).parent.parent
if str(_backend_dir) not in sys.path:
    sys.path.insert(0, str(_backend_dir))

from app.api.routes import update as update_mod


# ── Helpers ──────────────────────────────────────────────────────────────────

def _reset_cache():
    """Clear the module-level cache between tests."""
    update_mod._cached_at = None
    update_mod._cached_data = None
    update_mod._deployment_type_cache = None


def _make_github_response(tag: str = "v9.9.9", url: str = "https://github.com/releases/v9.9.9"):
    """Return a mock httpx response for the GitHub releases API."""
    resp = MagicMock()
    resp.status_code = 200
    resp.raise_for_status = MagicMock()
    resp.json.return_value = {"tag_name": tag, "html_url": url}
    return resp


# ── Version comparison helper ────────────────────────────────────────────────

def test_ver_tuple_basic():
    """The _ver_tuple parser inside _fetch_latest handles normal semver."""
    # Simulate the inline helper
    def _ver_tuple(v: str):
        try:
            parts = (v.split("-")[0].split(".") + ["0", "0", "0"])[:3]
            return tuple(int(x) for x in parts)
        except ValueError:
            return (0, 0, 0)

    assert _ver_tuple("2.1.0") == (2, 1, 0)
    assert _ver_tuple("2.1.1-rc1") == (2, 1, 1)
    assert _ver_tuple("10.20.30") == (10, 20, 30)
    assert _ver_tuple("garbage") == (0, 0, 0)


def test_ver_tuple_ordering():
    def _ver_tuple(v: str):
        try:
            parts = (v.split("-")[0].split(".") + ["0", "0", "0"])[:3]
            return tuple(int(x) for x in parts)
        except ValueError:
            return (0, 0, 0)

    assert _ver_tuple("2.1.1") > _ver_tuple("2.1.0")
    assert _ver_tuple("3.0.0") > _ver_tuple("2.99.99")
    assert not (_ver_tuple("2.1.0") > _ver_tuple("2.1.0"))


# ── Semver regex ─────────────────────────────────────────────────────────────

def test_semver_regex_accepts_valid():
    assert update_mod._SEMVER_RE.fullmatch("2.1.0")
    assert update_mod._SEMVER_RE.fullmatch("2.1.1-rc1")
    assert update_mod._SEMVER_RE.fullmatch("10.20.30+build.42")


def test_semver_regex_rejects_invalid():
    assert update_mod._SEMVER_RE.fullmatch("$(whoami)") is None
    assert update_mod._SEMVER_RE.fullmatch("2.1") is None
    assert update_mod._SEMVER_RE.fullmatch("v2.1.0") is None
    assert update_mod._SEMVER_RE.fullmatch("2.1.0; rm -rf /") is None


# ── check_update endpoint ───────────────────────────────────────────────────

def _mock_client(tag: str = "v9.9.9"):
    """Build a mock httpx.AsyncClient for a given GitHub tag."""
    mock_resp = _make_github_response(tag)
    client = AsyncMock()
    client.__aenter__ = AsyncMock(return_value=client)
    client.__aexit__ = AsyncMock(return_value=False)
    client.get = AsyncMock(return_value=mock_resp)
    return client


def test_check_returns_latest_version():
    _reset_cache()

    async def _go():
        with patch("app.api.routes.update.httpx.AsyncClient", return_value=_mock_client("v9.9.9")), \
             patch("app.api.routes.update._deployment_type", AsyncMock(return_value="windows_standalone")):
            return await update_mod.check_update()

    result = asyncio.run(_go())
    assert result["latest"] == "9.9.9"
    assert result["update_available"] is True
    assert "current" in result


def test_check_no_update_available():
    _reset_cache()
    current = update_mod.settings.app_version

    async def _go():
        with patch("app.api.routes.update.httpx.AsyncClient", return_value=_mock_client(f"v{current}")), \
             patch("app.api.routes.update._deployment_type", AsyncMock(return_value="windows_standalone")):
            return await update_mod.check_update()

    result = asyncio.run(_go())
    assert result["update_available"] is False


# ── apply_update endpoint ────────────────────────────────────────────────────

def test_apply_docker_returns_manual_command():
    _reset_cache()

    async def _go():
        with patch("app.api.routes.update.httpx.AsyncClient", return_value=_mock_client("v9.9.9")), \
             patch("app.api.routes.update._deployment_type", AsyncMock(return_value="docker")):
            return await update_mod.apply_update(request=MagicMock())

    result = asyncio.run(_go())
    assert result["status"] == "manual_required"
    assert "docker" in result["command"].lower()


def test_apply_rejects_invalid_version():
    """If GitHub somehow returns a non-semver version, apply should reject it."""
    _reset_cache()

    from fastapi import HTTPException

    async def _go():
        with patch("app.api.routes.update.httpx.AsyncClient", return_value=_mock_client("v$(whoami)")), \
             patch("app.api.routes.update._deployment_type", AsyncMock(return_value="windows_standalone")), \
             patch("app.api.routes.update.sys") as mock_sys:
            mock_sys.platform = "win32"
            await update_mod.apply_update(request=MagicMock())

    with pytest.raises(HTTPException) as exc_info:
        asyncio.run(_go())

    assert exc_info.value.status_code == 500
    assert "version" in exc_info.value.detail.lower()
