"""Targeted tests for security remediation regressions."""

from __future__ import annotations

import asyncio
from unittest.mock import patch

import aiosqlite
import httpx
import pytest

from app.db.migration_runner import run_migrations
from app.db.repositories.machine_repo import MachineRepo
from app.services.machine_monitor_service import MachineMonitorService
from app.services.webhook_service import WebhookService


class TestSecurityRegressions:
    def test_machine_update_preserves_api_key_when_omitted(self, tmp_db) -> None:
        async def _run() -> None:
            await run_migrations(tmp_db)
            db = await aiosqlite.connect(str(tmp_db))
            repo = MachineRepo(db)
            machine = await repo.create(
                name="Agent-01",
                base_url="http://127.0.0.1:9001",
                api_key="dc_live_old_key",
            )

            # object() matches the route sentinel contract: keep existing key.
            updated = await repo.update(machine["id"], name="Agent-01A", api_key=object())
            assert updated is not None
            assert updated["name"] == "Agent-01A"
            assert updated["api_key"] == "dc_live_old_key"
            await db.close()

        asyncio.run(_run())

    def test_machine_fetch_revalidates_url_before_request(self, tmp_db) -> None:
        async def _run() -> None:
            await run_migrations(tmp_db)
            db = await aiosqlite.connect(str(tmp_db))
            repo = MachineRepo(db)
            machine = await repo.create(
                name="Agent-02",
                base_url="https://example.test",
                api_key="dc_live_key",
            )

            calls = {"count": 0}

            def _handler(_request: httpx.Request) -> httpx.Response:
                calls["count"] += 1
                return httpx.Response(200, json={"status": "ok"})

            service = MachineMonitorService(repo)
            service._client = httpx.AsyncClient(transport=httpx.MockTransport(_handler))

            with patch(
                "app.services.machine_monitor_service.validate_outbound_url_at_request_time",
                return_value=(False, "Loopback targets are not allowed"),
            ):
                with pytest.raises(RuntimeError, match="URL blocked"):
                    await service._fetch_remote(machine)

            assert calls["count"] == 0
            await service._client.aclose()
            await db.close()

        asyncio.run(_run())

    def test_webhook_dispatch_revalidates_url_before_request(self, tmp_db) -> None:
        async def _run() -> None:
            await run_migrations(tmp_db)
            db = await aiosqlite.connect(str(tmp_db))
            svc = WebhookService(db)
            await svc.start()

            calls = {"count": 0}

            def _handler(_request: httpx.Request) -> httpx.Response:
                calls["count"] += 1
                return httpx.Response(204)

            svc._client = httpx.AsyncClient(transport=httpx.MockTransport(_handler))
            await svc.update_config(
                enabled=True,
                target_url="https://example.test/hook",
                signing_secret="secret",
                timeout_seconds=2.0,
                max_retries=0,
                retry_backoff_seconds=0.1,
            )

            with patch(
                "app.services.webhook_service.validate_outbound_url_at_request_time",
                return_value=(False, "Loopback targets are not allowed"),
            ):
                await svc.dispatch_alert_events([{
                    "rule_id": "r1",
                    "sensor_id": "cpu_0",
                    "sensor_name": "CPU",
                    "threshold": 80,
                    "actual_value": 90,
                    "timestamp": "2026-01-01T00:00:00Z",
                    "message": "hot",
                }])

            assert calls["count"] == 0
            await svc.stop()
            await db.close()

        asyncio.run(_run())
