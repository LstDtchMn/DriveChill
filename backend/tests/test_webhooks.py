"""Tests for webhook configuration and dispatch logging."""

from __future__ import annotations

import asyncio
import hashlib
import hmac as hmac_mod
import json
from unittest.mock import patch

import aiosqlite
import httpx

from app.db.migration_runner import run_migrations
from app.services.webhook_service import WebhookService


class TestWebhooks:
    def test_update_config_and_dispatch(self, tmp_db) -> None:
        async def _run() -> None:
            await run_migrations(tmp_db)
            db = await aiosqlite.connect(str(tmp_db))
            svc = WebhookService(db)
            await svc.start()

            payloads: list[dict] = []
            raw_bodies: list[bytes] = []
            signatures: list[str | None] = []
            signed_timestamps: list[str | None] = []
            nonces: list[str | None] = []

            def _handler(request: httpx.Request) -> httpx.Response:
                raw_bodies.append(bytes(request.content))
                payloads.append(json.loads(request.content.decode("utf-8")))
                signatures.append(request.headers.get("X-DriveChill-Signature"))
                signed_timestamps.append(request.headers.get("X-DriveChill-Timestamp"))
                nonces.append(request.headers.get("X-DriveChill-Nonce"))
                return httpx.Response(204)

            svc._client = httpx.AsyncClient(transport=httpx.MockTransport(_handler))

            cfg = await svc.update_config(
                enabled=True,
                target_url="https://user:pass@example.test/hook?token=abc",
                signing_secret="secret",
                timeout_seconds=2.0,
                max_retries=1,
                retry_backoff_seconds=0.1,
            )
            assert cfg["enabled"] is True
            assert cfg["target_url"] == "https://user:pass@example.test/hook?token=abc"
            assert cfg["has_signing_secret"] is True
            assert "signing_secret" not in cfg

            # Patch DNS-based URL validation — test uses a mock transport
            # so example.test does not need to resolve.
            with patch(
                "app.services.webhook_service.validate_outbound_url_at_request_time",
                return_value=(True, None),
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

            assert len(payloads) == 1
            assert payloads[0]["event_type"] == "alert_triggered"
            assert signatures[0] and signatures[0].startswith("sha256=")
            assert signed_timestamps[0] is not None
            assert nonces[0] is not None
            assert nonces[0] and len(nonces[0]) == 32
            assert int(signed_timestamps[0]) > 0
            # Independently verify the HMAC digest value
            signing_input = (
                f"{signed_timestamps[0]}.{nonces[0]}.".encode("utf-8") + raw_bodies[0]
            )
            expected_hmac = hmac_mod.new(
                b"secret", signing_input, digestmod=hashlib.sha256
            ).hexdigest()
            assert signatures[0] == f"sha256={expected_hmac}"

            logs = await svc.get_delivery_log(limit=10)
            assert len(logs) == 1
            assert logs[0]["success"] is True
            assert logs[0]["target_url"] == "https://example.test/hook"

            await svc.stop()
            await db.close()

        asyncio.run(_run())

    def test_delivery_log_supports_offset(self, tmp_db) -> None:
        async def _run() -> None:
            await run_migrations(tmp_db)
            db = await aiosqlite.connect(str(tmp_db))
            svc = WebhookService(db)
            await svc.start()

            await svc.update_config(
                enabled=True,
                target_url="https://example.test/hook",
                signing_secret=None,
                timeout_seconds=2.0,
                max_retries=0,
                retry_backoff_seconds=0.1,
            )

            with patch(
                "app.services.webhook_service.validate_outbound_url_at_request_time",
                return_value=(True, None),
            ):
                for i in range(3):
                    await svc.dispatch_alert_events([{
                        "rule_id": f"r{i}",
                        "sensor_id": "cpu_0",
                        "sensor_name": "CPU",
                        "threshold": 80,
                        "actual_value": 90 + i,
                        "timestamp": "2026-01-01T00:00:00Z",
                        "message": "hot",
                    }])

            page1 = await svc.get_delivery_log(limit=2, offset=0)
            page2 = await svc.get_delivery_log(limit=2, offset=2)

            assert len(page1) == 2
            assert len(page2) == 1
            assert page1[0]["timestamp"] >= page1[1]["timestamp"]
            assert page1[1]["timestamp"] >= page2[0]["timestamp"]

            await svc.stop()
            await db.close()

        asyncio.run(_run())
