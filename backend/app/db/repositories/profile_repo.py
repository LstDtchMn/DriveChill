"""SQLite-backed repository for fan profiles and their curves."""

from __future__ import annotations

import json
import logging
import secrets
from datetime import datetime, timezone

import aiosqlite

from app.models.fan_curves import FanCurve, FanCurvePoint
from app.models.profiles import Profile, ProfilePreset, PRESET_CURVES

logger = logging.getLogger(__name__)


class ProfileRepo:
    def __init__(self, db: aiosqlite.Connection) -> None:
        self._db = db

    # ------------------------------------------------------------------
    # Profiles
    # ------------------------------------------------------------------

    async def list_all(self) -> list[Profile]:
        cursor = await self._db.execute(
            "SELECT id, name, preset, is_active FROM profiles ORDER BY name"
        )
        rows = await cursor.fetchall()
        profiles: list[Profile] = []
        for row in rows:
            curves = await self._get_curves_for_profile(row[0])
            profiles.append(Profile(
                id=row[0],
                name=row[1],
                preset=ProfilePreset(row[2]),
                is_active=bool(row[3]),
                curves=curves,
            ))
        return profiles

    async def get(self, profile_id: str) -> Profile | None:
        cursor = await self._db.execute(
            "SELECT id, name, preset, is_active FROM profiles WHERE id = ?",
            (profile_id,),
        )
        row = await cursor.fetchone()
        if not row:
            return None
        curves = await self._get_curves_for_profile(row[0])
        return Profile(
            id=row[0],
            name=row[1],
            preset=ProfilePreset(row[2]),
            is_active=bool(row[3]),
            curves=curves,
        )

    async def create(self, name: str, preset: ProfilePreset = ProfilePreset.CUSTOM,
                     curves: list[FanCurve] | None = None) -> Profile:
        # H-7: 12 hex chars = 48 bits of randomness, collision prob negligible
        pid = secrets.token_hex(6)
        now = datetime.now(timezone.utc).isoformat()
        await self._db.execute(
            "INSERT INTO profiles (id, name, preset, is_active, created_at, updated_at) "
            "VALUES (?, ?, ?, 0, ?, ?)",
            (pid, name, preset.value, now, now),
        )
        if curves:
            for curve in curves:
                await self._upsert_curve(pid, curve)
        await self._db.commit()
        return Profile(id=pid, name=name, preset=preset, curves=curves or [], is_active=False)

    async def delete(self, profile_id: str) -> bool:
        cursor = await self._db.execute(
            "DELETE FROM profiles WHERE id = ?", (profile_id,)
        )
        await self._db.commit()
        return cursor.rowcount > 0

    async def activate(self, profile_id: str) -> bool:
        # Verify the target profile exists before deactivating others.
        cursor = await self._db.execute(
            "SELECT 1 FROM profiles WHERE id = ?", (profile_id,)
        )
        if not await cursor.fetchone():
            return False
        # C-1: single atomic statement — no race window between
        # "deactivate all" and "activate one" on the shared connection.
        await self._db.execute(
            "UPDATE profiles SET is_active = (CASE WHEN id = ? THEN 1 ELSE 0 END)",
            (profile_id,),
        )
        await self._db.commit()
        return True

    async def get_active(self) -> Profile | None:
        cursor = await self._db.execute(
            "SELECT id FROM profiles WHERE is_active = 1 LIMIT 1"
        )
        row = await cursor.fetchone()
        if not row:
            return None
        return await self.get(row[0])

    # ------------------------------------------------------------------
    # Curves
    # ------------------------------------------------------------------

    async def _get_curves_for_profile(self, profile_id: str) -> list[FanCurve]:
        cursor = await self._db.execute(
            "SELECT id, name, sensor_id, fan_id, enabled, points_json, "
            "sensor_ids_json FROM fan_curves WHERE profile_id = ?",
            (profile_id,),
        )
        rows = await cursor.fetchall()
        curves: list[FanCurve] = []
        for row in rows:
            # M-2: a corrupt row must not block loading the rest of the profile
            try:
                points = [FanCurvePoint(**p) for p in json.loads(row[5])]
            except Exception as exc:
                logger.warning(
                    "Skipping corrupt curve %s in profile %s: %s",
                    row[0], profile_id, exc,
                )
                continue
            try:
                sensor_ids = json.loads(row[6]) if row[6] else []
            except Exception:
                sensor_ids = []
            curves.append(FanCurve(
                id=row[0], name=row[1], sensor_id=row[2],
                fan_id=row[3], enabled=bool(row[4]), points=points,
                sensor_ids=sensor_ids,
            ))
        return curves

    async def _upsert_curve(self, profile_id: str, curve: FanCurve) -> None:
        points_json = json.dumps([p.model_dump() for p in curve.points])
        sensor_ids_json = json.dumps(curve.sensor_ids)
        await self._db.execute(
            "INSERT OR REPLACE INTO fan_curves "
            "(id, profile_id, name, sensor_id, fan_id, enabled, points_json, "
            "sensor_ids_json) VALUES (?, ?, ?, ?, ?, ?, ?, ?)",
            (curve.id, profile_id, curve.name, curve.sensor_id,
             curve.fan_id, int(curve.enabled), points_json, sensor_ids_json),
        )

    async def set_curves(self, profile_id: str, curves: list[FanCurve]) -> None:
        """Replace all curves for a profile atomically.

        C-1: BEGIN IMMEDIATE prevents concurrent reads on the same connection
        from observing the gap between DELETE and INSERT.
        """
        await self._db.execute("BEGIN IMMEDIATE")
        try:
            await self._db.execute(
                "DELETE FROM fan_curves WHERE profile_id = ?", (profile_id,)
            )
            for curve in curves:
                await self._upsert_curve(profile_id, curve)
            now = datetime.now(timezone.utc).isoformat()
            await self._db.execute(
                "UPDATE profiles SET updated_at = ? WHERE id = ?", (now, profile_id)
            )
            await self._db.commit()
        except Exception:
            await self._db.rollback()
            raise

    # ------------------------------------------------------------------
    # Default seeding
    # ------------------------------------------------------------------

    async def seed_defaults(self) -> None:
        """Insert default preset profiles if the table is empty."""
        cursor = await self._db.execute("SELECT COUNT(*) FROM profiles")
        row = await cursor.fetchone()
        if row and row[0] > 0:
            return

        now = datetime.now(timezone.utc).isoformat()
        for preset in [ProfilePreset.SILENT, ProfilePreset.BALANCED,
                       ProfilePreset.PERFORMANCE, ProfilePreset.FULL_SPEED,
                       ProfilePreset.GAMING, ProfilePreset.RENDERING,
                       ProfilePreset.SLEEP]:
            pid = secrets.token_hex(6)
            is_active = 1 if preset == ProfilePreset.BALANCED else 0
            await self._db.execute(
                "INSERT INTO profiles (id, name, preset, is_active, created_at, updated_at) "
                "VALUES (?, ?, ?, ?, ?, ?)",
                (pid, preset.value.replace("_", " ").title(), preset.value,
                 is_active, now, now),
            )
        await self._db.commit()

    async def seed_missing_presets(self) -> int:
        """Insert any preset profiles that don't exist yet (for upgrades)."""
        cursor = await self._db.execute(
            "SELECT preset FROM profiles WHERE preset != 'custom'"
        )
        existing = {row[0] for row in await cursor.fetchall()}
        added = 0
        now = datetime.now(timezone.utc).isoformat()
        for preset in ProfilePreset:
            if preset == ProfilePreset.CUSTOM:
                continue
            if preset.value in existing:
                continue
            pid = secrets.token_hex(6)
            await self._db.execute(
                "INSERT INTO profiles (id, name, preset, is_active, created_at, updated_at) "
                "VALUES (?, ?, ?, 0, ?, ?)",
                (pid, preset.value.replace("_", " ").title(), preset.value, now, now),
            )
            added += 1
        if added:
            await self._db.commit()
            logger.info("Seeded %d new preset profile(s)", added)
        return added
