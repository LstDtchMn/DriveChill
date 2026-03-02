"""SQLite repository for Web Push notification subscriptions."""

from __future__ import annotations

import uuid
from datetime import datetime, timezone

import aiosqlite


class PushSubscriptionRepo:
    def __init__(self, db: aiosqlite.Connection) -> None:
        self._db = db

    @staticmethod
    def _row_to_dict(row: tuple) -> dict:
        return {
            "id": row[0],
            "endpoint": row[1],
            "p256dh": row[2],
            "auth": row[3],
            "user_agent": row[4],
            "created_at": row[5],
            "last_used_at": row[6],
        }

    async def create(
        self,
        endpoint: str,
        p256dh: str,
        auth: str,
        user_agent: str | None,
    ) -> dict:
        subscription_id = str(uuid.uuid4())
        now = datetime.now(timezone.utc).isoformat()
        await self._db.execute(
            "INSERT INTO push_subscriptions (id, endpoint, p256dh, auth, user_agent, created_at) "
            "VALUES (?, ?, ?, ?, ?, ?)",
            (subscription_id, endpoint, p256dh, auth, user_agent, now),
        )
        await self._db.commit()
        result = await self.get_by_endpoint(endpoint)
        if result is None:
            raise RuntimeError("Push subscription creation failed unexpectedly")
        return result

    async def list_all(self) -> list[dict]:
        cursor = await self._db.execute(
            "SELECT id, endpoint, p256dh, auth, user_agent, created_at, last_used_at "
            "FROM push_subscriptions ORDER BY created_at ASC"
        )
        return [self._row_to_dict(r) for r in await cursor.fetchall()]

    async def delete(self, subscription_id: str) -> bool:
        cursor = await self._db.execute(
            "DELETE FROM push_subscriptions WHERE id = ?", (subscription_id,)
        )
        await self._db.commit()
        return cursor.rowcount > 0

    async def get_by_endpoint(self, endpoint: str) -> dict | None:
        cursor = await self._db.execute(
            "SELECT id, endpoint, p256dh, auth, user_agent, created_at, last_used_at "
            "FROM push_subscriptions WHERE endpoint = ?",
            (endpoint,),
        )
        row = await cursor.fetchone()
        return self._row_to_dict(row) if row else None

    async def get(self, subscription_id: str) -> dict | None:
        cursor = await self._db.execute(
            "SELECT id, endpoint, p256dh, auth, user_agent, created_at, last_used_at "
            "FROM push_subscriptions WHERE id = ?",
            (subscription_id,),
        )
        row = await cursor.fetchone()
        return self._row_to_dict(row) if row else None

    async def update_last_used(self, subscription_id: str) -> None:
        now = datetime.now(timezone.utc).isoformat()
        await self._db.execute(
            "UPDATE push_subscriptions SET last_used_at = ? WHERE id = ?",
            (now, subscription_id),
        )
        await self._db.commit()
