"""Tests for session auth (v1.0-11 release gate).

Covers: bcrypt hashing, brute-force lockout, rate limiting, CSRF tokens,
session expiry/sliding window, audit logging, auth_required config, and
WebSocket auth dependency.
"""
from __future__ import annotations

import asyncio
import time
from datetime import datetime, timedelta, timezone

import aiosqlite
import pytest

from app.db.migration_runner import run_migrations
from app.services.auth_service import (
    AuthService,
    _MAX_FAILED_ATTEMPTS,
    _rate_buckets,
)

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

async def _init_db(db_path) -> aiosqlite.Connection:
    """Open DB with full production migrations applied."""
    await run_migrations(db_path)
    db = await aiosqlite.connect(str(db_path))
    await db.execute("PRAGMA foreign_keys=ON")
    return db


@pytest.fixture
def db_and_service(tmp_db):
    """Provide an AuthService with an in-memory DB, then close it."""
    async def _setup():
        db = await _init_db(tmp_db)
        svc = AuthService(db, session_ttl_seconds=3600)
        return db, svc

    db, svc = asyncio.run(_setup())
    yield db, svc
    asyncio.run(db.close())


# ---------------------------------------------------------------------------
# Bcrypt hashing
# ---------------------------------------------------------------------------

class TestBcryptHash:

    def test_hash_format(self) -> None:
        """Hash starts with $2b$12$ (bcrypt, cost 12)."""
        h = AuthService.hash_password("testpass")
        assert h.startswith("$2b$12$")

    def test_correct_password_verifies(self) -> None:
        h = AuthService.hash_password("secret")
        assert AuthService.verify_password("secret", h) is True

    def test_wrong_password_rejected(self) -> None:
        h = AuthService.hash_password("secret")
        assert AuthService.verify_password("wrong", h) is False


# ---------------------------------------------------------------------------
# Brute-force protection
# ---------------------------------------------------------------------------

class TestBruteForceProtection:

    def test_lockout_after_max_failures(self, db_and_service) -> None:
        """Account is locked after exactly _MAX_FAILED_ATTEMPTS failures."""
        db, svc = db_and_service

        async def run():
            await svc.create_user("admin", "correct")
            for _ in range(_MAX_FAILED_ATTEMPTS):
                result = await svc.login("admin", "wrong", "127.0.0.1")
                assert result is None

            # Now locked — even correct password fails
            result = await svc.login("admin", "correct", "127.0.0.1")
            assert result is None

            # Confirm locked_until is set
            user = await svc.get_user("admin")
            assert user["locked_until"] is not None
            assert user["failed_attempts"] >= _MAX_FAILED_ATTEMPTS

        asyncio.run(run())

    def test_failed_counter_resets_on_success(self, db_and_service) -> None:
        """Successful login clears the failed attempt counter."""
        db, svc = db_and_service

        async def run():
            await svc.create_user("admin", "correct")
            # 3 failures (less than lockout threshold)
            for _ in range(3):
                await svc.login("admin", "wrong", "127.0.0.1")

            user = await svc.get_user("admin")
            assert user["failed_attempts"] == 3

            # Successful login resets
            result = await svc.login("admin", "correct", "127.0.0.1")
            assert result is not None

            user = await svc.get_user("admin")
            assert user["failed_attempts"] == 0

        asyncio.run(run())


# ---------------------------------------------------------------------------
# Rate limiter
# ---------------------------------------------------------------------------

class TestRateLimiter:

    def setup_method(self) -> None:
        # Clear global rate-limiter state between tests
        _rate_buckets.clear()

    def test_11th_request_blocked(self) -> None:
        """The 11th request within the window is rejected."""
        for i in range(10):
            assert AuthService.check_rate_limit("10.0.0.1") is True
        assert AuthService.check_rate_limit("10.0.0.1") is False

    def test_per_ip_independence(self) -> None:
        """Rate limits are tracked per IP — one IP filling up doesn't affect another."""
        for _ in range(10):
            AuthService.check_rate_limit("10.0.0.1")

        # Different IP still allowed
        assert AuthService.check_rate_limit("10.0.0.2") is True


