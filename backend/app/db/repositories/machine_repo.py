"""SQLite repository for multi-machine hub registry."""

from __future__ import annotations

import secrets
from datetime import datetime, timezone

import aiosqlite


class MachineRepo:
    def __init__(self, db: aiosqlite.Connection) -> None:
        self._db = db

    @staticmethod
    def _normalize_url(value: str) -> str:
        return value.strip().rstrip("/")

    @staticmethod
    def _row_to_dict(row: tuple) -> dict:
        return {
            "id": row[0],
            "name": row[1],
            "base_url": row[2],
            "api_key": row[3],
            "api_key_id": row[4],
            "enabled": bool(row[5]),
            "poll_interval_seconds": float(row[6]),
            "timeout_ms": int(row[7]),
            "status": row[8],
            "last_seen_at": row[9],
            "last_error": row[10],
            "consecutive_failures": int(row[11]),
            "created_at": row[12],
            "updated_at": row[13],
        }

    async def list_all(self) -> list[dict]:
        cursor = await self._db.execute(
            "SELECT id, name, base_url, api_key, api_key_id, enabled, poll_interval_seconds, "
            "timeout_ms, status, last_seen_at, last_error, consecutive_failures, "
            "created_at, updated_at FROM machines ORDER BY created_at ASC"
        )
        return [self._row_to_dict(r) for r in await cursor.fetchall()]

    async def list_enabled(self) -> list[dict]:
        cursor = await self._db.execute(
            "SELECT id, name, base_url, api_key, api_key_id, enabled, poll_interval_seconds, "
            "timeout_ms, status, last_seen_at, last_error, consecutive_failures, "
            "created_at, updated_at FROM machines WHERE enabled = 1 ORDER BY created_at ASC"
        )
        return [self._row_to_dict(r) for r in await cursor.fetchall()]

    async def get(self, machine_id: str) -> dict | None:
        cursor = await self._db.execute(
            "SELECT id, name, base_url, api_key, api_key_id, enabled, poll_interval_seconds, "
            "timeout_ms, status, last_seen_at, last_error, consecutive_failures, "
            "created_at, updated_at FROM machines WHERE id = ?",
            (machine_id,),
        )
        row = await cursor.fetchone()
        return self._row_to_dict(row) if row else None

    async def create(
        self,
        *,
        name: str,
        base_url: str,
        api_key: str | None = None,
        api_key_id: str | None = None,
        enabled: bool = True,
        poll_interval_seconds: float = 2.0,
        timeout_ms: int = 1200,
    ) -> dict:
        machine_id = secrets.token_hex(8)
        now = datetime.now(timezone.utc).isoformat()
        await self._db.execute(
            "INSERT INTO machines (id, name, base_url, api_key, api_key_id, enabled, "
            "poll_interval_seconds, timeout_ms, status, consecutive_failures, created_at, updated_at) "
            "VALUES (?, ?, ?, ?, ?, ?, ?, ?, 'unknown', 0, ?, ?)",
            (
                machine_id,
                name.strip(),
                self._normalize_url(base_url),
                api_key.strip() if api_key else None,
                api_key_id,
                int(enabled),
                float(poll_interval_seconds),
                int(timeout_ms),
                now,
                now,
            ),
        )
        await self._db.commit()
        machine = await self.get(machine_id)
        if machine is None:
            raise RuntimeError("Machine creation failed unexpectedly")
        return machine

    async def update(self, machine_id: str, **fields: object) -> dict | None:
        allowed = {
            "name",
            "base_url",
            "api_key",
            "api_key_id",
            "enabled",
            "poll_interval_seconds",
            "timeout_ms",
        }
        # Skip fields whose value is a bare `object()` sentinel — these
        # indicate "field was not sent in the request; keep existing value".
        updates = {
            k: v for k, v in fields.items()
            if k in allowed and type(v) is not object
        }
        if not updates:
            return await self.get(machine_id)

        if "name" in updates and isinstance(updates["name"], str):
            updates["name"] = updates["name"].strip()
        if "base_url" in updates and isinstance(updates["base_url"], str):
            updates["base_url"] = self._normalize_url(updates["base_url"])
        if "api_key" in updates and isinstance(updates["api_key"], str):
            updates["api_key"] = updates["api_key"].strip() or None
        if "api_key_id" in updates and isinstance(updates["api_key_id"], str):
            updates["api_key_id"] = updates["api_key_id"].strip() or None
        if "enabled" in updates:
            updates["enabled"] = int(bool(updates["enabled"]))

        updates["updated_at"] = datetime.now(timezone.utc).isoformat()
        assignments = ", ".join(f"{k} = ?" for k in updates.keys())
        values = list(updates.values()) + [machine_id]
        cursor = await self._db.execute(
            f"UPDATE machines SET {assignments} WHERE id = ?",
            tuple(values),
        )
        await self._db.commit()
        if cursor.rowcount == 0:
            return None
        return await self.get(machine_id)

    async def delete(self, machine_id: str) -> bool:
        cursor = await self._db.execute(
            "DELETE FROM machines WHERE id = ?", (machine_id,)
        )
        await self._db.commit()
        return cursor.rowcount > 0

    async def update_health(
        self,
        machine_id: str,
        *,
        status: str,
        last_seen_at: str | None,
        last_error: str | None,
        consecutive_failures: int,
    ) -> None:
        await self._db.execute(
            "UPDATE machines SET status = ?, last_seen_at = ?, last_error = ?, "
            "consecutive_failures = ?, updated_at = ? WHERE id = ?",
            (
                status,
                last_seen_at,
                last_error,
                int(consecutive_failures),
                datetime.now(timezone.utc).isoformat(),
                machine_id,
            ),
        )
        await self._db.commit()

    async def increment_failures(
        self,
        machine_id: str,
        *,
        status: str,
        last_error: str | None,
    ) -> int:
        """Atomically increment consecutive_failures and return the new value."""
        await self._db.execute(
            "UPDATE machines SET status = ?, last_error = ?, "
            "consecutive_failures = consecutive_failures + 1, "
            "updated_at = ? WHERE id = ?",
            (
                status,
                last_error,
                datetime.now(timezone.utc).isoformat(),
                machine_id,
            ),
        )
        await self._db.commit()
        cursor = await self._db.execute(
            "SELECT consecutive_failures FROM machines WHERE id = ?",
            (machine_id,),
        )
        row = await cursor.fetchone()
        return int(row[0]) if row else 1

    async def update_last_command_at(self, machine_id: str, command_at: str) -> None:
        await self._db.execute(
            "UPDATE machines SET last_command_at = ?, updated_at = datetime('now') WHERE id = ?",
            (command_at, machine_id),
        )
        await self._db.commit()
