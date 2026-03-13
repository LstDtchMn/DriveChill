"""Tests for audit gaps G1, G2, G3, G5.

G1 — Internal request auth bypass (_is_internal_request)
G2 — Tray profile activation (_activate_profile)
G3 — seed_missing_presets idempotency
G5 — session_ttl_seconds edge cases (extends existing test_settings_ttl_validation)
"""

from __future__ import annotations

import asyncio
import json
from unittest.mock import MagicMock, patch

import aiosqlite
import pytest

from app.config import Settings
from app.db.migration_runner import run_migrations
from app.db.repositories.profile_repo import ProfileRepo


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------


class _FakeHeaders(dict):
    """Mimics Starlette's Headers (case-insensitive get)."""

    def get(self, key: str, default: str = "") -> str:
        for k, v in self.items():
            if k.lower() == key.lower():
                return v
        return default


class _FakeRequest:
    def __init__(self, headers: dict):
        self.headers = _FakeHeaders(headers)


async def _init_db(db_path) -> aiosqlite.Connection:
    await run_migrations(db_path)
    db = await aiosqlite.connect(str(db_path))
    await db.execute("PRAGMA foreign_keys=ON")
    return db


# ---------------------------------------------------------------------------
# G1 — _is_internal_request
# ---------------------------------------------------------------------------


class TestInternalRequestAuth:
    """G1: Verify internal token auth checks."""

    def test_valid_internal_token_returns_true(self):
        from app.api.dependencies.auth import _is_internal_request
        from app.config import settings

        req = _FakeRequest({"x-drivechill-internal": settings.internal_token})
        assert _is_internal_request(req) is True

    def test_missing_internal_token_returns_false(self):
        from app.api.dependencies.auth import _is_internal_request

        req = _FakeRequest({})
        assert _is_internal_request(req) is False

    def test_wrong_internal_token_returns_false(self):
        from app.api.dependencies.auth import _is_internal_request

        req = _FakeRequest({"x-drivechill-internal": "totally-wrong-token"})
        assert _is_internal_request(req) is False

    def test_empty_internal_token_returns_false(self):
        from app.api.dependencies.auth import _is_internal_request

        req = _FakeRequest({"x-drivechill-internal": ""})
        assert _is_internal_request(req) is False

    def test_spoofed_x_forwarded_for_does_not_bypass(self):
        """X-Forwarded-For must NOT grant internal access without the token."""
        from app.api.dependencies.auth import _is_internal_request

        req = _FakeRequest({
            "x-forwarded-for": "127.0.0.1",
        })
        assert _is_internal_request(req) is False

    def test_spoofed_x_forwarded_for_with_wrong_token(self):
        """Even with spoofed X-Forwarded-For, wrong token must fail."""
        from app.api.dependencies.auth import _is_internal_request

        req = _FakeRequest({
            "x-forwarded-for": "127.0.0.1",
            "x-drivechill-internal": "bad-token",
        })
        assert _is_internal_request(req) is False

    def test_internal_token_is_timing_safe(self):
        """Verify hmac.compare_digest is used (not ==)."""
        import inspect
        from app.api.dependencies.auth import _is_internal_request

        source = inspect.getsource(_is_internal_request)
        assert "hmac.compare_digest" in source

    def test_internal_token_not_env_overridable(self):
        """The internal token is generated per-process and cannot be set via env."""
        import os

        with patch.dict(os.environ, {"DRIVECHILL_INTERNAL_TOKEN": "injected"}):
            s = Settings()
            assert s.internal_token != "injected"
            assert len(s.internal_token) == 64  # token_hex(32) = 64 hex chars


# ---------------------------------------------------------------------------
# G2 — Tray _activate_profile
# ---------------------------------------------------------------------------


