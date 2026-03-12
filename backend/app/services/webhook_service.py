from __future__ import annotations

import asyncio
import hashlib
import hmac
import json
import logging
import secrets
import time
from datetime import datetime, timezone
from urllib.parse import urlparse, urlunparse

import aiosqlite
import httpx

from app.services import prom_metrics
from app.utils.url_security import validate_outbound_url_at_request_time

logger = logging.getLogger(__name__)

# Sentinel: when signing_secret is NOT in the PUT body, keep the existing value.
_KEEP_SECRET = object()


def _redact_url_for_log(url: str) -> str:
    """Drop query/userinfo from logged URLs to avoid leaking credentials."""
    try:
        parsed = urlparse(url)
        if not parsed.scheme or not parsed.netloc:
            return url
        host = parsed.hostname or ""
        if not host:
            return url
        # keep explicit port where present
        if parsed.port is not None:
            host = f"{host}:{parsed.port}"
        sanitized = parsed._replace(netloc=host, query="", fragment="")
        return urlunparse(sanitized)
    except Exception:
        return url


class WebhookService:
    """Configurable webhook dispatcher for alert events."""

    def __init__(self, db: aiosqlite.Connection) -> None:
        self._db = db
        self._client: httpx.AsyncClient | None = None
        # Integration health tracking
        self.success_count: int = 0
        self.failure_count: int = 0
        self.last_error: str | None = None

    async def start(self) -> None:
        self._client = httpx.AsyncClient(follow_redirects=False)

    async def stop(self) -> None:
        if self._client:
            await self._client.aclose()
            self._client = None

    async def get_config(self) -> dict:
        raw = await self._get_config_raw()
        return {
            "enabled": bool(raw["enabled"]),
            "target_url": raw["target_url"] or "",
            "has_signing_secret": bool(raw.get("signing_secret")),
            "timeout_seconds": float(raw["timeout_seconds"]),
            "max_retries": int(raw["max_retries"]),
            "retry_backoff_seconds": float(raw["retry_backoff_seconds"]),
            "updated_at": raw["updated_at"],
        }

    async def _get_config_raw(self) -> dict:
        cursor = await self._db.execute(
            "SELECT enabled, target_url, signing_secret, timeout_seconds, "
            "max_retries, retry_backoff_seconds, updated_at "
            "FROM webhooks WHERE id = 1"
        )
        row = await cursor.fetchone()
        if not row:
            now = datetime.now(timezone.utc).isoformat()
            return {
                "enabled": False,
                "target_url": "",
                "signing_secret": None,
                "timeout_seconds": 3.0,
                "max_retries": 2,
                "retry_backoff_seconds": 1.0,
                "updated_at": now,
            }
        return {
            "enabled": bool(row[0]),
            "target_url": row[1] or "",
            "signing_secret": row[2],
            "timeout_seconds": float(row[3]),
            "max_retries": int(row[4]),
            "retry_backoff_seconds": float(row[5]),
            "updated_at": row[6],
        }

    async def update_config(
        self,
        *,
        enabled: bool,
        target_url: str,
        signing_secret: str | None | object,
        timeout_seconds: float,
        max_retries: int,
        retry_backoff_seconds: float,
    ) -> dict:
        secret_value: str | None
        if signing_secret is _KEEP_SECRET:
            cursor = await self._db.execute(
                "SELECT signing_secret FROM webhooks WHERE id = 1"
            )
            row = await cursor.fetchone()
            secret_value = row[0] if row else None
        else:
            secret_value = signing_secret or None

        now = datetime.now(timezone.utc).isoformat()
        await self._db.execute(
            "UPDATE webhooks SET enabled = ?, target_url = ?, signing_secret = ?, "
            "timeout_seconds = ?, max_retries = ?, retry_backoff_seconds = ?, "
            "updated_at = ? WHERE id = 1",
            (
                int(enabled),
                target_url.strip(),
                secret_value,
                float(timeout_seconds),
                int(max_retries),
                float(retry_backoff_seconds),
                now,
            ),
        )
        await self._db.commit()
        return await self.get_config()

    async def get_delivery_log(self, limit: int = 100, offset: int = 0) -> list[dict]:
        cursor = await self._db.execute(
            "SELECT timestamp, event_type, target_url, attempt, success, http_status, "
            "latency_ms, error FROM webhook_delivery_log ORDER BY id DESC LIMIT ? OFFSET ?",
            (max(1, min(limit, 500)), max(0, offset)),
        )
        rows = await cursor.fetchall()
        return [
            {
                "timestamp": row[0],
                "event_type": row[1],
                "target_url": row[2],
                "attempt": row[3],
                "success": bool(row[4]),
                "http_status": row[5],
                "latency_ms": row[6],
                "error": row[7],
            }
            for row in rows
        ]

    async def dispatch_alert_events(self, events: list[dict]) -> None:
        if not events:
            return
        cfg = await self._get_config_raw()
        if not cfg["enabled"] or not cfg["target_url"]:
            return
        payload = {
            "event_type": "alert_triggered",
            "timestamp": datetime.now(timezone.utc).isoformat(),
            "events": events,
        }
        await self._dispatch_payload("alert_triggered", payload, cfg)

    async def _dispatch_payload(self, event_type: str, payload: dict, cfg: dict) -> None:
        if not self._client:
            return
        payload_json = json.dumps(payload, separators=(",", ":"), ensure_ascii=True)
        body = payload_json.encode("utf-8")

        timeout = max(0.5, float(cfg["timeout_seconds"]))
        max_retries = max(0, int(cfg["max_retries"]))
        backoff = max(0.1, float(cfg["retry_backoff_seconds"]))
        target_url = str(cfg["target_url"])
        signing_secret = cfg.get("signing_secret") or ""

        for attempt in range(1, max_retries + 2):
            # Re-validate immediately before each request attempt.
            # This narrows the DNS rebinding window between validation and connect.
            ok, reason = await validate_outbound_url_at_request_time(target_url)
            if not ok:
                logger.warning("Webhook target blocked at dispatch time: %s", reason)
                return

            # Generate fresh timestamp/nonce/signature per attempt so that
            # receivers implementing replay protection accept retries.
            signed_timestamp = str(int(datetime.now(timezone.utc).timestamp()))
            nonce = secrets.token_hex(16)
            headers = {
                "Content-Type": "application/json",
                "X-DriveChill-Timestamp": signed_timestamp,
                "X-DriveChill-Nonce": nonce,
            }
            if signing_secret:
                message = f"{signed_timestamp}.{nonce}.".encode("utf-8") + body
                signature = hmac.new(
                    signing_secret.encode("utf-8"),
                    message,
                    digestmod=hashlib.sha256,
                ).hexdigest()
                headers["X-DriveChill-Signature"] = f"sha256={signature}"

            status_code: int | None = None
            error: str | None = None
            success = False
            start = time.perf_counter()
            try:
                resp = await self._client.post(
                    target_url,
                    content=body,
                    headers=headers,
                    timeout=timeout,
                )
                status_code = resp.status_code
                success = 200 <= resp.status_code < 300
                if not success:
                    error = f"HTTP {resp.status_code}"
            except Exception as exc:
                error = str(exc)[:300]
            latency_ms = int((time.perf_counter() - start) * 1000)

            prom_metrics.webhook_deliveries_total.labels("true" if success else "false").inc()
            await self._db.execute(
                "INSERT INTO webhook_delivery_log "
                "(timestamp, event_type, target_url, payload_json, attempt, success, "
                "http_status, latency_ms, error) "
                "VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)",
                (
                    datetime.now(timezone.utc).isoformat(),
                    event_type,
                    _redact_url_for_log(target_url),
                    payload_json,
                    attempt,
                    int(success),
                    status_code,
                    latency_ms,
                    error,
                ),
            )
            await self._db.commit()

            if success:
                self.success_count += 1
                self.last_error = None
                return
            else:
                self.failure_count += 1
                self.last_error = error
            if attempt < max_retries + 1:
                await asyncio.sleep(min(backoff * (2 ** (attempt - 1)), 60.0))
        logger.warning("Webhook delivery failed after retries: %s", target_url)

    async def prune_delivery_log(self, max_rows: int = 5000) -> int:
        """Keep webhook delivery log bounded to recent rows (atomic single query)."""
        safe_max = max(100, min(max_rows, 50000))
        cursor = await self._db.execute(
            "DELETE FROM webhook_delivery_log WHERE id NOT IN "
            "(SELECT id FROM webhook_delivery_log ORDER BY id DESC LIMIT ?)",
            (safe_max,),
        )
        await self._db.commit()
        return cursor.rowcount
