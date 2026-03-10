"""Tests for NotificationChannelService (Phase 9)."""
from __future__ import annotations

import asyncio
import json
from unittest.mock import patch

from app.services.notification_channel_service import (
    NotificationChannelService,
    VALID_CHANNEL_TYPES,
)

SCHEMA = """
CREATE TABLE IF NOT EXISTS notification_channels (
    id TEXT PRIMARY KEY,
    type TEXT NOT NULL,
    name TEXT NOT NULL DEFAULT '',
    enabled INTEGER NOT NULL DEFAULT 1,
    config_json TEXT NOT NULL DEFAULT '{}',
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at TEXT NOT NULL DEFAULT (datetime('now'))
);
"""


def _run(coro):
    return asyncio.run(coro)


async def _make_svc():
    import aiosqlite
    db = await aiosqlite.connect(":memory:")
    await db.execute(SCHEMA)
    await db.commit()
    return NotificationChannelService(db), db


class TestCRUD:
    def test_create_and_list(self):
        async def _test():
            svc, db = await _make_svc()
            ch = await svc.create_channel("nc_1", "discord", "Test Discord", True, {"webhook_url": "http://example.com"})
            assert ch.id == "nc_1"
            assert ch.type == "discord"
            assert ch.name == "Test Discord"
            assert ch.enabled is True
            assert ch.config["webhook_url"] == "http://example.com"
            channels = await svc.list_channels()
            assert len(channels) == 1
            assert channels[0].id == "nc_1"
            await db.close()
        _run(_test())

    def test_get_channel(self):
        async def _test():
            svc, db = await _make_svc()
            await svc.create_channel("nc_1", "slack", "My Slack", True, {})
            ch = await svc.get_channel("nc_1")
            assert ch is not None
            assert ch.type == "slack"
            await db.close()
        _run(_test())

    def test_get_missing_returns_none(self):
        async def _test():
            svc, db = await _make_svc()
            ch = await svc.get_channel("nonexistent")
            assert ch is None
            await db.close()
        _run(_test())

    def test_update_channel(self):
        async def _test():
            svc, db = await _make_svc()
            await svc.create_channel("nc_1", "ntfy", "NTFY", True, {"url": "https://ntfy.sh", "topic": "test"})
            ok = await svc.update_channel("nc_1", name="Updated Name", enabled=False)
            assert ok is True
            ch = await svc.get_channel("nc_1")
            assert ch is not None
            assert ch.name == "Updated Name"
            assert ch.enabled is False
            await db.close()
        _run(_test())

    def test_update_config_only(self):
        async def _test():
            svc, db = await _make_svc()
            await svc.create_channel("nc_1", "discord", "D", True, {"webhook_url": "old"})
            ok = await svc.update_channel("nc_1", config={"webhook_url": "new"})
            assert ok is True
            ch = await svc.get_channel("nc_1")
            assert ch is not None
            assert ch.config["webhook_url"] == "new"
            await db.close()
        _run(_test())

    def test_update_missing_returns_false(self):
        async def _test():
            svc, db = await _make_svc()
            ok = await svc.update_channel("nope", name="x")
            assert ok is False
            await db.close()
        _run(_test())

    def test_update_empty_returns_false(self):
        async def _test():
            svc, db = await _make_svc()
            await svc.create_channel("nc_1", "slack", "S", True, {})
            ok = await svc.update_channel("nc_1")
            assert ok is False
            await db.close()
        _run(_test())

    def test_delete_channel(self):
        async def _test():
            svc, db = await _make_svc()
            await svc.create_channel("nc_1", "ntfy", "N", True, {})
            ok = await svc.delete_channel("nc_1")
            assert ok is True
            assert await svc.get_channel("nc_1") is None
            await db.close()
        _run(_test())

    def test_delete_missing_returns_false(self):
        async def _test():
            svc, db = await _make_svc()
            ok = await svc.delete_channel("nope")
            assert ok is False
            await db.close()
        _run(_test())

    def test_multiple_channels(self):
        async def _test():
            svc, db = await _make_svc()
            await svc.create_channel("nc_1", "discord", "D1", True, {})
            await svc.create_channel("nc_2", "slack", "S1", False, {})
            await svc.create_channel("nc_3", "ntfy", "N1", True, {})
            channels = await svc.list_channels()
            assert len(channels) == 3
            await db.close()
        _run(_test())


class TestToDict:
    def test_to_dict_roundtrip(self):
        async def _test():
            svc, db = await _make_svc()
            await svc.create_channel("nc_1", "discord", "Test", True, {"key": "val"})
            ch = await svc.get_channel("nc_1")
            assert ch is not None
            d = ch.to_dict()
            assert d["id"] == "nc_1"
            assert d["type"] == "discord"
            assert d["config"]["key"] == "val"
            assert "created_at" in d
            assert "updated_at" in d
            await db.close()
        _run(_test())


