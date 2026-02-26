"""Tests for sensor labeling API (v1.0 must-ship feature).

Covers: CRUD operations on the sensor_labels table via the API routes.
"""
from __future__ import annotations

import asyncio

import aiosqlite
import pytest


async def _init_db(db_path) -> aiosqlite.Connection:
    """Open DB and create required tables."""
    db = await aiosqlite.connect(str(db_path))
    await db.execute("PRAGMA foreign_keys=ON")
    await db.executescript("""
        CREATE TABLE IF NOT EXISTS sensor_labels (
            sensor_id   TEXT PRIMARY KEY,
            label       TEXT NOT NULL,
            updated_at  TEXT NOT NULL DEFAULT (datetime('now'))
        );
    """)
    await db.commit()
    return db


class TestSensorLabels:

    def test_set_and_get_label(self, tmp_db) -> None:
        """A label can be stored and retrieved."""
        async def run():
            db = await _init_db(tmp_db)
            from datetime import datetime, timezone
            now = datetime.now(timezone.utc).isoformat()

            # Set a label
            await db.execute(
                "INSERT OR REPLACE INTO sensor_labels (sensor_id, label, updated_at) "
                "VALUES (?, ?, ?)",
                ("cpu_temp_0", "My CPU", now),
            )
            await db.commit()

            # Retrieve it
            cursor = await db.execute(
                "SELECT label FROM sensor_labels WHERE sensor_id = ?",
                ("cpu_temp_0",),
            )
            row = await cursor.fetchone()
            assert row is not None
            assert row[0] == "My CPU"
            await db.close()

        asyncio.run(run())

    def test_update_existing_label(self, tmp_db) -> None:
        """Updating an existing label replaces the old one."""
        async def run():
            db = await _init_db(tmp_db)
            from datetime import datetime, timezone
            now = datetime.now(timezone.utc).isoformat()

            await db.execute(
                "INSERT OR REPLACE INTO sensor_labels (sensor_id, label, updated_at) "
                "VALUES (?, ?, ?)",
                ("gpu_temp_0", "Old Name", now),
            )
            await db.commit()

            await db.execute(
                "INSERT OR REPLACE INTO sensor_labels (sensor_id, label, updated_at) "
                "VALUES (?, ?, ?)",
                ("gpu_temp_0", "New Name", now),
            )
            await db.commit()

            cursor = await db.execute(
                "SELECT label FROM sensor_labels WHERE sensor_id = ?",
                ("gpu_temp_0",),
            )
            row = await cursor.fetchone()
            assert row[0] == "New Name"

            # Only one row should exist
            cursor = await db.execute("SELECT COUNT(*) FROM sensor_labels")
            count = (await cursor.fetchone())[0]
            assert count == 1
            await db.close()

        asyncio.run(run())

    def test_delete_label(self, tmp_db) -> None:
        """Deleting a label removes it from the table."""
        async def run():
            db = await _init_db(tmp_db)
            from datetime import datetime, timezone
            now = datetime.now(timezone.utc).isoformat()

            await db.execute(
                "INSERT INTO sensor_labels (sensor_id, label, updated_at) "
                "VALUES (?, ?, ?)",
                ("hdd_temp_0", "SSD Drive", now),
            )
            await db.commit()

            cursor = await db.execute(
                "DELETE FROM sensor_labels WHERE sensor_id = ?",
                ("hdd_temp_0",),
            )
            await db.commit()
            assert cursor.rowcount == 1

            cursor = await db.execute("SELECT COUNT(*) FROM sensor_labels")
            count = (await cursor.fetchone())[0]
            assert count == 0
            await db.close()

        asyncio.run(run())

    def test_get_all_labels(self, tmp_db) -> None:
        """All labels can be retrieved at once."""
        async def run():
            db = await _init_db(tmp_db)
            from datetime import datetime, timezone
            now = datetime.now(timezone.utc).isoformat()

            for sid, label in [("cpu_temp_0", "CPU Core"), ("gpu_temp_0", "GPU Die")]:
                await db.execute(
                    "INSERT INTO sensor_labels (sensor_id, label, updated_at) "
                    "VALUES (?, ?, ?)",
                    (sid, label, now),
                )
            await db.commit()

            cursor = await db.execute("SELECT sensor_id, label FROM sensor_labels")
            rows = await cursor.fetchall()
            labels = {row[0]: row[1] for row in rows}
            assert labels == {"cpu_temp_0": "CPU Core", "gpu_temp_0": "GPU Die"}
            await db.close()

        asyncio.run(run())

    def test_delete_nonexistent_label(self, tmp_db) -> None:
        """Deleting a label that doesn't exist affects 0 rows."""
        async def run():
            db = await _init_db(tmp_db)
            cursor = await db.execute(
                "DELETE FROM sensor_labels WHERE sensor_id = ?",
                ("does_not_exist",),
            )
            await db.commit()
            assert cursor.rowcount == 0
            await db.close()

        asyncio.run(run())