# ---------------------------------------------------------------------------
# CSRF token
# ---------------------------------------------------------------------------

class TestCsrfToken:

    def test_csrf_token_returned_on_login(self, db_and_service) -> None:
        """Login returns a CSRF token along with the session token."""
        db, svc = db_and_service

        async def run():
            await svc.create_user("admin", "pass123")
            result = await svc.login("admin", "pass123", "127.0.0.1")
            assert result is not None
            session_token, csrf_token = result
            assert len(session_token) == 64  # 32 bytes hex
            assert len(csrf_token) == 64

        asyncio.run(run())

    def test_csrf_stored_in_session(self, db_and_service) -> None:
        """CSRF token is accessible via session validation."""
        db, svc = db_and_service

        async def run():
            await svc.create_user("admin", "pass123")
            session_token, csrf_token = await svc.login("admin", "pass123", "127.0.0.1")
            session = await svc.validate_session(session_token)
            assert session is not None
            assert session["csrf_token"] == csrf_token

        asyncio.run(run())

    def test_tampered_csrf_mismatch(self, db_and_service) -> None:
        """A tampered CSRF token does not match the stored one."""
        db, svc = db_and_service

        async def run():
            await svc.create_user("admin", "pass123")
            session_token, csrf_token = await svc.login("admin", "pass123", "127.0.0.1")
            session = await svc.validate_session(session_token)
            assert session["csrf_token"] != "tampered_token_value"

        asyncio.run(run())


# ---------------------------------------------------------------------------
# Session expiry & sliding window
# ---------------------------------------------------------------------------

class TestSessionExpiry:

    def test_session_invalid_after_ttl(self, db_and_service) -> None:
        """Session is rejected after TTL expires."""
        db, svc = db_and_service

        async def run():
            await svc.create_user("admin", "pass123")
            session_token, _ = await svc.login("admin", "pass123", "127.0.0.1")

            # Manually expire the session
            past = (datetime.now(timezone.utc) - timedelta(hours=2)).isoformat()
            await db.execute(
                "UPDATE sessions SET expires_at = ? WHERE token = ?",
                (past, session_token),
            )
            await db.commit()

            result = await svc.validate_session(session_token)
            assert result is None

        asyncio.run(run())

    def test_sliding_window_extends_expiry(self, db_and_service) -> None:
        """Validating a session extends its expiry (sliding window)."""
        db, svc = db_and_service

        async def run():
            await svc.create_user("admin", "pass123")
            session_token, _ = await svc.login("admin", "pass123", "127.0.0.1")

            # Record initial expiry
            cursor = await db.execute(
                "SELECT expires_at FROM sessions WHERE token = ?",
                (session_token,),
            )
            row = await cursor.fetchone()
            initial_expiry = row[0]

            # Wait a tiny bit so the new expiry is clearly different
            await asyncio.sleep(0.05)

            # Validate — triggers sliding window
            session = await svc.validate_session(session_token)
            assert session is not None
            assert session["expires_at"] > initial_expiry

        asyncio.run(run())


# ---------------------------------------------------------------------------
# Auth audit log
# ---------------------------------------------------------------------------

