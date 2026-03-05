"""Tests for thermal regression alert detection.

The regression SQL queries:
  baseline_sql: WHERE timestamp >= baseline_since  (30 days ago, no upper bound)
  recent_sql:   WHERE timestamp >= recent_since    (last 24h)

So baseline_avg is a 30-day rolling average that *includes* the recent window.
Tests use ≥25 baseline readings spread over hours_ago=720 so that the last
reading falls at now−28.8h, outside the recent 24h window.  This ensures the
baseline sample count is correct and the expected deltas are predictable.
"""

from __future__ import annotations

import asyncio
import sys
from datetime import datetime, timezone, timedelta
from pathlib import Path
from unittest.mock import AsyncMock, MagicMock

import pytest

# Ensure backend imports resolve.
_backend_dir = Path(__file__).parent.parent
if str(_backend_dir) not in sys.path:
    sys.path.insert(0, str(_backend_dir))

import aiosqlite

from app.api.routes.analytics import get_regression


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

async def _init_db(db_path: Path) -> aiosqlite.Connection:
    """Create a minimal sensor_log table and return an open connection."""
    db = await aiosqlite.connect(str(db_path))
    await db.execute("""
        CREATE TABLE IF NOT EXISTS sensor_log (
            timestamp TEXT NOT NULL,
            sensor_id TEXT NOT NULL,
            sensor_name TEXT NOT NULL,
            sensor_type TEXT NOT NULL,
            value TEXT NOT NULL,
            unit TEXT NOT NULL DEFAULT '°C'
        )
    """)
    await db.commit()
    return db


async def _insert_readings(
    db: aiosqlite.Connection,
    sensor_id: str,
    sensor_name: str,
    sensor_type: str,
    values: list[float],
    hours_ago: float,
):
    """Insert readings spread evenly over the given hours-ago window."""
    now = datetime.now(timezone.utc)
    interval_count = max(len(values), 1)
    fmt = "%Y-%m-%d %H:%M:%S"
    for i, v in enumerate(values):
        ts = now - timedelta(hours=hours_ago * (interval_count - i) / interval_count)
        await db.execute(
            "INSERT INTO sensor_log (timestamp, sensor_id, sensor_name, sensor_type, value, unit) VALUES (?, ?, ?, ?, ?, ?)",
            (ts.strftime(fmt), sensor_id, sensor_name, sensor_type, str(v), "°C"),
        )
    await db.commit()


def _make_request(db: aiosqlite.Connection):
    """Build a mock Request with app.state.db set."""
    request = MagicMock()
    request.app.state.db = db
    repo = MagicMock()
    repo.get_int = AsyncMock(return_value=720)
    request.app.state.settings_repo = repo
    return request


# ---------------------------------------------------------------------------
# Tests
# ---------------------------------------------------------------------------


def test_no_regression_when_stable(tmp_path):
    """When recent avg ≈ baseline avg, no regressions should be flagged."""
    async def _go():
        db = await _init_db(tmp_path / "test.db")
        try:
            # 25 baseline readings outside the 24h window (last at now−28.8h)
            await _insert_readings(db, "cpu_temp_0", "CPU", "cpu_temp", [50.0] * 25, hours_ago=720)
            # 20 recent readings at the same temp
            await _insert_readings(db, "cpu_temp_0", "CPU", "cpu_temp", [50.0] * 20, hours_ago=24)
            request = _make_request(db)
            result = await get_regression(request, baseline_days=30, recent_hours=24,
                                          threshold_delta=5.0, sensor_id=None, sensor_ids=None)
            assert result["regressions"] == []
        finally:
            await db.close()
    asyncio.run(_go())


def test_warning_regression(tmp_path):
    """A delta >= threshold but < 2x threshold should be 'warning' severity.

    25 baseline readings at 50°C + 20 recent at 60°C:
      baseline_avg (all 30d) = (25*50 + 20*60) / 45 ≈ 54.4°C
      recent_avg             = 60°C
      delta                  ≈ 5.6°C  → warning (≥5, <10)
    """
    async def _go():
        db = await _init_db(tmp_path / "test.db")
        try:
            await _insert_readings(db, "cpu_temp_0", "CPU", "cpu_temp", [50.0] * 25, hours_ago=720)
            await _insert_readings(db, "cpu_temp_0", "CPU", "cpu_temp", [60.0] * 20, hours_ago=24)
            request = _make_request(db)
            result = await get_regression(request, baseline_days=30, recent_hours=24,
                                          threshold_delta=5.0, sensor_id=None, sensor_ids=None)
            assert len(result["regressions"]) == 1
            reg = result["regressions"][0]
            assert reg["severity"] == "warning"
            assert reg["sensor_id"] == "cpu_temp_0"
            assert reg["delta"] >= 5.0
        finally:
            await db.close()
    asyncio.run(_go())


