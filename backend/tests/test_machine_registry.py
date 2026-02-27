"""Tests for machine registry repository and monitor service."""

from __future__ import annotations

import asyncio
from pathlib import Path

import aiosqlite
import httpx

from app.db.migration_runner import run_migrations
from app.db.repositories.machine_repo import MachineRepo
from app.services.machine_monitor_service import MachineMonitorService


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
