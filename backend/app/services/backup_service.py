"""Portable JSON backup and restore for DriveChill configuration data.

Exports/imports: profiles, fan curves, settings, sensor labels, alert rules,
and quiet hours schedules.  Raw sensor history is excluded (too large).

The backup format is versioned via a top-level ``backup_version`` field so
that future changes to the schema can be handled gracefully.
"""

from __future__ import annotations

import json
import logging
import shutil
from datetime import datetime, timezone
from pathlib import Path

import aiosqlite

from app.config import settings as app_settings
from app.db.migration_runner import run_migrations

logger = logging.getLogger(__name__)

BACKUP_VERSION = 1


async def export_backup(db_path: Path, output_path: Path | None = None) -> Path:
    """Export all configuration data to a portable JSON file.

    Returns the path to the created backup file.
    """
    ts = datetime.now().strftime("%Y%m%d-%H%M%S")
    if output_path is None:
        output_path = Path(f"drivechill-backup-{ts}.json")

    async with aiosqlite.connect(str(db_path)) as db:
        db.row_factory = aiosqlite.Row

        # Profiles
        cursor = await db.execute(
            "SELECT id, name, preset, is_active, created_at, updated_at FROM profiles"
        )
        profiles = [dict(row) for row in await cursor.fetchall()]

        # Fan curves (including composite sensor_ids_json from migration 003)
        cursor = await db.execute(
            "SELECT id, profile_id, name, sensor_id, fan_id, enabled, points_json, "
            "sensor_ids_json FROM fan_curves"
        )
        fan_curves = [dict(row) for row in await cursor.fetchall()]

        # Settings
        cursor = await db.execute("SELECT key, value FROM settings")
        settings_rows = await cursor.fetchall()
        settings_dict = {row["key"]: row["value"] for row in settings_rows}

        # Sensor labels
        cursor = await db.execute("SELECT sensor_id, label FROM sensor_labels")
        labels_rows = await cursor.fetchall()
        sensor_labels = {row["sensor_id"]: row["label"] for row in labels_rows}

        # Alert rules
        cursor = await db.execute(
            "SELECT id, sensor_id, threshold, direction, enabled, "
            "cooldown_seconds, name FROM alert_rules"
        )
        alert_rules = [dict(row) for row in await cursor.fetchall()]

        # Fan settings (per-fan min speed floor, zero-RPM)
        cursor = await db.execute(
            "SELECT fan_id, min_speed_pct, zero_rpm_capable, updated_at "
            "FROM fan_settings"
        )
        fan_settings = [dict(row) for row in await cursor.fetchall()]

        # Quiet hours
        cursor = await db.execute(
            "SELECT id, day_of_week, start_time, end_time, profile_id, enabled "
            "FROM quiet_hours"
        )
        quiet_hours = [dict(row) for row in await cursor.fetchall()]

    backup = {
        "backup_version": BACKUP_VERSION,
        "app_version": app_settings.app_version,
        "created_at": datetime.now(timezone.utc).isoformat(),
        "profiles": profiles,
        "fan_curves": fan_curves,
        "fan_settings": fan_settings,
        "settings": settings_dict,
        "sensor_labels": sensor_labels,
        "alert_rules": alert_rules,
        "quiet_hours": quiet_hours,
    }

    output_path.write_text(json.dumps(backup, indent=2), encoding="utf-8")
    logger.info("Backup exported to %s", output_path)
    return output_path