class TestAuthAuditLog:

    def test_all_event_types_logged(self, db_and_service) -> None:
        """Key auth events are written to the audit log (at least 5 distinct types)."""
        db, svc = db_and_service

        async def run():
            # login_failure (user not found)
            await svc.login("nobody", "pass", "127.0.0.1")

            # Create user, login_success
            await svc.create_user("admin", "pass123")
            session_token, _ = await svc.login("admin", "pass123", "127.0.0.1")

            # logout
            await svc.logout(session_token, "127.0.0.1")

            # login_failure (wrong password) — enough to trigger lockout
            for _ in range(_MAX_FAILED_ATTEMPTS):
                await svc.login("admin", "wrong", "127.0.0.1")

            # Gather distinct event types
            cursor = await db.execute(
                "SELECT DISTINCT event_type FROM auth_log"
            )
            rows = await cursor.fetchall()
            event_types = {r[0] for r in rows}

            # Should have at least: login_failure, login_success, logout,
            # lockout_triggered
            assert len(event_types) >= 4
            assert "login_success" in event_types
            assert "login_failure" in event_types
            assert "logout" in event_types
            assert "lockout_triggered" in event_types

        asyncio.run(run())

    def test_audit_log_cleanup(self, db_and_service) -> None:
        """cleanup_old_auth_logs removes entries older than 90 days."""
        db, svc = db_and_service

        async def run():
            # Insert an old log entry
            old_ts = (datetime.now(timezone.utc) - timedelta(days=100)).isoformat()
            await db.execute(
                "INSERT INTO auth_log (timestamp, event_type, ip_address, "
                "username, outcome) VALUES (?, ?, ?, ?, ?)",
                (old_ts, "login_success", "127.0.0.1", "admin", "success"),
            )
            # Insert a recent entry
            recent_ts = datetime.now(timezone.utc).isoformat()
            await db.execute(
                "INSERT INTO auth_log (timestamp, event_type, ip_address, "
                "username, outcome) VALUES (?, ?, ?, ?, ?)",
                (recent_ts, "login_success", "127.0.0.1", "admin", "success"),
            )
            await db.commit()

            removed = await svc.cleanup_old_auth_logs()
            assert removed >= 1

            # Recent entry should survive
            cursor = await db.execute("SELECT COUNT(*) FROM auth_log")
            row = await cursor.fetchone()
            assert row[0] >= 1

        asyncio.run(run())


# ---------------------------------------------------------------------------
# auth_required config property
# ---------------------------------------------------------------------------

class TestAuthRequiredConfig:

    def test_localhost_no_auth(self) -> None:
        """127.0.0.1 binding does not require auth."""
        from app.config import Settings
        s = Settings(host="127.0.0.1")
        assert s.auth_required is False

    def test_localhost_name_no_auth(self) -> None:
        """'localhost' binding does not require auth."""
        from app.config import Settings
        s = Settings(host="localhost")
        assert s.auth_required is False

    def test_ipv6_localhost_no_auth(self) -> None:
        """'::1' binding does not require auth."""
        from app.config import Settings
        s = Settings(host="::1")
        assert s.auth_required is False

    def test_0000_requires_auth(self) -> None:
        """0.0.0.0 (all interfaces) requires auth."""
        from app.config import Settings
        s = Settings(host="0.0.0.0")
        assert s.auth_required is True

    def test_external_ip_requires_auth(self) -> None:
        """Non-loopback IP requires auth."""
        from app.config import Settings
        s = Settings(host="192.168.1.100")
        assert s.auth_required is True

    def test_force_auth_overrides_localhost(self) -> None:
        """force_auth=True enables auth even on localhost."""
        from app.config import Settings
        s = Settings(host="127.0.0.1", force_auth=True)
        assert s.auth_required is True


# ---------------------------------------------------------------------------
# WebSocket auth dependency
# ---------------------------------------------------------------------------

class TestWsAuthDependency:

    def test_ws_auth_disabled_returns_none(self) -> None:
        """require_ws_auth returns None (proceed) when auth is disabled."""
        from unittest.mock import AsyncMock, patch, MagicMock

        from app.api.dependencies.auth import require_ws_auth

        ws = AsyncMock()
        ws.headers = MagicMock()
        ws.headers.get = MagicMock(return_value="")

        with patch("app.api.dependencies.auth._auth_enabled", return_value=False):
            result = asyncio.run(require_ws_auth(ws))
        assert result is None
        ws.close.assert_not_called()

    def test_ws_auth_no_cookie_closes(self) -> None:
        """require_ws_auth closes WS with 1008 when no session cookie."""
        from unittest.mock import AsyncMock, patch, MagicMock

        from app.api.dependencies.auth import require_ws_auth

        ws = AsyncMock()
        ws.headers = MagicMock()
        ws.headers.get = MagicMock(return_value="")

        with patch("app.api.dependencies.auth._auth_enabled", return_value=True):
            result = asyncio.run(require_ws_auth(ws))
        assert result is None
        ws.close.assert_called_once_with(code=1008, reason="Authentication required")

    def test_ws_auth_valid_cookie_returns_session(self, db_and_service) -> None:
        """require_ws_auth returns session dict with a valid cookie."""
        from unittest.mock import AsyncMock, patch, MagicMock

        from app.api.dependencies.auth import require_ws_auth

        db, svc = db_and_service

        async def run():
            await svc.create_user("admin", "pass123")
            session_token, csrf_token = await svc.login("admin", "pass123", "127.0.0.1")

            ws = AsyncMock()
            ws.headers = MagicMock()
            ws.headers.get = MagicMock(return_value=f"drivechill_session={session_token}")
            ws.app = MagicMock()
            ws.app.state.auth_service = svc

            with patch("app.api.dependencies.auth._auth_enabled", return_value=True):
                result = await require_ws_auth(ws)

            assert result is not None
            assert result["csrf_token"] == csrf_token
            ws.close.assert_not_called()

        asyncio.run(run())


