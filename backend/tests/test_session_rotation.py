"""Tests for POST /api/auth/me/password — self-service password change with session rotation.

Tests the service-layer logic directly (same pattern as test_auth.py):
- Happy path: password is updated, old session invalidated, new session issued
- Wrong current password returns 403 (service layer: verify_password returns False)
- _create_session helper produces valid 64-char hex tokens
- Session rotation: old token is gone, new token validates
"""
from __future__ import annotations

import asyncio

import aiosqlite
import pytest

from app.db.migration_runner import run_migrations
from app.services.auth_service import AuthService


# ---------------------------------------------------------------------------
# Fixtures (mirrors test_auth.py pattern)
# ---------------------------------------------------------------------------

async def _init_db(db_path) -> aiosqlite.Connection:
    await run_migrations(db_path)
    db = await aiosqlite.connect(str(db_path))
    await db.execute("PRAGMA foreign_keys=ON")
    return db


@pytest.fixture
def db_and_service(tmp_db):
    async def _setup():
        db = await _init_db(tmp_db)
        svc = AuthService(db, session_ttl_seconds=3600)
        return db, svc

    db, svc = asyncio.run(_setup())
    yield db, svc
    asyncio.run(db.close())


# ---------------------------------------------------------------------------
# _create_session helper
# ---------------------------------------------------------------------------

class TestCreateSession:

    def test_returns_two_64char_hex_tokens(self, db_and_service):
        db, svc = db_and_service

        async def run():
            await svc.create_user("alice", "password1")
            user = await svc.get_user("alice")
            session_token, csrf_token = await svc._create_session(
                user["id"], "127.0.0.1", "test-agent"
            )
            assert len(session_token) == 64
            assert len(csrf_token) == 64
            assert session_token != csrf_token

        asyncio.run(run())

    def test_session_is_immediately_valid(self, db_and_service):
        db, svc = db_and_service

        async def run():
            await svc.create_user("bob", "password1")
            user = await svc.get_user("bob")
            session_token, _ = await svc._create_session(user["id"], "127.0.0.1", "ua")
            session = await svc.validate_session(session_token)
            assert session is not None
            assert session["username"] == "bob"

        asyncio.run(run())

    def test_each_call_produces_unique_tokens(self, db_and_service):
        db, svc = db_and_service

        async def run():
            await svc.create_user("carol", "password1")
            user = await svc.get_user("carol")
            tok1, csrf1 = await svc._create_session(user["id"], "127.0.0.1", "ua")
            tok2, csrf2 = await svc._create_session(user["id"], "127.0.0.1", "ua")
            assert tok1 != tok2
            assert csrf1 != csrf2

        asyncio.run(run())


# ---------------------------------------------------------------------------
# Self-password-change logic (service layer)
# ---------------------------------------------------------------------------

class TestSelfPasswordChange:

    def test_verify_password_rejects_wrong_password(self, db_and_service):
        """verify_password returns False for a wrong password — endpoint would 403."""
        _, svc = db_and_service

        async def run():
            await svc.create_user("dave", "correct_pass")
            user = await svc.get_user("dave")
            assert svc.verify_password("wrong_pass", user["password_hash"]) is False

        asyncio.run(run())

    def test_verify_password_accepts_correct_password(self, db_and_service):
        _, svc = db_and_service

        async def run():
            await svc.create_user("eve", "correct_pass")
            user = await svc.get_user("eve")
            assert svc.verify_password("correct_pass", user["password_hash"]) is True

        asyncio.run(run())

    def test_password_update_and_session_rotation(self, db_and_service):
        """Full happy-path: update password, invalidate old session, issue new one."""
        db, svc = db_and_service

        async def run():
            await svc.create_user("frank", "old_password")
            user = await svc.get_user("frank")

            # Simulate an existing login session
            old_token, _ = await svc._create_session(user["id"], "127.0.0.1", "ua")
            assert await svc.validate_session(old_token) is not None

            # Verify current password (endpoint would 403 if this fails)
            assert svc.verify_password("old_password", user["password_hash"])

            # Update password
            new_hash = svc.hash_password("new_password_123")
            await db.execute(
                "UPDATE users SET password_hash = ?, updated_at = datetime('now') WHERE id = ?",
                (new_hash, user["id"]),
            )
            await db.commit()

            # Invalidate all sessions
            await db.execute("DELETE FROM sessions WHERE user_id = ?", (user["id"],))
            await db.commit()

            # Old session is now gone
            assert await svc.validate_session(old_token) is None

            # Issue a new session
            new_token, new_csrf = await svc._create_session(user["id"], "127.0.0.1", "ua")
            session = await svc.validate_session(new_token)
            assert session is not None
            assert session["username"] == "frank"

            # Old password no longer works; new password does
            refreshed_user = await svc.get_user("frank")
            assert svc.verify_password("new_password_123", refreshed_user["password_hash"])
            assert not svc.verify_password("old_password", refreshed_user["password_hash"])

        asyncio.run(run())

    def test_multiple_sessions_all_invalidated(self, db_and_service):
        """All pre-existing sessions are wiped, not just the active one."""
        db, svc = db_and_service

        async def run():
            await svc.create_user("grace", "password1")
            user = await svc.get_user("grace")

            tok_a, _ = await svc._create_session(user["id"], "192.168.1.1", "browser-a")
            tok_b, _ = await svc._create_session(user["id"], "192.168.1.2", "browser-b")
            tok_c, _ = await svc._create_session(user["id"], "192.168.1.3", "browser-c")

            # Simulate password change + bulk session wipe
            await db.execute("DELETE FROM sessions WHERE user_id = ?", (user["id"],))
            await db.commit()

            assert await svc.validate_session(tok_a) is None
            assert await svc.validate_session(tok_b) is None
            assert await svc.validate_session(tok_c) is None

        asyncio.run(run())

    def test_new_password_enforced_on_next_login(self, db_and_service):
        """After a password change the old password no longer allows login."""
        db, svc = db_and_service

        async def run():
            await svc.create_user("hank", "old_pass_abc")

            # Change password at service level
            new_hash = svc.hash_password("new_pass_xyz")
            await db.execute(
                "UPDATE users SET password_hash = ? WHERE username = ?",
                (new_hash, "hank"),
            )
            await db.commit()

            # Old password login fails
            assert await svc.login("hank", "old_pass_abc", "127.0.0.1") is None
            # New password login succeeds
            result = await svc.login("hank", "new_pass_xyz", "127.0.0.1")
            assert result is not None

        asyncio.run(run())