class TestTrayActivateProfile:
    """G2: Verify tray profile activation makes correct HTTP call."""

    def test_activate_profile_calls_correct_url_and_method(self):
        """_activate_profile should PUT to /api/profiles/{id}/activate with internal headers."""
        from app.config import settings

        captured_requests = []

        def fake_urlopen(req, timeout=5):
            captured_requests.append(req)
            # Return a mock response with status 200
            resp = MagicMock()
            resp.status = 200
            resp.__enter__ = MagicMock(return_value=resp)
            resp.__exit__ = MagicMock(return_value=False)
            return resp

        with patch("app.tray.urllib.request.urlopen", side_effect=fake_urlopen):
            # Import after patching to avoid module-level side effects
            from app.tray import _activate_profile
            _activate_profile("test-profile-123")

        assert len(captured_requests) >= 1
        req = captured_requests[0]
        assert req.get_method() == "PUT"
        assert "/api/profiles/test-profile-123/activate" in req.full_url
        assert req.get_header("X-drivechill-internal") == settings.internal_token
        assert req.get_header("Content-type") == "application/json"

    def test_activate_profile_url_encodes_id(self):
        """Profile IDs with special characters should be URL-encoded."""
        captured_requests = []

        def fake_urlopen(req, timeout=5):
            captured_requests.append(req)
            resp = MagicMock()
            resp.status = 200
            resp.__enter__ = MagicMock(return_value=resp)
            resp.__exit__ = MagicMock(return_value=False)
            return resp

        with patch("app.tray.urllib.request.urlopen", side_effect=fake_urlopen):
            from app.tray import _activate_profile
            _activate_profile("id/with/slashes")

        assert len(captured_requests) >= 1
        req = captured_requests[0]
        # Slashes in ID should be percent-encoded
        assert "id%2Fwith%2Fslashes" in req.full_url or "id/with/slashes" not in req.full_url.split("/api/profiles/")[1].split("/activate")[0]

    def test_activate_profile_handles_failure_gracefully(self):
        """Network errors should be caught and logged, not raised."""
        with patch("app.tray.urllib.request.urlopen", side_effect=ConnectionError("refused")):
            from app.tray import _activate_profile
            # Should not raise
            _activate_profile("nonexistent-profile")


# ---------------------------------------------------------------------------
# G3 — seed_missing_presets idempotency
# ---------------------------------------------------------------------------


class TestSeedMissingPresetsIdempotency:
    """G3: Calling seed_missing_presets twice must not create duplicates."""

    def test_double_seed_no_duplicates(self, tmp_db):
        async def _run():
            db = await _init_db(tmp_db)
            repo = ProfileRepo(db)

            # First call: seed from empty — should add all non-custom presets
            added_first = await repo.seed_missing_presets()
            assert added_first == 7  # silent, balanced, performance, full_speed, gaming, rendering, sleep

            # Second call: should add nothing
            added_second = await repo.seed_missing_presets()
            assert added_second == 0

            # Verify no duplicates
            cursor = await db.execute(
                "SELECT preset, COUNT(*) as cnt FROM profiles "
                "WHERE preset != 'custom' GROUP BY preset HAVING cnt > 1"
            )
            dupes = await cursor.fetchall()
            assert dupes == [], f"Duplicate presets found: {dupes}"

            # Total should be exactly 7
            cursor = await db.execute(
                "SELECT COUNT(*) FROM profiles WHERE preset != 'custom'"
            )
            row = await cursor.fetchone()
            assert row[0] == 7

            await db.close()

        asyncio.run(_run())

    def test_seed_missing_after_seed_defaults(self, tmp_db):
        """seed_missing_presets should not duplicate profiles created by seed_defaults."""
        async def _run():
            db = await _init_db(tmp_db)
            repo = ProfileRepo(db)

            # seed_defaults inserts all presets when table is empty
            await repo.seed_defaults()

            cursor = await db.execute("SELECT COUNT(*) FROM profiles")
            count_after_defaults = (await cursor.fetchone())[0]
            assert count_after_defaults == 7

            # seed_missing_presets should find them all and add nothing
            added = await repo.seed_missing_presets()
            assert added == 0

            cursor = await db.execute("SELECT COUNT(*) FROM profiles")
            count_after_missing = (await cursor.fetchone())[0]
            assert count_after_missing == count_after_defaults

            await db.close()

        asyncio.run(_run())

    def test_seed_missing_adds_only_new_presets(self, tmp_db):
        """If some presets exist, only the missing ones are added."""
        async def _run():
            db = await _init_db(tmp_db)
            repo = ProfileRepo(db)

            # Manually insert just 2 presets
            from datetime import datetime, timezone
            now = datetime.now(timezone.utc).isoformat()
            await db.execute(
                "INSERT INTO profiles (id, name, preset, is_active, created_at, updated_at) "
                "VALUES (?, ?, ?, 0, ?, ?)",
                ("p1", "Silent", "silent", now, now),
            )
            await db.execute(
                "INSERT INTO profiles (id, name, preset, is_active, created_at, updated_at) "
                "VALUES (?, ?, ?, 0, ?, ?)",
                ("p2", "Balanced", "balanced", now, now),
            )
            await db.commit()

            # Should add the remaining 5
            added = await repo.seed_missing_presets()
            assert added == 5

            # Second call adds nothing
            added2 = await repo.seed_missing_presets()
            assert added2 == 0

            # Total: 7
            cursor = await db.execute(
                "SELECT COUNT(*) FROM profiles WHERE preset != 'custom'"
            )
            assert (await cursor.fetchone())[0] == 7

            await db.close()

        asyncio.run(_run())