async def import_backup(db_path: Path, backup_path: Path) -> dict[str, int]:
    """Import configuration data from a JSON backup file.

    Runs migrations first to ensure the schema is current, then replaces
    all configuration data with the contents of the backup.

    Returns a summary dict with counts of imported items.
    """
    file_size = backup_path.stat().st_size
    if file_size > 50 * 1024 * 1024:  # 50 MB cap
        raise ValueError(f"Backup file too large ({file_size} bytes). Maximum is 50 MB.")

    raw = backup_path.read_text(encoding="utf-8")
    data = json.loads(raw)

    if not isinstance(data, dict):
        raise ValueError("Invalid backup file: expected a JSON object at top level")

    version = data.get("backup_version")
    if version is None:
        raise ValueError("Invalid backup file: missing 'backup_version' field")
    if version > BACKUP_VERSION:
        raise ValueError(
            f"Backup version {version} is newer than supported version "
            f"{BACKUP_VERSION}. Update DriveChill before restoring."
        )

    # Ensure schema is current before importing
    await run_migrations(db_path)

    async with aiosqlite.connect(str(db_path)) as db:
        await db.execute("PRAGMA journal_mode=WAL")
        await db.execute("PRAGMA foreign_keys=OFF")  # defer FK checks during bulk load
        await db.execute("BEGIN IMMEDIATE")

        try:
            # --- Profiles & fan curves ---
            await db.execute("DELETE FROM fan_curves")
            await db.execute("DELETE FROM profiles")

            profiles = data.get("profiles", [])
            for p in profiles:
                await db.execute(
                    "INSERT INTO profiles (id, name, preset, is_active, created_at, updated_at) "
                    "VALUES (?, ?, ?, ?, ?, ?)",
                    (p["id"], p["name"], p["preset"], p.get("is_active", 0),
                     p.get("created_at", ""), p.get("updated_at", "")),
                )

            fan_curves = data.get("fan_curves", [])
            for c in fan_curves:
                await db.execute(
                    "INSERT INTO fan_curves (id, profile_id, name, sensor_id, fan_id, "
                    "enabled, points_json, sensor_ids_json) "
                    "VALUES (?, ?, ?, ?, ?, ?, ?, ?)",
                    (c["id"], c["profile_id"], c["name"], c["sensor_id"],
                     c["fan_id"], c.get("enabled", 1), c.get("points_json", "[]"),
                     c.get("sensor_ids_json", "[]")),
                )

            # --- Settings (full replacement — clear stale keys first) ---
            await db.execute("DELETE FROM settings")
            settings_dict = data.get("settings", {})
            for key, value in settings_dict.items():
                await db.execute(
                    "INSERT INTO settings (key, value, updated_at) "
                    "VALUES (?, ?, datetime('now'))",
                    (key, value),
                )

            # --- Sensor labels ---
            await db.execute("DELETE FROM sensor_labels")
            sensor_labels = data.get("sensor_labels", {})
            for sensor_id, label in sensor_labels.items():
                await db.execute(
                    "INSERT INTO sensor_labels (sensor_id, label, updated_at) "
                    "VALUES (?, ?, datetime('now'))",
                    (sensor_id, label),
                )

            # --- Fan settings (per-fan min speed floor, zero-RPM) ---
            await db.execute("DELETE FROM fan_settings")
            fan_settings = data.get("fan_settings", [])
            for fs in fan_settings:
                await db.execute(
                    "INSERT INTO fan_settings (fan_id, min_speed_pct, zero_rpm_capable, "
                    "updated_at) VALUES (?, ?, ?, ?)",
                    (fs["fan_id"], fs.get("min_speed_pct", 0),
                     fs.get("zero_rpm_capable", 0),
                     fs.get("updated_at", datetime.now(timezone.utc).isoformat())),
                )

            # --- Alert rules ---
            await db.execute("DELETE FROM alert_rules")
            alert_rules = data.get("alert_rules", [])
            for r in alert_rules:
                await db.execute(
                    "INSERT INTO alert_rules (id, sensor_id, threshold, direction, "
                    "enabled, cooldown_seconds, name) VALUES (?, ?, ?, ?, ?, ?, ?)",
                    (r["id"], r["sensor_id"], r["threshold"],
                     r.get("direction", "above"), r.get("enabled", 1),
                     r.get("cooldown_seconds", 300), r.get("name", "")),
                )

            # --- Quiet hours ---
            await db.execute("DELETE FROM quiet_hours")
            quiet_hours = data.get("quiet_hours", [])
            for q in quiet_hours:
                await db.execute(
                    "INSERT INTO quiet_hours (day_of_week, start_time, end_time, "
                    "profile_id, enabled) VALUES (?, ?, ?, ?, ?)",
                    (q["day_of_week"], q["start_time"], q["end_time"],
                     q["profile_id"], q.get("enabled", 1)),
                )

            # Validate referential integrity before committing.
            # FKs were disabled for bulk load, so check explicitly.
            cursor = await db.execute("PRAGMA foreign_key_check")
            violations = await cursor.fetchall()
            if violations:
                await db.rollback()
                tables = {v[0] for v in violations}
                raise ValueError(
                    f"Backup contains invalid foreign key references in: "
                    f"{', '.join(sorted(tables))}. Import aborted."
                )

            await db.commit()
        except Exception:
            await db.rollback()
            raise
        finally:
            await db.execute("PRAGMA foreign_keys=ON")

    summary = {
        "profiles": len(profiles),
        "fan_curves": len(fan_curves),
        "fan_settings": len(fan_settings),
        "settings": len(settings_dict),
        "sensor_labels": len(sensor_labels),
        "alert_rules": len(alert_rules),
        "quiet_hours": len(quiet_hours),
    }
    logger.info("Backup imported: %s", summary)
    return summary


def restore_db_snapshot(snapshot_path: Path, db_path: Path) -> None:
    """Restore a full SQLite database snapshot.

    Validates the snapshot is a real SQLite file before overwriting.
    """
    if not snapshot_path.exists():
        raise FileNotFoundError(f"Snapshot not found: {snapshot_path}")

    # Validate SQLite magic header (first 16 bytes)
    with open(snapshot_path, "rb") as f:
        header = f.read(16)
    if header[:15] != b"SQLite format 3":
        raise ValueError(
            f"File does not appear to be a valid SQLite database: {snapshot_path}"
        )

    shutil.copy2(str(snapshot_path), str(db_path))
    logger.info("Database restored from snapshot: %s", snapshot_path)
