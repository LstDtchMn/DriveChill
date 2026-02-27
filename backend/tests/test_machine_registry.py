"""Tests for machine registry repository and monitor service."""

from __future__ import annotations

import asyncio
from pathlib import Path

import aiosqlite
import httpx

from app.db.migration_runner import run_migrations
from app.db.repositories.machine_repo import MachineRepo
from app.services.machine_monitor_service import MachineMonitorService, _redact_error


async def _setup_repo(db_path: Path) -> tuple[aiosqlite.Connection, MachineRepo]:
    await run_migrations(db_path)
    db = await aiosqlite.connect(str(db_path))
    repo = MachineRepo(db)
    return db, repo


class TestMachineRepo:

    def test_create_and_get_machine(self, tmp_db: Path) -> None:
        async def _run() -> None:
            db, repo = await _setup_repo(tmp_db)
            machine = await repo.create(
                name="Server-01",
                base_url="http://127.0.0.1:9001/",
                enabled=True,
            )
            loaded = await repo.get(machine["id"])
            assert loaded is not None
            assert loaded["name"] == "Server-01"
            assert loaded["base_url"] == "http://127.0.0.1:9001"
            assert loaded["enabled"] is True
            await db.close()
        asyncio.run(_run())

    def test_update_and_delete_machine(self, tmp_db: Path) -> None:
        async def _run() -> None:
            db, repo = await _setup_repo(tmp_db)
            machine = await repo.create(
                name="Server-01",
                base_url="http://127.0.0.1:9001",
                enabled=True,
            )
            updated = await repo.update(
                machine["id"],
                name="Server-01A",
                enabled=False,
                timeout_ms=2000,
            )
            assert updated is not None
            assert updated["name"] == "Server-01A"
            assert updated["enabled"] is False
            assert updated["timeout_ms"] == 2000
            assert await repo.delete(machine["id"]) is True
            assert await repo.get(machine["id"]) is None
            await db.close()
        asyncio.run(_run())


