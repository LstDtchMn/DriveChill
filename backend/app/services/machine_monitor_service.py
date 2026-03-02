"""Background polling service for multi-machine hub monitoring."""

from __future__ import annotations

import asyncio
import logging
from datetime import datetime, timezone
from typing import Awaitable, Callable

import re

import httpx

from app.config import settings
from app.db.repositories.machine_repo import MachineRepo
from app.utils.url_security import validate_outbound_url_at_request_time

logger = logging.getLogger(__name__)

# Pattern to strip bearer tokens, passwords, and API keys from error messages.
_CREDENTIAL_RE = re.compile(
    r"(Bearer\s+)\S+|(api[_-]?key[=:]\s*)\S+|(password[=:]\s*)\S+",
    re.IGNORECASE,
)
_URL_USERINFO_RE = re.compile(r"://[^/\s@]+@", re.IGNORECASE)


def _redact_error(exc: Exception) -> str:
    """Truncate and strip credentials from exception text."""
    text = str(exc)[:300]
    text = _CREDENTIAL_RE.sub(r"\1\2\3[REDACTED]", text)
    text = _URL_USERINFO_RE.sub("://[REDACTED]@", text)
    return text


MachineFetcher = Callable[[dict], Awaitable[dict]]


class MachineMonitorService:
    """Polls configured machines and maintains latest remote snapshots."""

    def __init__(
        self,
        machine_repo: MachineRepo,
        fetcher: MachineFetcher | None = None,
    ) -> None:
        self._repo = machine_repo
        self._fetcher = fetcher or self._fetch_remote
        self._snapshots: dict[str, dict] = {}
        self._last_polled: dict[str, float] = {}
        self._next_allowed_poll: dict[str, float] = {}
        self._backoff_seconds: dict[str, float] = {}
        self._running = False
        self._task: asyncio.Task | None = None
        self._client: httpx.AsyncClient | None = None

    async def start(self) -> None:
        self._running = True
        self._client = httpx.AsyncClient(follow_redirects=False)
        self._task = asyncio.create_task(self._poll_loop())

    async def stop(self) -> None:
        self._running = False
        if self._task:
            self._task.cancel()
            try:
                await self._task
            except asyncio.CancelledError:
                pass
        if self._client:
            await self._client.aclose()
            self._client = None

    def get_snapshot(self, machine_id: str) -> dict | None:
        return self._snapshots.get(machine_id)

    def forget_machine(self, machine_id: str) -> None:
        self._snapshots.pop(machine_id, None)
        self._last_polled.pop(machine_id, None)
        self._next_allowed_poll.pop(machine_id, None)
        self._backoff_seconds.pop(machine_id, None)

    async def verify_machine(self, machine: dict) -> dict:
        """One-shot verification used by API/UI."""
        try:
            snapshot = await self._fetcher(machine)
            health = snapshot.get("health", {})
            api_version = str(health.get("api_version", "v1"))
            if not api_version.startswith("v1"):
                raise RuntimeError(f"API version mismatch: {api_version}")
            ts = snapshot.get("timestamp") or datetime.now(timezone.utc).isoformat()
            snapshot["timestamp"] = ts
            self._snapshots[machine["id"]] = snapshot
            await self._repo.update_health(
                machine["id"],
                status="online",
                last_seen_at=ts,
                last_error=None,
                consecutive_failures=0,
            )
            self._backoff_seconds[machine["id"]] = 2.0
            self._next_allowed_poll[machine["id"]] = 0.0
            return {"success": True, "status": "online", "snapshot": snapshot}
        except Exception as exc:
            status = self._classify_failure(exc)
            await self._repo.update_health(
                machine["id"],
                status=status,
                last_seen_at=machine.get("last_seen_at"),
                last_error=_redact_error(exc),
                consecutive_failures=int(machine.get("consecutive_failures", 0)) + 1,
            )
            return {"success": False, "status": status, "error": _redact_error(exc)}

    async def poll_once(self) -> None:
        machines = await self._repo.list_enabled()
        if not machines:
            return

        loop = asyncio.get_running_loop()
        now_mono = loop.time()
        tasks = []
        for machine in machines:
            interval = max(0.5, float(machine["poll_interval_seconds"]))
            last = self._last_polled.get(machine["id"])
            if last is not None and (now_mono - last) < interval:
                continue
            next_allowed = self._next_allowed_poll.get(machine["id"], 0.0)
            if now_mono < next_allowed:
                continue
            self._last_polled[machine["id"]] = now_mono
            tasks.append(self._poll_machine(machine))

        if tasks:
            await asyncio.gather(*tasks, return_exceptions=True)

    async def _poll_loop(self) -> None:
        while self._running:
            try:
                await self.poll_once()
            except asyncio.CancelledError:
                raise
            except Exception:
                logger.exception("Machine monitor poll loop failed")
            await asyncio.sleep(1.0)

    async def _poll_machine(self, machine: dict) -> None:
        machine_id = machine["id"]
        try:
            snapshot = await self._fetcher(machine)
            health = snapshot.get("health", {})
            api_version = str(health.get("api_version", "v1"))
            if not api_version.startswith("v1"):
                raise RuntimeError(f"API version mismatch: {api_version}")
            ts = snapshot.get("timestamp") or datetime.now(timezone.utc).isoformat()
            snapshot["timestamp"] = ts
            self._snapshots[machine_id] = snapshot
            await self._repo.update_health(
                machine_id,
                status="online",
                last_seen_at=ts,
                last_error=None,
                consecutive_failures=0,
            )
            self._backoff_seconds[machine_id] = 2.0
            self._next_allowed_poll[machine_id] = 0.0
        except Exception as exc:
            failures = int(machine.get("consecutive_failures", 0)) + 1
            status = self._classify_failure(exc)
            if status not in {"auth_error", "version_mismatch"}:
                status = "offline" if failures >= 3 else "degraded"
            await self._repo.update_health(
                machine_id,
                status=status,
                last_seen_at=machine.get("last_seen_at"),
                last_error=_redact_error(exc),
                consecutive_failures=failures,
            )
            if status in {"auth_error", "version_mismatch"}:
                backoff = self._backoff_seconds.get(machine_id, 2.0)
                self._next_allowed_poll[machine_id] = asyncio.get_running_loop().time() + backoff
                self._backoff_seconds[machine_id] = min(backoff * 2.0, 30.0)

    @staticmethod
    def _classify_failure(exc: Exception) -> str:
        if isinstance(exc, httpx.HTTPStatusError):
            code = exc.response.status_code
            if code in (401, 403):
                return "auth_error"
            if code == 426:
                return "version_mismatch"
        if "version mismatch" in str(exc).lower():
            return "version_mismatch"
        return "degraded"

    async def send_command(
        self,
        machine: dict,
        method: str,
        path: str,
        body: dict | None = None,
        timeout_override: float | None = None,
    ) -> dict:
        """Proxy a command to a remote agent. Returns the parsed JSON response.
        Raises httpx.HTTPStatusError on non-2xx, RuntimeError on URL block."""
        if not self._client:
            raise RuntimeError("Machine monitor client is not initialized")

        base_url = machine["base_url"].rstrip("/")
        timeout = timeout_override or (float(machine["timeout_ms"]) / 1000.0)

        ok, reason = validate_outbound_url_at_request_time(
            base_url,
            allow_private=settings.allow_private_outbound_targets,
        )
        if not ok:
            raise RuntimeError(f"URL blocked: {reason}")

        headers: dict[str, str] = {}
        if machine.get("api_key"):
            headers["Authorization"] = f"Bearer {machine['api_key']}"

        url = f"{base_url}{path}"
        response = await self._client.request(
            method=method.upper(),
            url=url,
            json=body,
            headers=headers,
            timeout=timeout,
        )
        response.raise_for_status()
        return response.json()

    async def get_remote_state(self, machine: dict) -> dict:
        """Fetch full remote state: profiles, fans, sensors."""
        profiles = await self.send_command(machine, "GET", "/api/profiles")
        fans = await self.send_command(machine, "GET", "/api/fans")
        sensors = await self.send_command(machine, "GET", "/api/sensors")
        return {
            "profiles": profiles.get("profiles", []),
            "fans": fans.get("fans", []),
            "sensors": sensors.get("readings", []),
        }

    async def _fetch_remote(self, machine: dict) -> dict:
        if not self._client:
            raise RuntimeError("Machine monitor client is not initialized")

        base_url = machine["base_url"].rstrip("/")

        timeout = max(0.2, float(machine["timeout_ms"]) / 1000.0)
        headers: dict[str, str] = {}
        if machine.get("api_key"):
            headers["Authorization"] = f"Bearer {machine['api_key']}"

        # Re-validate immediately before each outbound request.
        ok, reason = validate_outbound_url_at_request_time(
            base_url,
            allow_private=settings.allow_private_outbound_targets,
        )
        if not ok:
            raise RuntimeError(f"URL blocked: {reason}")
        health_resp = await self._client.get(
            f"{base_url}/api/health", headers=headers, timeout=timeout
        )
        health_resp.raise_for_status()
        health_json = health_resp.json()

        ok, reason = validate_outbound_url_at_request_time(
            base_url,
            allow_private=settings.allow_private_outbound_targets,
        )
        if not ok:
            raise RuntimeError(f"URL blocked: {reason}")
        sensors_resp = await self._client.get(
            f"{base_url}/api/sensors", headers=headers, timeout=timeout
        )
        sensors_resp.raise_for_status()
        sensors_json = sensors_resp.json()

        readings = sensors_json.get("readings", [])

        def first_value(sensor_type: str) -> float | None:
            for r in readings:
                if r.get("sensor_type") == sensor_type:
                    value = r.get("value")
                    if isinstance(value, (int, float)):
                        return float(value)
            return None

        summary = {
            "cpu_temp": first_value("cpu_temp"),
            "gpu_temp": first_value("gpu_temp"),
            "case_temp": first_value("case_temp"),
            "fan_count": sum(1 for r in readings if r.get("sensor_type") == "fan_rpm"),
            "backend": sensors_json.get("backend"),
        }

        return {
            "timestamp": datetime.now(timezone.utc).isoformat(),
            "health": health_json,
            "summary": summary,
        }