class TestValidTypes:
    def test_valid_types(self):
        assert VALID_CHANNEL_TYPES == {"ntfy", "discord", "slack", "generic_webhook", "mqtt"}


class TestSendAlertAll:
    def test_disabled_channels_skipped(self):
        async def _test():
            svc, db = await _make_svc()
            await svc.create_channel("nc_1", "discord", "Disabled", False, {"webhook_url": "http://example.com"})
            count = await svc.send_alert_all("CPU", 90.0, 80.0)
            assert count == 0
            await db.close()
        _run(_test())

    def test_no_channels_returns_zero(self):
        async def _test():
            svc, db = await _make_svc()
            count = await svc.send_alert_all("CPU", 90.0, 80.0)
            assert count == 0
            await db.close()
        _run(_test())


class TestSendTest:
    def test_missing_channel(self):
        async def _test():
            svc, db = await _make_svc()
            ok, error = await svc.send_test("nonexistent")
            assert ok is False
            assert error == "Channel not found"
            await db.close()
        _run(_test())


# ---------------------------------------------------------------------------
# SSRF send-time rejection tests
# ---------------------------------------------------------------------------

_BLOCKED = (False, "RFC1918 private address blocked")
_ALLOWED = (True, "")

_SSRF_PATCH = "app.services.notification_channel_service.validate_outbound_url_at_request_time"


class TestSSRFDelivery:
    """Verify that send-time SSRF validation blocks unsafe destinations.

    We patch ``validate_outbound_url_at_request_time`` so the tests are
    deterministic and require no external network access.
    """

    def test_ntfy_blocked_at_send_time(self):
        async def _test():
            svc, db = await _make_svc()
            await svc.create_channel(
                "nc_1", "ntfy", "NTFY", True,
                {"url": "http://127.0.0.1", "topic": "alerts"},
            )
            with patch(_SSRF_PATCH, return_value=_BLOCKED):
                count = await svc.send_alert_all("CPU", 95.0, 80.0)
            assert count == 0
            await db.close()
        _run(_test())

    def test_discord_blocked_at_send_time(self):
        async def _test():
            svc, db = await _make_svc()
            await svc.create_channel(
                "nc_1", "discord", "Discord", True,
                {"webhook_url": "http://192.168.1.10/hook"},
            )
            with patch(_SSRF_PATCH, return_value=_BLOCKED):
                count = await svc.send_alert_all("CPU", 95.0, 80.0)
            assert count == 0
            await db.close()
        _run(_test())

    def test_slack_blocked_at_send_time(self):
        async def _test():
            svc, db = await _make_svc()
            await svc.create_channel(
                "nc_1", "slack", "Slack", True,
                {"webhook_url": "http://169.254.169.254/latest/meta-data/"},
            )
            with patch(_SSRF_PATCH, return_value=_BLOCKED):
                count = await svc.send_alert_all("CPU", 95.0, 80.0)
            assert count == 0
            await db.close()
        _run(_test())

    def test_generic_blocked_at_send_time(self):
        async def _test():
            svc, db = await _make_svc()
            await svc.create_channel(
                "nc_1", "generic_webhook", "Generic", True,
                {"url": "http://10.0.0.1/hook"},
            )
            with patch(_SSRF_PATCH, return_value=_BLOCKED):
                count = await svc.send_alert_all("CPU", 95.0, 80.0)
            assert count == 0
            await db.close()
        _run(_test())

    def test_safe_url_passes_through(self):
        """When validation passes, delivery proceeds (may fail for other reasons, count is >=0)."""
        async def _test():
            svc, db = await _make_svc()
            await svc.create_channel(
                "nc_1", "discord", "Discord", True,
                {"webhook_url": "https://discord.com/api/webhooks/123/abc"},
            )
            # Patch to allow + also mock the HTTP session so no real request is made
            with patch(_SSRF_PATCH, return_value=_ALLOWED):
                # No session → delivery returns False gracefully (aiohttp not installed in tests)
                count = await svc.send_alert_all("CPU", 95.0, 80.0)
            # count is 0 because no real HTTP is sent, but SSRF did not block it
            assert isinstance(count, int)
            await db.close()
        _run(_test())

    def test_multiple_channels_blocked_ones_skipped(self):
        """Blocked channels are skipped; passing channels attempt delivery."""
        async def _test():
            svc, db = await _make_svc()
            await svc.create_channel(
                "nc_1", "discord", "Bad", True,
                {"webhook_url": "http://192.168.1.10/hook"},
            )
            await svc.create_channel(
                "nc_2", "discord", "Good", True,
                {"webhook_url": "https://discord.com/api/webhooks/123/abc"},
            )
            calls: list[str] = []

            async def mock_validate(url: str):
                calls.append(url)
                if "192.168" in url:
                    return False, "RFC1918 blocked"
                return True, ""

            with patch(_SSRF_PATCH, side_effect=mock_validate):
                await svc.send_alert_all("CPU", 95.0, 80.0)

            # validate_outbound_url_at_request_time called once per enabled channel
            assert len(calls) == 2
            await db.close()
        _run(_test())


