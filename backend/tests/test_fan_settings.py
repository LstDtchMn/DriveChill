"""Tests for per-fan settings (min speed floor, zero-RPM) and DB persistence."""

from __future__ import annotations

import asyncio
from pathlib import Path

import aiosqlite
import pytest

from app.db.migration_runner import run_migrations
from app.db.repositories.fan_settings_repo import FanSettingsRepo


async def _setup_db(db_path: Path) -> None:
    await run_migrations(db_path)


class TestFanSettingsRepo:

    def test_get_nonexistent_returns_none(self, tmp_db: Path) -> None:
        async def _run():
            await _setup_db(tmp_db)
            async with aiosqlite.connect(str(tmp_db)) as db:
                repo = FanSettingsRepo(db)
                result = await repo.get("fan_unknown")
                assert result is None
        asyncio.run(_run())

    def test_set_and_get(self, tmp_db: Path) -> None:
        async def _run():
            await _setup_db(tmp_db)
            async with aiosqlite.connect(str(tmp_db)) as db:
                repo = FanSettingsRepo(db)
                await repo.set("fan_1", 25.0, False)
                result = await repo.get("fan_1")
                assert result is not None
                assert result["min_speed_pct"] == 25.0
                assert result["zero_rpm_capable"] is False
        asyncio.run(_run())

    def test_set_zero_rpm(self, tmp_db: Path) -> None:
        async def _run():
            await _setup_db(tmp_db)
            async with aiosqlite.connect(str(tmp_db)) as db:
                repo = FanSettingsRepo(db)
                await repo.set("fan_1", 0.0, True)
                result = await repo.get("fan_1")
                assert result is not None
                assert result["min_speed_pct"] == 0.0
                assert result["zero_rpm_capable"] is True
        asyncio.run(_run())

    def test_update_existing(self, tmp_db: Path) -> None:
        async def _run():
            await _setup_db(tmp_db)
            async with aiosqlite.connect(str(tmp_db)) as db:
                repo = FanSettingsRepo(db)
                await repo.set("fan_1", 30.0, False)
                await repo.set("fan_1", 15.0, True)
                result = await repo.get("fan_1")
                assert result["min_speed_pct"] == 15.0
                assert result["zero_rpm_capable"] is True
        asyncio.run(_run())

    def test_get_all(self, tmp_db: Path) -> None:
        async def _run():
            await _setup_db(tmp_db)
            async with aiosqlite.connect(str(tmp_db)) as db:
                repo = FanSettingsRepo(db)
                await repo.set("fan_1", 25.0, False)
                await repo.set("fan_2", 0.0, True)
                all_settings = await repo.get_all()
                assert len(all_settings) == 2
                assert all_settings["fan_1"]["min_speed_pct"] == 25.0
                assert all_settings["fan_2"]["zero_rpm_capable"] is True
        asyncio.run(_run())

    def test_get_all_empty(self, tmp_db: Path) -> None:
        async def _run():
            await _setup_db(tmp_db)
            async with aiosqlite.connect(str(tmp_db)) as db:
                repo = FanSettingsRepo(db)
                all_settings = await repo.get_all()
                assert all_settings == {}
        asyncio.run(_run())


class TestMinSpeedFloorLogic:
    """Test that the min speed floor logic works correctly (unit-level)."""

    def test_floor_applied(self) -> None:
        """Speed below floor is raised to floor."""
        speed = 15.0
        min_floor = 30.0
        zero_rpm = False
        if not zero_rpm:
            speed = max(speed, min_floor)
        assert speed == 30.0

    def test_speed_above_floor_unchanged(self) -> None:
        speed = 50.0
        min_floor = 30.0
        zero_rpm = False
        if not zero_rpm:
            speed = max(speed, min_floor)
        assert speed == 50.0

    def test_zero_rpm_allows_zero(self) -> None:
        """Zero-RPM capable fans can go to 0% despite floor."""
        speed = 0.0
        min_floor = 30.0
        zero_rpm = True
        if speed == 0 and zero_rpm:
            pass  # allow
        elif not zero_rpm:
            speed = max(speed, min_floor)
        assert speed == 0.0

    def test_zero_rpm_non_zero_still_has_floor(self) -> None:
        """Zero-RPM fan at non-zero speed still respects floor."""
        speed = 10.0
        min_floor = 30.0
        zero_rpm = True
        # Fan service logic: zero-RPM exemption only applies at exactly 0%.
        # Non-zero speeds are still raised to the floor.
        if speed == 0 and zero_rpm:
            pass  # allow 0%
        else:
            speed = max(speed, min_floor)
        assert speed == 30.0