# ---------------------------------------------------------------------------
# G5 — session_ttl_seconds edge cases
# (extends test_settings_ttl_validation.py with additional edge cases)
# ---------------------------------------------------------------------------


class TestSessionTtlEdgeCases:
    """G5: Additional edge cases beyond the existing parametrized test."""

    def test_empty_string_returns_default(self):
        s = Settings(session_ttl="")
        # Empty string -> int("") -> ValueError -> 28800
        assert s.session_ttl_seconds == 28800

    def test_whitespace_only_returns_default(self):
        s = Settings(session_ttl="   ")
        assert s.session_ttl_seconds == 28800

    def test_raw_positive_integer_string(self):
        s = Settings(session_ttl="3600")
        assert s.session_ttl_seconds == 3600

    def test_raw_negative_integer_returns_default(self):
        s = Settings(session_ttl="-100")
        assert s.session_ttl_seconds == 28800

    def test_days_suffix(self):
        s = Settings(session_ttl="7d")
        assert s.session_ttl_seconds == 7 * 86400

    def test_hours_suffix(self):
        s = Settings(session_ttl="24h")
        assert s.session_ttl_seconds == 24 * 3600

    def test_minutes_suffix(self):
        s = Settings(session_ttl="30m")
        assert s.session_ttl_seconds == 30 * 60

    def test_zero_hours_returns_default(self):
        s = Settings(session_ttl="0h")
        assert s.session_ttl_seconds == 28800

    def test_negative_days_returns_default(self):
        s = Settings(session_ttl="-1d")
        assert s.session_ttl_seconds == 28800

    def test_non_numeric_returns_default(self):
        s = Settings(session_ttl="abc")
        assert s.session_ttl_seconds == 28800

    def test_float_like_string_returns_default(self):
        """'1.5h' should fail int() parsing and return default."""
        s = Settings(session_ttl="1.5h")
        assert s.session_ttl_seconds == 28800

    def test_mixed_units_returns_default(self):
        """'1h30m' is not supported — should fall back to default."""
        s = Settings(session_ttl="1h30m")
        assert s.session_ttl_seconds == 28800

    def test_uppercase_suffix_handled(self):
        """Parsing lowercases the string, so '8H' should work."""
        s = Settings(session_ttl="8H")
        assert s.session_ttl_seconds == 8 * 3600

    def test_very_large_value_accepted(self):
        s = Settings(session_ttl="365d")
        assert s.session_ttl_seconds == 365 * 86400

    def test_one_minute(self):
        s = Settings(session_ttl="1m")
        assert s.session_ttl_seconds == 60
