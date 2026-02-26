"""SQLite-backed repository for per-fan settings (min speed floor, zero-RPM)."""

from __future__ import annotations

import aiosqlite


class FanSettingsRepo:
    def __init__(self, db: aiosqlite.Connection) -> None:
        self._db = db

    async def get(self, fan_id: str) -> dict | None:
        cursor = await self._db.execute(
            "SELECT min_speed_pct, zero_rpm_capable FROM fan_settings WHERE fan_id = ?",
            (fan_id,),
        )
        row = await cursor.fetchone()
        if not row:
            return None
        return {"min_speed_pct": row[0], "zero_rpm_capable": bool(row[1])}

    async def get_all(self) -> dict[str, dict]:
        cursor = await self._db.execute(
            "SELECT fan_id, min_speed_pct, zero_rpm_capable FROM fan_settings"
        )
        rows = await cursor.fetchall()
        return {
            row[0]: {"min_speed_pct": row[1], "zero_rpm_capable": bool(row[2])}
            for row in rows
        }

    async def set(self, fan_id: str, min_speed_pct: float,
                  zero_rpm_capable: bool) -> None:
        await self._db.execute(
            "INSERT OR REPLACE INTO fan_settings "
            "(fan_id, min_speed_pct, zero_rpm_capable, updated_at) "
            "VALUES (?, ?, ?, datetime('now'))",
            (fan_id, min_speed_pct, int(zero_rpm_capable)),
        )
        await self._db.commit()