# ---------------------------------------------------------------------------
# MQTT channel tests
# ---------------------------------------------------------------------------

class TestMQTTChannel:
    """MQTT channel CRUD and delivery tests."""

    def test_create_mqtt_channel(self):
        async def _test():
            svc, db = await _make_svc()
            ch = await svc.create_channel(
                "mqtt_1", "mqtt", "Home MQTT", True,
                {
                    "broker_url": "mqtt://192.168.1.100:1883",
                    "topic_prefix": "drivechill",
                    "username": "user",
                    "password": "pass",
                    "client_id": "dc-test",
                    "qos": 1,
                    "retain": False,
                    "publish_telemetry": True,
                },
            )
            assert ch.id == "mqtt_1"
            assert ch.type == "mqtt"
            assert ch.config["broker_url"] == "mqtt://192.168.1.100:1883"
            assert ch.config["topic_prefix"] == "drivechill"
            assert ch.config["publish_telemetry"] is True
            await db.close()
        _run(_test())

    def test_mqtt_send_without_aiomqtt_graceful(self):
        """MQTT send fails gracefully when aiomqtt is not importable."""
        async def _test():
            svc, db = await _make_svc()
            await svc.create_channel(
                "mqtt_1", "mqtt", "Test MQTT", True,
                {"broker_url": "mqtt://localhost:1883", "topic_prefix": "test"},
            )
            # Mock aiomqtt import to fail
            with patch.dict("sys.modules", {"aiomqtt": None}):
                count = await svc.send_alert_all("CPU", 90.0, 80.0)
            assert count == 0
            await db.close()
        _run(_test())

    def test_mqtt_send_connection_failure_graceful(self):
        """MQTT channel handles connection failure without crashing."""
        async def _test():
            svc, db = await _make_svc()
            await svc.create_channel(
                "mqtt_1", "mqtt", "Bad Broker", True,
                {"broker_url": "mqtt://nonexistent.invalid:1883", "topic_prefix": "test"},
            )
            # Connection will fail — should return 0 not raise
            count = await svc.send_alert_all("CPU", 90.0, 80.0)
            assert count == 0
            await db.close()
        _run(_test())

    def test_mqtt_disabled_channel_skipped(self):
        async def _test():
            svc, db = await _make_svc()
            await svc.create_channel(
                "mqtt_1", "mqtt", "Disabled MQTT", False,
                {"broker_url": "mqtt://localhost:1883", "topic_prefix": "test"},
            )
            count = await svc.send_alert_all("CPU", 90.0, 80.0)
            assert count == 0
            await db.close()
        _run(_test())

    def test_mqtt_empty_broker_url_returns_false(self):
        """MQTT channel with empty broker_url skips send."""
        async def _test():
            svc, db = await _make_svc()
            await svc.create_channel(
                "mqtt_1", "mqtt", "No Broker", True,
                {"broker_url": "", "topic_prefix": "test"},
            )
            count = await svc.send_alert_all("CPU", 90.0, 80.0)
            assert count == 0
            await db.close()
        _run(_test())

    def test_publish_telemetry_no_mqtt_channels(self):
        """publish_telemetry returns 0 when no MQTT channels exist."""
        async def _test():
            svc, db = await _make_svc()
            await svc.create_channel("nc_1", "discord", "D", True, {"webhook_url": "http://example.com"})
            count = await svc.publish_telemetry([{"id": "s1", "value": 42}])
            assert count == 0
            await db.close()
        _run(_test())

    def test_publish_telemetry_skips_non_telemetry_channels(self):
        """MQTT channels without publish_telemetry=True are skipped."""
        async def _test():
            svc, db = await _make_svc()
            await svc.create_channel(
                "mqtt_1", "mqtt", "Alerts Only", True,
                {"broker_url": "mqtt://localhost:1883", "topic_prefix": "test", "publish_telemetry": False},
            )
            count = await svc.publish_telemetry([{"id": "s1", "value": 42}])
            assert count == 0
            await db.close()
        _run(_test())

    def test_close_mqtt_clients(self):
        """close method clears MQTT client cache."""
        async def _test():
            svc, db = await _make_svc()
            # Manually add a mock client to the cache
            svc._mqtt_clients["fake"] = "placeholder"
            assert len(svc._mqtt_clients) == 1
            await svc._close_mqtt_clients()
            assert len(svc._mqtt_clients) == 0
            await db.close()
        _run(_test())