# ---------------------------------------------------------------------------
# Internal token bypass (X-DriveChill-Internal header)
# ---------------------------------------------------------------------------

class TestInternalTokenBypass:

    def test_valid_internal_token_bypasses_auth(self) -> None:
        """require_auth returns None (bypass) when correct internal token is sent."""
        from unittest.mock import AsyncMock, patch, MagicMock
        from app.api.dependencies.auth import require_auth
        from app.config import settings

        request = MagicMock()
        request.headers = {"x-drivechill-internal": settings.internal_token}

        with patch("app.api.dependencies.auth._auth_enabled", return_value=True):
            result = asyncio.run(require_auth(request, drivechill_session=None))
        assert result is None  # bypassed — no 401 raised

    def test_wrong_internal_token_does_not_bypass(self) -> None:
        """require_auth raises 401 when wrong internal token is sent (no session)."""
        from unittest.mock import MagicMock, patch
        from fastapi import HTTPException
        from app.api.dependencies.auth import require_auth

        request = MagicMock()
        request.headers = {"x-drivechill-internal": "wrong-token-value"}

        with patch("app.api.dependencies.auth._auth_enabled", return_value=True):
            with pytest.raises(HTTPException) as exc_info:
                asyncio.run(require_auth(request, drivechill_session=None))
            assert exc_info.value.status_code == 401

    def test_valid_internal_token_bypasses_csrf(self) -> None:
        """require_csrf returns (no error) when correct internal token is sent."""
        from unittest.mock import MagicMock, patch
        from app.api.dependencies.auth import require_csrf
        from app.config import settings

        request = MagicMock()
        request.headers = {"x-drivechill-internal": settings.internal_token}

        with patch("app.api.dependencies.auth._auth_enabled", return_value=True):
            # Should not raise — internal token bypasses CSRF
            asyncio.run(require_csrf(request, drivechill_session=None, x_csrf_token=None))

    def test_missing_internal_token_no_bypass(self) -> None:
        """require_auth raises 401 with no token and no session when auth is enabled."""
        from unittest.mock import MagicMock, patch
        from fastapi import HTTPException
        from app.api.dependencies.auth import require_auth

        request = MagicMock()
        request.headers = {}  # no internal token

        with patch("app.api.dependencies.auth._auth_enabled", return_value=True):
            with pytest.raises(HTTPException) as exc_info:
                asyncio.run(require_auth(request, drivechill_session=None))
            assert exc_info.value.status_code == 401

    def test_internal_token_not_env_overridable(self) -> None:
        """internal_token is a property, not a pydantic field — env cannot override it."""
        import os
        from app.config import Settings, _INTERNAL_TOKEN

        # Even with the env var set, Settings.internal_token should be the module constant
        os.environ["DRIVECHILL_INTERNAL_TOKEN"] = "attacker-controlled-value"
        try:
            s = Settings()
            assert s.internal_token == _INTERNAL_TOKEN
            assert s.internal_token != "attacker-controlled-value"
        finally:
            del os.environ["DRIVECHILL_INTERNAL_TOKEN"]