class TestMachineMonitorService:

    def test_success_poll_sets_online_and_snapshot(self, tmp_db: Path) -> None:
        async def _run() -> None:
            db, repo = await _setup_repo(tmp_db)
            machine = await repo.create(
                name="Server-01",
                base_url="http://127.0.0.1:9001",
                poll_interval_seconds=0.5,
            )

            async def fetcher(_machine: dict) -> dict:
                return {
                    "timestamp": "2026-02-26T12:00:00+00:00",
                    "health": {"status": "ok", "backend": "Mock"},
                    "summary": {"cpu_temp": 55.0, "gpu_temp": 50.0, "case_temp": 32.0, "fan_count": 4, "backend": "Mock"},
                }

            service = MachineMonitorService(repo, fetcher=fetcher)
            await service.poll_once()

            loaded = await repo.get(machine["id"])
            assert loaded is not None
            assert loaded["status"] == "online"
            assert loaded["consecutive_failures"] == 0

            snapshot = service.get_snapshot(machine["id"])
            assert snapshot is not None
            assert snapshot["summary"]["cpu_temp"] == 55.0
            await db.close()
        asyncio.run(_run())

    def test_three_failures_mark_offline(self, tmp_db: Path) -> None:
        async def _run() -> None:
            db, repo = await _setup_repo(tmp_db)
            machine = await repo.create(
                name="Server-02",
                base_url="http://127.0.0.1:9002",
                poll_interval_seconds=0.5,
            )

            async def failing_fetcher(_machine: dict) -> dict:
                raise RuntimeError("connection refused")

            service = MachineMonitorService(repo, fetcher=failing_fetcher)
            for _ in range(3):
                await service.poll_once()
                await asyncio.sleep(0.55)

            loaded = await repo.get(machine["id"])
            assert loaded is not None
            assert loaded["status"] == "offline"
            assert loaded["consecutive_failures"] >= 3
            assert loaded["last_error"] is not None
            await db.close()
        asyncio.run(_run())

    def test_auth_error_status_on_401(self, tmp_db: Path) -> None:
        async def _run() -> None:
            db, repo = await _setup_repo(tmp_db)
            machine = await repo.create(
                name="SecuredAgent",
                base_url="http://127.0.0.1:9003",
                poll_interval_seconds=0.5,
            )

            async def failing_fetcher(_machine: dict) -> dict:
                req = httpx.Request("GET", "http://example.test/api/health")
                resp = httpx.Response(401, request=req)
                raise httpx.HTTPStatusError("Unauthorized", request=req, response=resp)

            service = MachineMonitorService(repo, fetcher=failing_fetcher)
            await service.poll_once()

            loaded = await repo.get(machine["id"])
            assert loaded is not None
            assert loaded["status"] == "auth_error"
            await db.close()

        asyncio.run(_run())

    def test_version_mismatch_status(self, tmp_db: Path) -> None:
        async def _run() -> None:
            db, repo = await _setup_repo(tmp_db)
            machine = await repo.create(
                name="OldAgent",
                base_url="http://127.0.0.1:9004",
                poll_interval_seconds=0.5,
            )

            async def fetcher(_machine: dict) -> dict:
                return {
                    "timestamp": "2026-02-26T12:00:00+00:00",
                    "health": {"status": "ok", "api_version": "v2", "backend": "Mock"},
                    "summary": {"cpu_temp": 55.0, "gpu_temp": 50.0, "case_temp": 32.0, "fan_count": 4, "backend": "Mock"},
                }

            service = MachineMonitorService(repo, fetcher=fetcher)
            await service.poll_once()

            loaded = await repo.get(machine["id"])
            assert loaded is not None
            assert loaded["status"] == "version_mismatch"
            await db.close()

        asyncio.run(_run())

    def test_verify_machine_success(self, tmp_db: Path) -> None:
        """verify_machine one-shot returns success and stores snapshot."""
        async def _run() -> None:
            db, repo = await _setup_repo(tmp_db)
            machine = await repo.create(
                name="VerifyTarget",
                base_url="http://127.0.0.1:9005",
            )

            async def fetcher(_machine: dict) -> dict:
                return {
                    "timestamp": "2026-02-27T10:00:00+00:00",
                    "health": {"status": "ok", "api_version": "v1", "backend": "Mock"},
                    "summary": {"cpu_temp": 42.0},
                }

            service = MachineMonitorService(repo, fetcher=fetcher)
            result = await service.verify_machine(machine)

            assert result["success"] is True
            assert result["status"] == "online"
            assert "snapshot" in result

            loaded = await repo.get(machine["id"])
            assert loaded is not None
            assert loaded["status"] == "online"
            assert loaded["consecutive_failures"] == 0

            snap = service.get_snapshot(machine["id"])
            assert snap is not None
            assert snap["summary"]["cpu_temp"] == 42.0
            await db.close()

        asyncio.run(_run())

    def test_verify_machine_auth_error(self, tmp_db: Path) -> None:
        """verify_machine returns auth_error on 401."""
        async def _run() -> None:
            db, repo = await _setup_repo(tmp_db)
            machine = await repo.create(
                name="LockedAgent",
                base_url="http://127.0.0.1:9006",
            )

            async def failing_fetcher(_machine: dict) -> dict:
                req = httpx.Request("GET", "http://example.test/api/health")
                resp = httpx.Response(401, request=req)
                raise httpx.HTTPStatusError("Unauthorized", request=req, response=resp)

            service = MachineMonitorService(repo, fetcher=failing_fetcher)
            result = await service.verify_machine(machine)

            assert result["success"] is False
            assert result["status"] == "auth_error"
            assert "error" in result

            loaded = await repo.get(machine["id"])
            assert loaded is not None
            assert loaded["status"] == "auth_error"
            assert loaded["consecutive_failures"] == 1
            await db.close()

        asyncio.run(_run())

    def test_verify_machine_version_mismatch(self, tmp_db: Path) -> None:
        """verify_machine detects API version mismatch."""
        async def _run() -> None:
            db, repo = await _setup_repo(tmp_db)
            machine = await repo.create(
                name="OldVerify",
                base_url="http://127.0.0.1:9007",
            )

            async def fetcher(_machine: dict) -> dict:
                return {
                    "timestamp": "2026-02-27T10:00:00+00:00",
                    "health": {"api_version": "v3"},
                    "summary": {},
                }

            service = MachineMonitorService(repo, fetcher=fetcher)
            result = await service.verify_machine(machine)

            assert result["success"] is False
            assert result["status"] == "version_mismatch"
            await db.close()

        asyncio.run(_run())

    def test_backoff_blocks_rapid_polls_after_auth_error(self, tmp_db: Path) -> None:
        """After auth_error, machine is blocked from polling until backoff expires."""
        async def _run() -> None:
            db, repo = await _setup_repo(tmp_db)
            machine = await repo.create(
                name="BackoffAgent",
                base_url="http://127.0.0.1:9008",
                poll_interval_seconds=0.1,  # very short interval
            )

            fetch_calls: list[dict] = []

            async def failing_fetcher(_machine: dict) -> dict:
                fetch_calls.append(_machine)
                req = httpx.Request("GET", "http://example.test/api/health")
                resp = httpx.Response(401, request=req)
                raise httpx.HTTPStatusError("Unauthorized", request=req, response=resp)

            service = MachineMonitorService(repo, fetcher=failing_fetcher)

            # First poll — should fetch and fail
            await service.poll_once()
            assert len(fetch_calls) == 1

            # Immediate second poll — should be blocked by backoff (2s default)
            await asyncio.sleep(0.15)  # just past poll_interval
            await service.poll_once()
            assert len(fetch_calls) == 1  # still 1 — blocked by backoff

            await db.close()

        asyncio.run(_run())

    def test_backoff_resets_on_success(self, tmp_db: Path) -> None:
        """After a successful poll, backoff resets and machine can be polled immediately."""
        async def _run() -> None:
            db, repo = await _setup_repo(tmp_db)
            machine = await repo.create(
                name="RecoverAgent",
                base_url="http://127.0.0.1:9009",
                poll_interval_seconds=0.1,
            )

            should_fail = [True]

            async def fetcher(_machine: dict) -> dict:
                if should_fail[0]:
                    req = httpx.Request("GET", "http://example.test/api/health")
                    resp = httpx.Response(401, request=req)
                    raise httpx.HTTPStatusError("Unauthorized", request=req, response=resp)
                return {
                    "timestamp": "2026-02-27T10:00:00+00:00",
                    "health": {"status": "ok", "api_version": "v1"},
                    "summary": {},
                }

            service = MachineMonitorService(repo, fetcher=fetcher)

            # First poll fails — sets backoff
            await service.poll_once()
            loaded = await repo.get(machine["id"])
            assert loaded is not None
            assert loaded["status"] == "auth_error"

            # Fix the issue and wait for backoff to expire
            should_fail[0] = False
            await asyncio.sleep(2.1)  # default initial backoff is 2s
            await service.poll_once()

            loaded = await repo.get(machine["id"])
            assert loaded is not None
            assert loaded["status"] == "online"
            assert loaded["consecutive_failures"] == 0

            # Backoff should be reset — next poll can happen at normal interval
            assert service._backoff_seconds.get(machine["id"], 2.0) == 2.0
            assert service._next_allowed_poll.get(machine["id"], 0.0) == 0.0
            await db.close()

        asyncio.run(_run())


class TestMachineMonitorRedaction:
    def test_redact_error_strips_url_userinfo(self) -> None:
        exc = RuntimeError("request failed: https://user:secret@example.test/api/health")
        redacted = _redact_error(exc)
        assert "secret@" not in redacted
        assert "://[REDACTED]@" in redacted
