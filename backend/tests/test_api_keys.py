"""Tests for API key lifecycle and validation."""

from __future__ import annotations

import asyncio

import aiosqlite

from app.db.migration_runner import run_migrations
from app.services.auth_service import AuthService


class TestApiKeys:
    def test_create_validate_revoke(self, tmp_db) -> None:
        async def _run() -> None:
            await run_migrations(tmp_db)
            db = await aiosqlite.connect(str(tmp_db))
            svc = AuthService(db)

            meta, plaintext = await svc.create_api_key("Hub Agent")
            assert meta["name"] == "Hub Agent"
            assert plaintext.startswith("dc_live_")
            assert meta["revoked_at"] is None
            assert meta["scopes"] == ["read:sensors"]

            validated = await svc.validate_api_key(plaintext)
            assert validated is not None
            assert validated["id"] == meta["id"]
            assert validated["last_used_at"] is not None
            assert validated["scopes"] == ["read:sensors"]

            listed = await svc.list_api_keys()
            assert len(listed) == 1
            assert listed[0]["id"] == meta["id"]
            assert listed[0]["scopes"] == ["read:sensors"]

            revoked = await svc.revoke_api_key(meta["id"])
            assert revoked is True

            validated_after = await svc.validate_api_key(plaintext)
            assert validated_after is None

            await db.close()

        asyncio.run(_run())

    def test_create_rejects_unknown_scope(self, tmp_db) -> None:
        async def _run() -> None:
            await run_migrations(tmp_db)
            db = await aiosqlite.connect(str(tmp_db))
            svc = AuthService(db)

            try:
                await svc.create_api_key("Bad Scope", scopes=["root:all"])
                assert False, "Expected ValueError for invalid scope"
            except ValueError:
                pass

            await db.close()

        asyncio.run(_run())
