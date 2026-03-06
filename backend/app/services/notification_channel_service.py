"""Notification channel service: ntfy.sh, Discord, Slack, generic webhook."""
from __future__ import annotations

import json
import logging
from typing import Any

import aiosqlite

from app.utils.url_security import validate_outbound_url_at_request_time

logger = logging.getLogger(__name__)

VALID_CHANNEL_TYPES = {"ntfy", "discord", "slack", "generic_webhook"}


class NotificationChannel:
    """In-memory representation of a notification channel."""
    __slots__ = ("id", "type", "name", "enabled", "config", "created_at", "updated_at")

    def __init__(self, *, id: str, type: str, name: str, enabled: bool,
                 config: dict, created_at: str, updated_at: str) -> None:
        self.id = id
        self.type = type
        self.name = name
        self.enabled = enabled
        self.config = config
        self.created_at = created_at
        self.updated_at = updated_at

    def to_dict(self) -> dict:
        return {
            "id": self.id,
            "type": self.type,
            "name": self.name,
            "enabled": self.enabled,
            "config": self.config,
            "created_at": self.created_at,
            "updated_at": self.updated_at,
        }


class NotificationChannelService:
    """CRUD and delivery for notification channels."""

    def __init__(self, db: aiosqlite.Connection) -> None:
        self._db = db
        self._http_session: Any = None  # lazily created aiohttp.ClientSession

    async def _ensure_session(self) -> Any:
        if self._http_session is None:
            try:
                import aiohttp
                self._http_session = aiohttp.ClientSession(
                    timeout=aiohttp.ClientTimeout(total=15),
                )
            except ImportError:
                logger.warning(
                    "aiohttp is not installed — notification channel delivery is unavailable. "
                    "Install it with: pip install aiohttp"
                )
        return self._http_session

    async def close(self) -> None:
        if self._http_session:
            await self._http_session.close()
            self._http_session = None

    # ── CRUD ─────────────────────────────────────────────────────────────

    async def list_channels(self) -> list[NotificationChannel]:
        cursor = await self._db.execute(
            "SELECT id, type, name, enabled, config_json, created_at, updated_at "
            "FROM notification_channels ORDER BY created_at"
        )
        rows = await cursor.fetchall()
        return [self._row_to_channel(r) for r in rows]

    async def get_channel(self, channel_id: str) -> NotificationChannel | None:
        cursor = await self._db.execute(
            "SELECT id, type, name, enabled, config_json, created_at, updated_at "
            "FROM notification_channels WHERE id = ?", (channel_id,)
        )
        row = await cursor.fetchone()
        return self._row_to_channel(row) if row else None

    async def create_channel(self, channel_id: str, channel_type: str,
                             name: str, enabled: bool, config: dict) -> NotificationChannel:
        await self._db.execute(
            "INSERT INTO notification_channels (id, type, name, enabled, config_json) "
            "VALUES (?, ?, ?, ?, ?)",
            (channel_id, channel_type, name, int(enabled), json.dumps(config)),
        )
        await self._db.commit()
        ch = await self.get_channel(channel_id)
        assert ch is not None
        return ch

    async def update_channel(self, channel_id: str, name: str | None = None,
                             enabled: bool | None = None, config: dict | None = None) -> bool:
        parts: list[str] = []
        vals: list[Any] = []
        if name is not None:
            parts.append("name = ?")
            vals.append(name)
        if enabled is not None:
            parts.append("enabled = ?")
            vals.append(int(enabled))
        if config is not None:
            parts.append("config_json = ?")
            vals.append(json.dumps(config))
        if not parts:
            return False
        parts.append("updated_at = datetime('now')")
        vals.append(channel_id)
        result = await self._db.execute(
            f"UPDATE notification_channels SET {', '.join(parts)} WHERE id = ?", vals
        )
        await self._db.commit()
        return result.rowcount > 0

    async def delete_channel(self, channel_id: str) -> bool:
        result = await self._db.execute(
            "DELETE FROM notification_channels WHERE id = ?", (channel_id,)
        )
        await self._db.commit()
        return result.rowcount > 0

    # ── Delivery ─────────────────────────────────────────────────────────

    async def send_alert_all(self, sensor_name: str, value: float, threshold: float) -> int:
        """Send alert to all enabled channels. Returns count of successful deliveries."""
        channels = await self.list_channels()
        successes = 0
        for ch in channels:
            if not ch.enabled:
                continue
            try:
                ok = await self._send(ch, sensor_name, value, threshold)
                if ok:
                    successes += 1
            except Exception:
                logger.exception("Failed to send alert via channel %s (%s)", ch.name, ch.type)
        return successes

    async def send_test(self, channel_id: str) -> tuple[bool, str | None]:
        """Send a test message to a specific channel. Returns (success, error)."""
        ch = await self.get_channel(channel_id)
        if ch is None:
            return False, "Channel not found"
        try:
            ok = await self._send(ch, "Test Sensor", 85.0, 80.0, test=True)
            return (ok, None if ok else "Delivery failed")
        except Exception as exc:
            return False, str(exc)

    async def _send(self, ch: NotificationChannel, sensor_name: str,
                    value: float, threshold: float, test: bool = False) -> bool:
        if ch.type == "ntfy":
            return await self._send_ntfy(ch.config, sensor_name, value, threshold, test)
        elif ch.type == "discord":
            return await self._send_discord(ch.config, sensor_name, value, threshold, test)
        elif ch.type == "slack":
            return await self._send_slack(ch.config, sensor_name, value, threshold, test)
        elif ch.type == "generic_webhook":
            return await self._send_generic(ch.config, sensor_name, value, threshold, test)
        else:
            logger.warning("Unknown channel type: %s", ch.type)
            return False

    async def _send_ntfy(self, config: dict, sensor_name: str,
                         value: float, threshold: float, test: bool) -> bool:
        """Send via ntfy.sh — simple HTTP POST to topic URL."""
        url = config.get("url", "").rstrip("/")
        topic = config.get("topic", "")
        if not url or not topic:
            return False
        ok, reason = await validate_outbound_url_at_request_time(url)
        if not ok:
            logger.warning("ntfy delivery blocked (SSRF): %s", reason)
            return False
        target = f"{url}/{topic}"
        title = "DriveChill Test Alert" if test else "DriveChill Alert"
        message = f"{sensor_name}: {value}°C (threshold: {threshold}°C)"

        headers: dict[str, str] = {
            "Title": title,
            "Priority": config.get("priority", "high"),
            "Tags": "thermometer,warning",
        }
        token = config.get("token", "")
        if token:
            headers["Authorization"] = f"Bearer {token}"

        session = await self._ensure_session()
        if session is None:
            return False
        async with session.post(target, data=message, headers=headers) as resp:
            return resp.status in (200, 201)

    async def _send_discord(self, config: dict, sensor_name: str,
                            value: float, threshold: float, test: bool) -> bool:
        """Send via Discord webhook — embed format."""
        webhook_url = config.get("webhook_url", "")
        if not webhook_url:
            return False
        ok, reason = await validate_outbound_url_at_request_time(webhook_url)
        if not ok:
            logger.warning("Discord delivery blocked (SSRF): %s", reason)
            return False
        title = "DriveChill Test Alert" if test else "DriveChill Alert"
        payload = {
            "embeds": [{
                "title": title,
                "description": f"**{sensor_name}** reached {value}°C (threshold: {threshold}°C)",
                "color": 0xFF4444,  # red
            }],
        }
        session = await self._ensure_session()
        if session is None:
            return False
        async with session.post(webhook_url, json=payload) as resp:
            return resp.status in (200, 204)

    async def _send_slack(self, config: dict, sensor_name: str,
                          value: float, threshold: float, test: bool) -> bool:
        """Send via Slack incoming webhook."""
        webhook_url = config.get("webhook_url", "")
        if not webhook_url:
            return False
        ok, reason = await validate_outbound_url_at_request_time(webhook_url)
        if not ok:
            logger.warning("Slack delivery blocked (SSRF): %s", reason)
            return False
        prefix = ":test_tube: " if test else ":warning: "
        payload = {
            "text": f"{prefix}*DriveChill Alert*\n{sensor_name}: {value}°C (threshold: {threshold}°C)",
        }
        session = await self._ensure_session()
        if session is None:
            return False
        async with session.post(webhook_url, json=payload) as resp:
            return resp.status == 200

    async def _send_generic(self, config: dict, sensor_name: str,
                            value: float, threshold: float, test: bool) -> bool:
        """Send via generic webhook — JSON POST with optional HMAC-SHA256 signature."""
        url = config.get("url", "")
        if not url:
            return False
        ok, reason = await validate_outbound_url_at_request_time(url)
        if not ok:
            logger.warning("Generic webhook delivery blocked (SSRF): %s", reason)
            return False
        payload = {
            "source": "drivechill",
            "test": test,
            "sensor_name": sensor_name,
            "value": value,
            "threshold": threshold,
            "message": f"{sensor_name}: {value}°C (threshold: {threshold}°C)",
        }
        # Serialize once so HMAC is over the exact bytes sent on the wire
        body = json.dumps(payload, separators=(",", ":")).encode()
        headers: dict[str, str] = {"Content-Type": "application/json"}
        secret = config.get("hmac_secret", "")
        if secret:
            import hashlib
            import hmac as hmac_mod
            sig = hmac_mod.new(secret.encode(), body, hashlib.sha256).hexdigest()
            headers["X-DriveChill-Signature"] = f"sha256={sig}"

        session = await self._ensure_session()
        if session is None:
            return False
        async with session.post(url, data=body, headers=headers) as resp:
            return 200 <= resp.status < 300

    @staticmethod
    def _row_to_channel(row: tuple) -> NotificationChannel:
        config = {}
        if row[4]:
            try:
                config = json.loads(row[4])
            except (json.JSONDecodeError, TypeError):
                pass
        return NotificationChannel(
            id=row[0], type=row[1], name=row[2],
            enabled=bool(row[3]), config=config,
            created_at=row[5] or "", updated_at=row[6] or "",
        )
