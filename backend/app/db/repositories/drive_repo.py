"""SQLite repository for drive monitoring tables."""
from __future__ import annotations

import json
import secrets
from datetime import datetime, timezone
from typing import Any

import aiosqlite

from app.models.drives import DriveRawAttribute, DriveSettings, DriveSettingsOverride, MediaType


class DriveRepo:
    def __init__(self, db: aiosqlite.Connection) -> None:
        self._db = db

    # ── drives table ────────────────────────────────────────────────────────

    async def upsert_drive(
        self,
        *,
        id: str,
        name: str,
        model: str,
        serial_full: str,
        device_path: str,
        bus_type: str,
        media_type: str,
        capacity_bytes: int,
        firmware_version: str,
        smart_available: bool,
        native_available: bool,
        supports_self_test: bool,
        supports_abort: bool,
    ) -> None:
        now = datetime.now(timezone.utc).isoformat()
        await self._db.execute(
            """
            INSERT INTO drives
                (id, name, model, serial_full, device_path, bus_type, media_type,
                 capacity_bytes, firmware_version, smart_available, native_available,
                 supports_self_test, supports_abort, last_seen_at, last_updated_at)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            ON CONFLICT(id) DO UPDATE SET
                name=excluded.name, model=excluded.model,
                serial_full=excluded.serial_full,
                device_path=excluded.device_path, bus_type=excluded.bus_type,
                media_type=excluded.media_type, capacity_bytes=excluded.capacity_bytes,
                firmware_version=excluded.firmware_version,
                smart_available=excluded.smart_available,
                native_available=excluded.native_available,
                supports_self_test=excluded.supports_self_test,
                supports_abort=excluded.supports_abort,
                last_seen_at=excluded.last_seen_at,
                last_updated_at=excluded.last_updated_at
            """,
            (
                id, name, model, serial_full, device_path, bus_type, media_type,
                capacity_bytes, firmware_version,
                int(smart_available), int(native_available),
                int(supports_self_test), int(supports_abort),
                now, now,
            ),
        )
        await self._db.commit()

    async def list_drives(self) -> list[dict[str, Any]]:
        cursor = await self._db.execute(
            "SELECT id, name, model, serial_full, device_path, bus_type, media_type, "
            "capacity_bytes, firmware_version, smart_available, native_available, "
            "supports_self_test, supports_abort, last_seen_at, last_updated_at "
            "FROM drives ORDER BY name ASC"
        )
        return [self._row_to_drive_dict(r) for r in await cursor.fetchall()]

    async def get_drive(self, drive_id: str) -> dict[str, Any] | None:
        cursor = await self._db.execute(
            "SELECT id, name, model, serial_full, device_path, bus_type, media_type, "
            "capacity_bytes, firmware_version, smart_available, native_available, "
            "supports_self_test, supports_abort, last_seen_at, last_updated_at "
            "FROM drives WHERE id = ?",
            (drive_id,),
        )
        row = await cursor.fetchone()
        return self._row_to_drive_dict(row) if row else None

    @staticmethod
    def _row_to_drive_dict(row: tuple) -> dict[str, Any]:
        return {
            "id": row[0],
            "name": row[1],
            "model": row[2],
            "serial_full": row[3],
            "device_path": row[4],
            "bus_type": row[5],
            "media_type": row[6],
            "capacity_bytes": row[7],
            "firmware_version": row[8],
            "smart_available": bool(row[9]),
            "native_available": bool(row[10]),
            "supports_self_test": bool(row[11]),
            "supports_abort": bool(row[12]),
            "last_seen_at": row[13],
            "last_updated_at": row[14],
        }

    # ── health snapshots ─────────────────────────────────────────────────────

    async def insert_health_snapshot(
        self,
        *,
        drive_id: str,
        temperature_c: float | None,
        health_status: str,
        health_percent: float | None,
        predicted_failure: bool,
        wear_percent_used: float | None,
        available_spare_percent: float | None,
        reallocated_sectors: int | None,
        pending_sectors: int | None,
        uncorrectable_errors: int | None,
        media_errors: int | None,
        power_on_hours: int | None,
        unsafe_shutdowns: int | None,
    ) -> None:
        now = datetime.now(timezone.utc).isoformat()
        await self._db.execute(
            """
            INSERT INTO drive_health_snapshots
                (drive_id, recorded_at, temperature_c, health_status, health_percent,
                 predicted_failure, wear_percent_used, available_spare_percent,
                 reallocated_sectors, pending_sectors, uncorrectable_errors,
                 media_errors, power_on_hours, unsafe_shutdowns)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            """,
            (
                drive_id, now, temperature_c, health_status, health_percent,
                int(predicted_failure), wear_percent_used, available_spare_percent,
                reallocated_sectors, pending_sectors, uncorrectable_errors,
                media_errors, power_on_hours, unsafe_shutdowns,
            ),
        )
        await self._db.commit()

    async def get_health_history(
        self, drive_id: str, hours: float = 168.0
    ) -> list[dict[str, Any]]:
        cursor = await self._db.execute(
            """
            SELECT recorded_at, temperature_c, health_status, health_percent,
                   predicted_failure, wear_percent_used, reallocated_sectors,
                   pending_sectors, uncorrectable_errors, media_errors
            FROM drive_health_snapshots
            WHERE drive_id = ?
              AND recorded_at >= datetime('now', ? || ' hours')
            ORDER BY recorded_at ASC
            """,
            (drive_id, f"-{hours:.4f}"),
        )
        cols = [
            "recorded_at", "temperature_c", "health_status", "health_percent",
            "predicted_failure", "wear_percent_used", "reallocated_sectors",
            "pending_sectors", "uncorrectable_errors", "media_errors",
        ]
        rows = await cursor.fetchall()
        return [dict(zip(cols, r)) for r in rows]

    async def prune_health_history(self, retention_hours: float) -> int:
        """Delete drive health snapshots older than retention_hours. Returns rows deleted."""
        cursor = await self._db.execute(
            "DELETE FROM drive_health_snapshots WHERE recorded_at < datetime('now', ? || ' hours')",
            (f"-{retention_hours:.4f}",),
        )
        await self._db.commit()
        return cursor.rowcount

    # ── attributes_latest ───────────────────────────────────────────────────

    async def upsert_attributes(
        self, drive_id: str, attributes: list[DriveRawAttribute]
    ) -> None:
        now = datetime.now(timezone.utc).isoformat()
        attrs_json = json.dumps([a.model_dump() for a in attributes])
        await self._db.execute(
            """
            INSERT INTO drive_attributes_latest (drive_id, captured_at, attributes_json)
            VALUES (?, ?, ?)
            ON CONFLICT(drive_id) DO UPDATE SET
                captured_at=excluded.captured_at,
                attributes_json=excluded.attributes_json
            """,
            (drive_id, now, attrs_json),
        )
        await self._db.commit()

    async def get_attributes(self, drive_id: str) -> list[dict[str, Any]]:
        cursor = await self._db.execute(
            "SELECT attributes_json FROM drive_attributes_latest WHERE drive_id = ?",
            (drive_id,),
        )
        row = await cursor.fetchone()
        if not row:
            return []
        try:
            return json.loads(row[0])
        except (json.JSONDecodeError, TypeError):
            return []

    # ── self-test runs ───────────────────────────────────────────────────────

    async def create_self_test_run(
        self,
        *,
        drive_id: str,
        test_type: str,
        provider_run_ref: str | None = None,
    ) -> str:
        run_id = secrets.token_hex(8)
        now = datetime.now(timezone.utc).isoformat()
        await self._db.execute(
            """
            INSERT INTO drive_self_test_runs
                (id, drive_id, type, status, started_at, provider_run_ref)
            VALUES (?, ?, ?, 'running', ?, ?)
            """,
            (run_id, drive_id, test_type, now, provider_run_ref),
        )
        await self._db.commit()
        return run_id

    async def update_self_test_run(
        self,
        run_id: str,
        *,
        status: str,
        progress_percent: float | None = None,
        failure_message: str | None = None,
        finished_at: str | None = None,
    ) -> None:
        now = datetime.now(timezone.utc).isoformat()
        await self._db.execute(
            """
            UPDATE drive_self_test_runs
            SET status=?, progress_percent=COALESCE(?, progress_percent),
                failure_message=?, finished_at=?
            WHERE id=?
            """,
            (
                status,
                progress_percent,
                failure_message,
                finished_at or (now if status in ("passed", "failed", "aborted") else None),
                run_id,
            ),
        )
        await self._db.commit()

    async def get_self_test_runs(
        self, drive_id: str, limit: int = 10
    ) -> list[dict[str, Any]]:
        cursor = await self._db.execute(
            """
            SELECT id, drive_id, type, status, progress_percent,
                   started_at, finished_at, failure_message, provider_run_ref
            FROM drive_self_test_runs
            WHERE drive_id = ?
            ORDER BY started_at DESC
            LIMIT ?
            """,
            (drive_id, limit),
        )
        cols = [
            "id", "drive_id", "type", "status", "progress_percent",
            "started_at", "finished_at", "failure_message", "provider_run_ref",
        ]
        return [dict(zip(cols, r)) for r in await cursor.fetchall()]

    async def get_self_test_run(self, run_id: str) -> dict[str, Any] | None:
        cursor = await self._db.execute(
            """
            SELECT id, drive_id, type, status, progress_percent,
                   started_at, finished_at, failure_message, provider_run_ref
            FROM drive_self_test_runs WHERE id = ?
            """,
            (run_id,),
        )
        row = await cursor.fetchone()
        if not row:
            return None
        cols = [
            "id", "drive_id", "type", "status", "progress_percent",
            "started_at", "finished_at", "failure_message", "provider_run_ref",
        ]
        return dict(zip(cols, row))

    async def get_running_self_tests(self) -> list[dict[str, Any]]:
        cursor = await self._db.execute(
            "SELECT id, drive_id, type, status, progress_percent, "
            "started_at, finished_at, failure_message, provider_run_ref "
            "FROM drive_self_test_runs WHERE status = 'running'"
        )
        cols = [
            "id", "drive_id", "type", "status", "progress_percent",
            "started_at", "finished_at", "failure_message", "provider_run_ref",
        ]
        return [dict(zip(cols, r)) for r in await cursor.fetchall()]

    # ── settings overrides ───────────────────────────────────────────────────

    async def get_drive_settings_override(
        self, drive_id: str
    ) -> dict[str, Any] | None:
        cursor = await self._db.execute(
            "SELECT drive_id, temp_warning_c, temp_critical_c, alerts_enabled, "
            "curve_picker_enabled, updated_at FROM drive_settings_overrides WHERE drive_id = ?",
            (drive_id,),
        )
        row = await cursor.fetchone()
        if not row:
            return None
        return {
            "drive_id": row[0],
            "temp_warning_c": row[1],
            "temp_critical_c": row[2],
            "alerts_enabled": None if row[3] is None else bool(row[3]),
            "curve_picker_enabled": None if row[4] is None else bool(row[4]),
            "updated_at": row[5],
        }

    async def upsert_drive_settings_override(
        self,
        drive_id: str,
        override: DriveSettingsOverride,
    ) -> None:
        now = datetime.now(timezone.utc).isoformat()
        await self._db.execute(
            """
            INSERT INTO drive_settings_overrides
                (drive_id, temp_warning_c, temp_critical_c, alerts_enabled,
                 curve_picker_enabled, updated_at)
            VALUES (?, ?, ?, ?, ?, ?)
            ON CONFLICT(drive_id) DO UPDATE SET
                temp_warning_c=COALESCE(excluded.temp_warning_c, temp_warning_c),
                temp_critical_c=COALESCE(excluded.temp_critical_c, temp_critical_c),
                alerts_enabled=COALESCE(excluded.alerts_enabled, alerts_enabled),
                curve_picker_enabled=COALESCE(excluded.curve_picker_enabled, curve_picker_enabled),
                updated_at=excluded.updated_at
            """,
            (
                drive_id,
                override.temp_warning_c,
                override.temp_critical_c,
                None if override.alerts_enabled is None else int(override.alerts_enabled),
                None if override.curve_picker_enabled is None else int(override.curve_picker_enabled),
                now,
            ),
        )
        await self._db.commit()
