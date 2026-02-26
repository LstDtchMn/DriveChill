"""Tests for the portable JSON backup/restore and DB snapshot restore."""

from __future__ import annotations

import asyncio
import json
import shutil
from pathlib import Path

import aiosqlite
import pytest

from app.db.migration_runner import run_migrations
from app.services.backup_service import (
    BACKUP_VERSION,
    export_backup,
    import_backup,
    restore_db_snapshot,
)


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

async def _seed_db(db_path: Path) -> None:
    """Apply migrations and insert representative test data."""
    await run_migrations(db_path)

    async with aiosqlite.connect(str(db_path)) as db:
        await db.execute("PRAGMA foreign_keys=ON")

        # Profile
        await db.execute(
            "INSERT INTO profiles (id, name, preset, is_active, created_at, updated_at) "
            "VALUES ('p1', 'Test Profile', 'custom', 1, '2026-01-01', '2026-01-01')"
        )
        # Fan curve
        await db.execute(
            "INSERT INTO fan_curves (id, profile_id, name, sensor_id, fan_id, enabled, points_json) "
            "VALUES ('c1', 'p1', 'CPU Curve', 'sensor_cpu', 'fan_1', 1, "
            "'[{\"temp\":30,\"speed\":20},{\"temp\":80,\"speed\":100}]')"
        )
        # Settings
        await db.execute(
            "INSERT OR REPLACE INTO settings (key, value) VALUES ('temp_unit', 'F')"
        )
        await db.execute(
            "INSERT OR REPLACE INTO settings (key, value) VALUES ('poll_interval', '2.0')"
        )
        # Sensor label
        await db.execute(
            "INSERT INTO sensor_labels (sensor_id, label) VALUES ('sensor_cpu', 'My CPU')"
        )
        # Alert rule
        await db.execute(
            "INSERT INTO alert_rules (id, sensor_id, threshold, direction, enabled, cooldown_seconds, name) "
            "VALUES ('a1', 'sensor_cpu', 85.0, 'above', 1, 300, 'CPU Hot')"
        )
        # Quiet hours rule
        await db.execute(
            "INSERT INTO quiet_hours (day_of_week, start_time, end_time, profile_id, enabled) "
            "VALUES (0, '23:00', '07:00', 'p1', 1)"
        )
        await db.commit()


async def _count_rows(db_path: Path, table: str) -> int:
    async with aiosqlite.connect(str(db_path)) as db:
        cursor = await db.execute(f"SELECT COUNT(*) FROM {table}")  # noqa: S608
        row = await cursor.fetchone()
        return row[0]


async def _get_setting(db_path: Path, key: str) -> str | None:
    async with aiosqlite.connect(str(db_path)) as db:
        cursor = await db.execute("SELECT value FROM settings WHERE key = ?", (key,))
        row = await cursor.fetchone()
        return row[0] if row else None


# ---------------------------------------------------------------------------
# Export tests
# ---------------------------------------------------------------------------

class TestExportBackup:

    def test_export_creates_json_file(self, tmp_db: Path, tmp_path: Path) -> None:
        asyncio.run(_seed_db(tmp_db))
        out = tmp_path / "backup.json"
        result = asyncio.run(export_backup(tmp_db, out))
        assert result == out
        assert out.exists()

    def test_export_contains_all_sections(self, tmp_db: Path, tmp_path: Path) -> None:
        asyncio.run(_seed_db(tmp_db))
        out = tmp_path / "backup.json"
        asyncio.run(export_backup(tmp_db, out))

        data = json.loads(out.read_text())
        assert data["backup_version"] == BACKUP_VERSION
        assert "app_version" in data
        assert "created_at" in data
        assert len(data["profiles"]) == 1
        assert len(data["fan_curves"]) == 1
        assert data["settings"]["temp_unit"] == "F"
        assert data["sensor_labels"]["sensor_cpu"] == "My CPU"
        assert len(data["alert_rules"]) == 1
        assert len(data["quiet_hours"]) == 1

    def test_export_profile_fields(self, tmp_db: Path, tmp_path: Path) -> None:
        asyncio.run(_seed_db(tmp_db))
        out = tmp_path / "backup.json"
        asyncio.run(export_backup(tmp_db, out))

        data = json.loads(out.read_text())
        profile = data["profiles"][0]
        assert profile["id"] == "p1"
        assert profile["name"] == "Test Profile"
        assert profile["preset"] == "custom"

    def test_export_default_output_path(self, tmp_db: Path, tmp_path: Path, monkeypatch) -> None:
        """When no output path is given, file is created in cwd."""
        asyncio.run(_seed_db(tmp_db))
        monkeypatch.chdir(tmp_path)
        result = asyncio.run(export_backup(tmp_db, None))
        assert result.exists()
        assert result.name.startswith("drivechill-backup-")
        assert result.suffix == ".json"


