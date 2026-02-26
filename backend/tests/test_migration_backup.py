"""Tests for migration auto-backup and rollback (v1.0 release gate v1.0-17).

v1.0-17 procedure:
  Introduce a deliberately failing migration after backup step. Start backend
  upgrade.

v1.0-17 pass criteria:
  - Timestamped .db.bak snapshot is created before migration.
  - On failure, DB is restored from snapshot automatically.
  - Startup aborts with clear error.
  - Pre-upgrade data remains intact.
"""

from __future__ import annotations

import asyncio
from pathlib import Path

import aiosqlite
import pytest

from app.db.migration_runner import run_migrations, MIGRATIONS_DIR


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

async def _setup_db_at_version(db_path: Path, version: int) -> None:
    """Create a DB with migrations applied up to *version* and seed test data."""
    await run_migrations(db_path)

    # Insert test data we can verify survives rollback
    async with aiosqlite.connect(str(db_path)) as db:
        await db.execute(
            "INSERT OR REPLACE INTO settings (key, value, updated_at) "
            "VALUES ('test_key', 'test_value', datetime('now'))"
        )
        await db.commit()


async def _get_setting(db_path: Path, key: str) -> str | None:
    async with aiosqlite.connect(str(db_path)) as db:
        cursor = await db.execute(
            "SELECT value FROM settings WHERE key = ?", (key,)
        )
        row = await cursor.fetchone()
        return row[0] if row else None


async def _get_schema_version(db_path: Path) -> int:
    async with aiosqlite.connect(str(db_path)) as db:
        cursor = await db.execute(
            "SELECT COALESCE(MAX(version), 0) FROM schema_version"
        )
        row = await cursor.fetchone()
        return row[0] if row else 0


# ---------------------------------------------------------------------------
# Tests
# ---------------------------------------------------------------------------

class TestMigrationAutoBackup:
    """v1.0-17: Migration auto-backup + rollback."""

    def test_backup_created_before_migration(self, tmp_db: Path) -> None:
        """A timestamped .bak file is created before any migration runs."""
        asyncio.run(_setup_db_at_version(tmp_db, 2))

        # Verify no extra .bak files yet (run_migrations creates one only
        # when there are pending migrations against a non-empty DB).
        bak_files = list(tmp_db.parent.glob("*.bak-*"))
        # The first run on a fresh DB may or may not create a backup
        # (the DB was empty before migration 001).  To test backup creation
        # reliably, add a failing migration and trigger a second run.

        # Write a deliberately failing migration 999
        bad_migration = MIGRATIONS_DIR / "999_will_fail.sql"
        bad_migration.write_text(
            "CREATE TABLE this_will_work (id INTEGER);\n"
            "THIS IS NOT VALID SQL;\n",
            encoding="utf-8",
        )

        try:
            with pytest.raises(RuntimeError, match="Migration 999"):
                asyncio.run(run_migrations(tmp_db))

            # .bak file should exist now
            bak_files = list(tmp_db.parent.glob("*.bak-*"))
            assert len(bak_files) >= 1, "No backup file was created before migration"
        finally:
            bad_migration.unlink(missing_ok=True)

    def test_rollback_on_failure(self, tmp_db: Path) -> None:
        """On migration failure, DB is restored from backup and data is intact."""
        asyncio.run(_setup_db_at_version(tmp_db, 2))

        # Confirm test data exists
        val = asyncio.run(_get_setting(tmp_db, "test_key"))
        assert val == "test_value"

        ver_before = asyncio.run(_get_schema_version(tmp_db))

        # Write failing migration
        bad_migration = MIGRATIONS_DIR / "999_will_fail.sql"
        bad_migration.write_text("INVALID SQL STATEMENT;", encoding="utf-8")

        try:
            with pytest.raises(RuntimeError, match="failed"):
                asyncio.run(run_migrations(tmp_db))

            # Data should be intact after rollback
            val_after = asyncio.run(_get_setting(tmp_db, "test_key"))
            assert val_after == "test_value", "Test data was lost after rollback"

            # Schema version should not have advanced
            ver_after = asyncio.run(_get_schema_version(tmp_db))
            assert ver_after == ver_before, (
                f"Schema version advanced from {ver_before} to {ver_after} "
                f"despite migration failure"
            )
        finally:
            bad_migration.unlink(missing_ok=True)

    def test_clear_error_message_on_failure(self, tmp_db: Path) -> None:
        """RuntimeError message includes migration number, description, and
        confirmation that the database was restored."""
        asyncio.run(_setup_db_at_version(tmp_db, 2))

        bad_migration = MIGRATIONS_DIR / "999_will_fail.sql"
        bad_migration.write_text("NOT REAL SQL;", encoding="utf-8")

        try:
            with pytest.raises(RuntimeError) as exc_info:
                asyncio.run(run_migrations(tmp_db))

            msg = str(exc_info.value)
            assert "999" in msg
            assert "restored" in msg.lower() or "backup" in msg.lower()
        finally:
            bad_migration.unlink(missing_ok=True)

    def test_normal_migration_applies_cleanly(self, tmp_db: Path) -> None:
        """Migrations apply without error on a fresh database."""
        applied = asyncio.run(run_migrations(tmp_db))
        assert applied >= 1

        version = asyncio.run(_get_schema_version(tmp_db))
        assert version >= 1

    def test_idempotent_rerun(self, tmp_db: Path) -> None:
        """Running migrations twice applies zero the second time."""
        asyncio.run(run_migrations(tmp_db))
        applied = asyncio.run(run_migrations(tmp_db))
        assert applied == 0
