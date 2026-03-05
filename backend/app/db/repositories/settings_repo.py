"""SQLite-backed repository for application settings (key-value store)."""

from __future__ import annotations

from datetime import datetime, timezone

import aiosqlite


# Default values seeded on first run.  Keys here must match what the
# settings route and config module expect.
_DEFAULTS: dict[str, str] = {
    "sensor_poll_interval": "1.0",
    "history_retention_hours": "720",  # 30 days
    "temp_unit": "C",
    "panic_cpu_temp_c": "95",
    "panic_gpu_temp_c": "90",
    "panic_hysteresis_c": "5",
    "sensor_failure_limit": "3",
    "alert_cooldown_seconds": "300",
}


class SettingsRepo:
    def __init__(self, db: aiosqlite.Connection) -> None:
        self._db = db

    async def get(self, key: str) -> str | None:
        cursor = await self._db.execute(
            "SELECT value FROM settings WHERE key = ?", (key,)
        )
        row = await cursor.fetchone()
        return row[0] if row else None

    async def get_float(self, key: str, default: float = 0.0) -> float:
        val = await self.get(key)
        if val is None:
            return default
        try:
            return float(val)
        except ValueError:
            return default

    async def get_int(self, key: str, default: int = 0) -> int:
        val = await self.get(key)
        if val is None:
            return default
        try:
            return int(val)
        except ValueError:
            return default

    async def get_all(self) -> dict[str, str]:
        cursor = await self._db.execute("SELECT key, value FROM settings")
        rows = await cursor.fetchall()
        return {row[0]: row[1] for row in rows}

    async def set(self, key: str, value: str) -> None:
        now = datetime.now(timezone.utc).isoformat()
        await self._db.execute(
            "INSERT OR REPLACE INTO settings (key, value, updated_at) "
            "VALUES (?, ?, ?)",
            (key, value, now),
        )
        await self._db.commit()

    async def set_many(self, items: dict[str, str]) -> None:
        now = datetime.now(timezone.utc).isoformat()
        for key, value in items.items():
            await self._db.execute(
                "INSERT OR REPLACE INTO settings (key, value, updated_at) "
                "VALUES (?, ?, ?)",
                (key, value, now),
            )
        await self._db.commit()

    async def seed_defaults(self) -> None:
        """Insert default settings for any keys that don't already exist.

        M-5: single INSERT OR IGNORE batch instead of N sequential
        SELECT+INSERT round-trips on every startup.
        """
        now = datetime.now(timezone.utc).isoformat()
        await self._db.executemany(
            "INSERT OR IGNORE INTO settings (key, value, updated_at) VALUES (?, ?, ?)",
            [(k, v, now) for k, v in _DEFAULTS.items()],
        )
        await self._db.commit()