# ---------------------------------------------------------------------------
# Import tests
# ---------------------------------------------------------------------------

class TestImportBackup:

    def test_round_trip(self, tmp_db: Path, tmp_path: Path) -> None:
        """Export -> wipe DB -> import -> all data restored."""
        asyncio.run(_seed_db(tmp_db))
        backup_file = tmp_path / "backup.json"
        asyncio.run(export_backup(tmp_db, backup_file))

        # Wipe the DB
        tmp_db.unlink()

        summary = asyncio.run(import_backup(tmp_db, backup_file))
        assert summary["profiles"] == 1
        assert summary["fan_curves"] == 1
        assert summary["settings"] >= 2
        assert summary["sensor_labels"] == 1
        assert summary["alert_rules"] == 1
        assert summary["quiet_hours"] == 1

        # Verify data
        val = asyncio.run(_get_setting(tmp_db, "temp_unit"))
        assert val == "F"
        count = asyncio.run(_count_rows(tmp_db, "profiles"))
        assert count == 1

    def test_import_replaces_existing_data(self, tmp_db: Path, tmp_path: Path) -> None:
        """Import into a DB that already has data replaces it."""
        asyncio.run(_seed_db(tmp_db))
        backup_file = tmp_path / "backup.json"
        asyncio.run(export_backup(tmp_db, backup_file))

        # Re-import on top of existing data
        summary = asyncio.run(import_backup(tmp_db, backup_file))
        assert summary["profiles"] == 1

        # Should still have exactly 1 profile, not 2
        count = asyncio.run(_count_rows(tmp_db, "profiles"))
        assert count == 1

    def test_import_missing_version_raises(self, tmp_db: Path, tmp_path: Path) -> None:
        bad_file = tmp_path / "bad.json"
        bad_file.write_text('{"profiles": []}')

        with pytest.raises(ValueError, match="missing 'backup_version'"):
            asyncio.run(import_backup(tmp_db, bad_file))

    def test_import_future_version_raises(self, tmp_db: Path, tmp_path: Path) -> None:
        bad_file = tmp_path / "future.json"
        bad_file.write_text(json.dumps({"backup_version": 999}))

        with pytest.raises(ValueError, match="newer than supported"):
            asyncio.run(import_backup(tmp_db, bad_file))

    def test_import_runs_migrations(self, tmp_db: Path, tmp_path: Path) -> None:
        """Import into a brand-new DB (no tables) runs migrations first."""
        asyncio.run(_seed_db(tmp_db))
        backup_file = tmp_path / "backup.json"
        asyncio.run(export_backup(tmp_db, backup_file))

        # Fresh DB with no tables
        fresh_db = tmp_path / "fresh.db"
        summary = asyncio.run(import_backup(fresh_db, backup_file))
        assert summary["profiles"] == 1


# ---------------------------------------------------------------------------
# DB snapshot restore tests
# ---------------------------------------------------------------------------

class TestRestoreDbSnapshot:

    def test_restore_snapshot(self, tmp_db: Path, tmp_path: Path) -> None:
        """Snapshot restore replaces the current DB file."""
        asyncio.run(_seed_db(tmp_db))

        # Take a snapshot
        snapshot = tmp_path / "snapshot.bak"
        shutil.copy2(str(tmp_db), str(snapshot))

        # Modify the live DB
        async def _modify():
            async with aiosqlite.connect(str(tmp_db)) as db:
                await db.execute("DELETE FROM profiles")
                await db.commit()
        asyncio.run(_modify())

        count = asyncio.run(_count_rows(tmp_db, "profiles"))
        assert count == 0

        # Restore
        restore_db_snapshot(snapshot, tmp_db)

        count = asyncio.run(_count_rows(tmp_db, "profiles"))
        assert count == 1

    def test_restore_nonexistent_snapshot_raises(self, tmp_db: Path, tmp_path: Path) -> None:
        with pytest.raises(FileNotFoundError):
            restore_db_snapshot(tmp_path / "nope.bak", tmp_db)

    def test_restore_invalid_file_raises(self, tmp_db: Path, tmp_path: Path) -> None:
        bad = tmp_path / "not_sqlite.bak"
        bad.write_text("this is not a database")

        with pytest.raises(ValueError, match="not .* valid SQLite"):
            restore_db_snapshot(bad, tmp_db)
