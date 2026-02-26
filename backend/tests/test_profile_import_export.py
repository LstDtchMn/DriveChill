"""Tests for profile import/export (v1.0 must-ship feature).

Covers: exporting a profile as JSON, importing from JSON, curve ID reassignment.
"""
from __future__ import annotations

import asyncio
import json

import aiosqlite
import pytest

from app.db.repositories.profile_repo import ProfileRepo
from app.models.fan_curves import FanCurve, FanCurvePoint
from app.models.profiles import ProfilePreset


async def _init_db(db_path) -> aiosqlite.Connection:
    db = await aiosqlite.connect(str(db_path))
    await db.execute("PRAGMA foreign_keys=ON")
    await db.executescript("""
        CREATE TABLE IF NOT EXISTS profiles (
            id          TEXT PRIMARY KEY,
            name        TEXT NOT NULL,
            preset      TEXT NOT NULL DEFAULT 'custom',
            is_active   INTEGER NOT NULL DEFAULT 0,
            created_at  TEXT NOT NULL DEFAULT (datetime('now')),
            updated_at  TEXT NOT NULL DEFAULT (datetime('now'))
        );
        CREATE TABLE IF NOT EXISTS fan_curves (
            id          TEXT PRIMARY KEY,
            profile_id  TEXT NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
            name        TEXT NOT NULL,
            sensor_id   TEXT NOT NULL,
            fan_id      TEXT NOT NULL,
            enabled     INTEGER NOT NULL DEFAULT 1,
            points_json TEXT NOT NULL DEFAULT '[]',
            sensor_ids_json TEXT
        );
        CREATE INDEX IF NOT EXISTS idx_fan_curves_profile ON fan_curves(profile_id);
    """)
    await db.commit()
    return db


class TestProfileExport:

    def test_export_contains_name_and_curves(self, tmp_db) -> None:
        """Exported profile includes name, preset, and all curve data."""
        async def run():
            db = await _init_db(tmp_db)
            repo = ProfileRepo(db)
            curves = [FanCurve(
                id="curve_abc",
                name="CPU Fan",
                sensor_id="cpu_temp_0",
                fan_id="fan_cpu",
                points=[
                    FanCurvePoint(temp=30, speed=20),
                    FanCurvePoint(temp=70, speed=80),
                ],
                sensor_ids=["cpu_temp_0", "gpu_temp_0"],
            )]
            profile = await repo.create(
                name="My Gaming Profile",
                preset=ProfilePreset.CUSTOM,
                curves=curves,
            )

            # Simulate export: fetch profile and serialize
            fetched = await repo.get(profile.id)
            assert fetched is not None
            export_data = {
                "export_version": 1,
                "profile": {
                    "name": fetched.name,
                    "preset": fetched.preset.value,
                    "curves": [c.model_dump() for c in fetched.curves],
                },
            }

            assert export_data["profile"]["name"] == "My Gaming Profile"
            assert export_data["profile"]["preset"] == "custom"
            assert len(export_data["profile"]["curves"]) == 1
            assert export_data["profile"]["curves"][0]["sensor_ids"] == ["cpu_temp_0", "gpu_temp_0"]
            assert len(export_data["profile"]["curves"][0]["points"]) == 2
            await db.close()

        asyncio.run(run())


class TestProfileImport:

    def test_import_creates_new_profile(self, tmp_db) -> None:
        """Importing profile JSON creates a new profile in the DB."""
        async def run():
            db = await _init_db(tmp_db)
            repo = ProfileRepo(db)

            import_data = {
                "name": "Imported Silent",
                "preset": "silent",
                "curves": [
                    {
                        "id": "original_id_123",
                        "name": "CPU Silent",
                        "sensor_id": "cpu_temp_0",
                        "fan_id": "fan_cpu",
                        "points": [
                            {"temp": 0, "speed": 10},
                            {"temp": 80, "speed": 60},
                        ],
                        "enabled": True,
                        "sensor_ids": [],
                    }
                ],
            }

            curves = [FanCurve(**c) for c in import_data["curves"]]
            # Import assigns fresh IDs
            import secrets
            fresh_curves = [
                c.model_copy(update={"id": f"curve_{secrets.token_hex(6)}"})
                for c in curves
            ]
            profile = await repo.create(
                name=import_data["name"],
                preset=ProfilePreset(import_data["preset"]),
                curves=fresh_curves,
            )

            # Verify created
            fetched = await repo.get(profile.id)
            assert fetched is not None
            assert fetched.name == "Imported Silent"
            assert fetched.preset == ProfilePreset.SILENT
            assert len(fetched.curves) == 1
            assert fetched.curves[0].id != "original_id_123"  # Fresh ID
            assert len(fetched.curves[0].points) == 2
            await db.close()

        asyncio.run(run())

    def test_import_does_not_collide_with_existing(self, tmp_db) -> None:
        """Importing the same profile twice creates two separate profiles."""
        async def run():
            db = await _init_db(tmp_db)
            repo = ProfileRepo(db)

            import_data = {
                "name": "Test Profile",
                "preset": "balanced",
                "curves": [],
            }

            p1 = await repo.create(
                name=import_data["name"],
                preset=ProfilePreset(import_data["preset"]),
            )
            p2 = await repo.create(
                name=import_data["name"],
                preset=ProfilePreset(import_data["preset"]),
            )

            assert p1.id != p2.id
            all_profiles = await repo.list_all()
            assert len(all_profiles) == 2
            await db.close()

        asyncio.run(run())

    def test_round_trip_export_import(self, tmp_db) -> None:
        """A profile exported as JSON and re-imported is equivalent."""
        async def run():
            db = await _init_db(tmp_db)
            repo = ProfileRepo(db)

            # Create original
            curves = [FanCurve(
                id="curve_orig",
                name="All Fan",
                sensor_id="cpu_temp_0",
                fan_id="fan_all",
                points=[
                    FanCurvePoint(temp=20, speed=15),
                    FanCurvePoint(temp=50, speed=50),
                    FanCurvePoint(temp=85, speed=100),
                ],
                sensor_ids=["cpu_temp_0"],
            )]
            original = await repo.create(
                name="Round Trip",
                preset=ProfilePreset.PERFORMANCE,
                curves=curves,
            )

            # Export
            fetched = await repo.get(original.id)
            export_json = json.dumps({
                "export_version": 1,
                "profile": {
                    "name": fetched.name,
                    "preset": fetched.preset.value,
                    "curves": [c.model_dump() for c in fetched.curves],
                },
            })

            # Import
            data = json.loads(export_json)["profile"]
            import secrets
            imported_curves = [
                FanCurve(**c).model_copy(update={"id": f"curve_{secrets.token_hex(6)}"})
                for c in data["curves"]
            ]
            imported = await repo.create(
                name=data["name"],
                preset=ProfilePreset(data["preset"]),
                curves=imported_curves,
            )

            # Compare
            imp = await repo.get(imported.id)
            assert imp.name == fetched.name
            assert imp.preset == fetched.preset
            assert len(imp.curves) == len(fetched.curves)
            assert len(imp.curves[0].points) == len(fetched.curves[0].points)
            for i, pt in enumerate(imp.curves[0].points):
                assert pt.temp == fetched.curves[0].points[i].temp
                assert pt.speed == fetched.curves[0].points[i].speed
            await db.close()

        asyncio.run(run())
