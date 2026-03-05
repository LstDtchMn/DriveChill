"""SQLite-backed repository for temperature targets."""

from __future__ import annotations

import json
import secrets
from datetime import datetime, timezone

import aiosqlite

from app.models.temperature_targets import TemperatureTarget


class TemperatureTargetRepo:
    def __init__(self, db: aiosqlite.Connection) -> None:
        self._db = db

    async def list_all(self) -> list[TemperatureTarget]:
        cursor = await self._db.execute(
            "SELECT id, name, drive_id, sensor_id, fan_ids_json, "
            "target_temp_c, tolerance_c, min_fan_speed, enabled "
            "FROM temperature_targets ORDER BY created_at"
        )
        rows = await cursor.fetchall()
        return [self._row_to_model(r) for r in rows]

    async def get(self, target_id: str) -> TemperatureTarget | None:
        cursor = await self._db.execute(
            "SELECT id, name, drive_id, sensor_id, fan_ids_json, "
            "target_temp_c, tolerance_c, min_fan_speed, enabled "
            "FROM temperature_targets WHERE id = ?",
            (target_id,),
        )
        row = await cursor.fetchone()
        return self._row_to_model(row) if row else None

    async def create(self, target: TemperatureTarget) -> TemperatureTarget:
        now = datetime.now(timezone.utc).strftime("%Y-%m-%d %H:%M:%S")
        await self._db.execute(
            "INSERT INTO temperature_targets "
            "(id, name, drive_id, sensor_id, fan_ids_json, "
            "target_temp_c, tolerance_c, min_fan_speed, enabled, created_at, updated_at) "
            "VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
            (
                target.id,
                target.name,
                target.drive_id,
                target.sensor_id,
                json.dumps(target.fan_ids),
                target.target_temp_c,
                target.tolerance_c,
                target.min_fan_speed,
                1 if target.enabled else 0,
                now,
                now,
            ),
        )
        await self._db.commit()
        return target

    _ALLOWED_UPDATE_FIELDS = frozenset({
        "name", "drive_id", "sensor_id", "fan_ids", "fan_ids_json",
        "target_temp_c", "tolerance_c", "min_fan_speed", "enabled", "updated_at",
    })

    async def update(self, target_id: str, **fields) -> TemperatureTarget | None:
        existing = await self.get(target_id)
        if not existing:
            return None

        illegal = set(fields) - self._ALLOWED_UPDATE_FIELDS
        if illegal:
            raise ValueError(f"Disallowed update fields: {illegal}")

        now = datetime.now(timezone.utc).strftime("%Y-%m-%d %H:%M:%S")
        updates = dict(fields)
        updates["updated_at"] = now

        # Convert fan_ids list to JSON for storage
        if "fan_ids" in updates:
            updates["fan_ids_json"] = json.dumps(updates.pop("fan_ids"))

        # Convert enabled bool to int
        if "enabled" in updates:
            updates["enabled"] = 1 if updates["enabled"] else 0

        set_clause = ", ".join(f"{k} = ?" for k in updates)
        values = list(updates.values()) + [target_id]
        await self._db.execute(
            f"UPDATE temperature_targets SET {set_clause} WHERE id = ?",
            values,
        )
        await self._db.commit()
        return await self.get(target_id)

    async def delete(self, target_id: str) -> bool:
        cursor = await self._db.execute(
            "DELETE FROM temperature_targets WHERE id = ?",
            (target_id,),
        )
        await self._db.commit()
        return cursor.rowcount > 0

    @staticmethod
    def generate_id() -> str:
        return secrets.token_hex(6)

    @staticmethod
    def _row_to_model(row) -> TemperatureTarget:
        return TemperatureTarget(
            id=row[0],
            name=row[1],
            drive_id=row[2],
            sensor_id=row[3],
            fan_ids=json.loads(row[4]) if row[4] else [],
            target_temp_c=row[5],
            tolerance_c=row[6],
            min_fan_speed=row[7],
            enabled=bool(row[8]),
        )
