"""Schema migration runner with auto-backup and rollback.

Reads numbered SQL files from the migrations/ directory, applies them in
order, and tracks the current schema version in a ``schema_version`` table.

Before applying any new migration the runner creates a timestamped copy of
the database file.  If a migration fails the backup is restored
automatically and the error is surfaced to the caller.
"""

from __future__ import annotations

import logging
import re
import shutil
from datetime import datetime, timezone
from pathlib import Path

import aiosqlite

logger = logging.getLogger(__name__)

MIGRATIONS_DIR = Path(__file__).parent / "migrations"


def _split_sql_statements(sql: str) -> list[str]:
    """Split a SQL script into individual non-empty statements.

    Strips line comments first so they don't interfere with the split.
    """
    no_comments = re.sub(r"--[^\n]*", "", sql)
    return [s.strip() for s in no_comments.split(";") if s.strip()]


async def _ensure_schema_version_table(db: aiosqlite.Connection) -> None:
    await db.execute("""
        CREATE TABLE IF NOT EXISTS schema_version (
            version INTEGER PRIMARY KEY,
            applied_at TEXT NOT NULL,
            description TEXT
        )
    """)
    await db.commit()


async def _get_current_version(db: aiosqlite.Connection) -> int:
    cursor = await db.execute(
        "SELECT COALESCE(MAX(version), 0) FROM schema_version"
    )
    row = await cursor.fetchone()
    return row[0] if row else 0


def _discover_migrations(directory: Path) -> list[tuple[int, str, Path]]:
    """Return sorted list of (version, description, path) from SQL files.

    File naming convention: ``NNN_description.sql`` where NNN is the
    zero-padded version number (e.g. ``001_initial_schema.sql``).
    """
    migrations: list[tuple[int, str, Path]] = []
    for sql_file in sorted(directory.glob("*.sql")):
        parts = sql_file.stem.split("_", 1)
        if len(parts) != 2:
            logger.warning("Skipping malformed migration filename: %s", sql_file.name)
            continue
        try:
            version = int(parts[0])
        except ValueError:
            logger.warning("Skipping non-numeric migration prefix: %s", sql_file.name)
            continue
        description = parts[1].replace("_", " ")
        migrations.append((version, description, sql_file))
    return migrations


def _backup_db(db_path: Path) -> Path:
    """Create a timestamped backup of the database file.

    Returns the path to the backup file.
    """
    ts = datetime.now().strftime("%Y%m%d-%H%M%S")
    backup_path = db_path.parent / f"{db_path.name}.bak-{ts}"
    shutil.copy2(str(db_path), str(backup_path))
    logger.info("Database backed up to %s", backup_path)
    return backup_path


def _restore_db(backup_path: Path, db_path: Path) -> None:
    """Restore the database from a backup file."""
    shutil.copy2(str(backup_path), str(db_path))
    logger.info("Database restored from %s", backup_path)


async def _fix_legacy_schema(db: aiosqlite.Connection) -> None:
    """Fix known schema issues from pre-migration database versions.

    The original LoggingService created sensor_log with a ``ts`` column;
    migration 001 expects ``timestamp``.  Rename if needed.
    """
    cursor = await db.execute("PRAGMA table_info(sensor_log)")
    columns = {row[1] for row in await cursor.fetchall()}
    if "ts" in columns and "timestamp" not in columns:
        logger.info("Legacy fix: renaming sensor_log.ts -> timestamp")
        await db.execute("ALTER TABLE sensor_log RENAME COLUMN ts TO timestamp")
        await db.commit()


async def run_migrations(db_path: Path) -> int:
    """Apply pending migrations to the database at *db_path*.

    Returns the number of migrations applied.  Raises ``RuntimeError`` if a
    migration fails (after restoring the backup).
    """
    migrations = _discover_migrations(MIGRATIONS_DIR)
    if not migrations:
        logger.info("No migration files found in %s", MIGRATIONS_DIR)
        # Still ensure schema_version table exists
        async with aiosqlite.connect(str(db_path)) as db:
            await _ensure_schema_version_table(db)
        return 0

    db_path.parent.mkdir(parents=True, exist_ok=True)

    async with aiosqlite.connect(str(db_path)) as db:
        await _ensure_schema_version_table(db)
        await _fix_legacy_schema(db)
        current_version = await _get_current_version(db)

    pending = [(v, desc, p) for v, desc, p in migrations if v > current_version]
    if not pending:
        logger.info("Database is up to date at version %d", current_version)
        return 0

    logger.info(
        "Found %d pending migration(s): %s",
        len(pending),
        ", ".join(str(v) for v, _, _ in pending),
    )

    # Backup before applying any migrations
    backup_path: Path | None = None
    if db_path.exists() and db_path.stat().st_size > 0:
        backup_path = _backup_db(db_path)

    applied = 0
    for version, description, sql_path in pending:
        sql = sql_path.read_text(encoding="utf-8")
        statements = _split_sql_statements(sql)
        logger.info(
            "Applying migration %03d: %s (%d statements)",
            version, description, len(statements),
        )
        try:
            async with aiosqlite.connect(str(db_path)) as db:
                # C-2: set WAL before writing; executescript() is avoided
                # because it issues an implicit COMMIT before executing and
                # cannot be rolled back as a unit.
                await db.execute("PRAGMA journal_mode=WAL")
                await db.execute("PRAGMA foreign_keys=ON")
                await db.execute("BEGIN")
                for stmt in statements:
                    await db.execute(stmt)
                await db.execute(
                    "INSERT INTO schema_version (version, applied_at, description) "
                    "VALUES (?, ?, ?)",
                    (version, datetime.now(timezone.utc).isoformat(), description),
                )
                await db.commit()
            applied += 1
        except Exception as exc:
            logger.error("Migration %03d failed: %s. Rolling back.", version, exc)
            # M-6: wrap restore so its own failure doesn't mask the original error
            if backup_path and backup_path.exists():
                try:
                    _restore_db(backup_path, db_path)
                    raise RuntimeError(
                        f"Migration {version:03d} ({description}) failed: {exc}. "
                        f"Database restored from backup."
                    ) from exc
                except RuntimeError:
                    raise
                except Exception as restore_exc:
                    raise RuntimeError(
                        f"Migration {version:03d} ({description}) failed: {exc}. "
                        f"Restore ALSO failed: {restore_exc}. Manual intervention required."
                    ) from exc
            raise RuntimeError(
                f"Migration {version:03d} ({description}) failed: {exc}. "
                f"No backup available."
            ) from exc

    logger.info(
        "Migrations complete. Database now at version %d (%d applied).",
        pending[-1][0],
        applied,
    )
    return applied