def test_critical_regression(tmp_path):
    """A delta >= 2x threshold should be 'critical' severity.

    25 baseline readings at 50°C + 20 recent at 72°C:
      baseline_avg ≈ (25*50 + 20*72) / 45 ≈ 59.8°C
      recent_avg             = 72°C
      delta                  ≈ 12.2°C  → critical (≥10)
    """
    async def _go():
        db = await _init_db(tmp_path / "test.db")
        try:
            await _insert_readings(db, "cpu_temp_0", "CPU", "cpu_temp", [50.0] * 25, hours_ago=720)
            await _insert_readings(db, "cpu_temp_0", "CPU", "cpu_temp", [72.0] * 20, hours_ago=24)
            request = _make_request(db)
            result = await get_regression(request, baseline_days=30, recent_hours=24,
                                          threshold_delta=5.0, sensor_id=None, sensor_ids=None)
            assert len(result["regressions"]) == 1
            assert result["regressions"][0]["severity"] == "critical"
        finally:
            await db.close()
    asyncio.run(_go())


def test_per_sensor_independent(tmp_path):
    """CPU can regress while GPU stays stable — only CPU should appear."""
    async def _go():
        db = await _init_db(tmp_path / "test.db")
        try:
            # CPU baseline 50°C, recent 60°C → delta ≈ 5.6 → regression
            await _insert_readings(db, "cpu_temp_0", "CPU", "cpu_temp", [50.0] * 25, hours_ago=720)
            await _insert_readings(db, "cpu_temp_0", "CPU", "cpu_temp", [60.0] * 20, hours_ago=24)
            # GPU baseline 45°C, recent 45°C → delta = 0 → stable
            await _insert_readings(db, "gpu_temp_0", "GPU", "gpu_temp", [45.0] * 25, hours_ago=720)
            await _insert_readings(db, "gpu_temp_0", "GPU", "gpu_temp", [45.0] * 20, hours_ago=24)
            request = _make_request(db)
            result = await get_regression(request, baseline_days=30, recent_hours=24,
                                          threshold_delta=5.0, sensor_id=None, sensor_ids=None)
            assert len(result["regressions"]) == 1
            assert result["regressions"][0]["sensor_id"] == "cpu_temp_0"
        finally:
            await db.close()
    asyncio.run(_go())


def test_insufficient_baseline_returns_empty(tmp_path):
    """Fewer than 10 baseline samples should not produce a regression.

    With only 5 old readings, the combined baseline avg is pulled toward the
    recent high values, keeping the delta below the threshold.
    """
    async def _go():
        db = await _init_db(tmp_path / "test.db")
        try:
            # 5 baseline readings (last at now−144h, well outside recent 24h)
            await _insert_readings(db, "cpu_temp_0", "CPU", "cpu_temp", [50.0] * 5, hours_ago=720)
            await _insert_readings(db, "cpu_temp_0", "CPU", "cpu_temp", [70.0] * 20, hours_ago=24)
            request = _make_request(db)
            result = await get_regression(request, baseline_days=30, recent_hours=24,
                                          threshold_delta=5.0, sensor_id=None, sensor_ids=None)
            assert result["regressions"] == []
        finally:
            await db.close()
    asyncio.run(_go())


def test_parameter_clamping(tmp_path):
    """Parameters outside valid ranges should be clamped, not error."""
    async def _go():
        db = await _init_db(tmp_path / "test.db")
        try:
            request = _make_request(db)
            result = await get_regression(
                request, baseline_days=200, recent_hours=500, threshold_delta=100,
                sensor_id=None, sensor_ids=None,
            )
            # Clamped: baseline_days -> 90, recent_hours -> 168
            assert result["baseline_period_days"] == 90
            assert result["recent_period_hours"] == 168.0
        finally:
            await db.close()
    asyncio.run(_go())
