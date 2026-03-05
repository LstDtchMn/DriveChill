"""Analytics v2.0 contract, validation, and retention-migration tests.

Tests contract shape (series dict, returned_range, anomaly severity),
HTTP-400 validation (dual sensor params, missing correlation sensors),
and retention defaults.

Uses asyncio.run() directly (matches perf test pattern) since pytest-asyncio
is not installed in this environment.
"""

from __future__ import annotations

import asyncio
import sys
from datetime import datetime, timezone, timedelta
from pathlib import Path
from unittest.mock import AsyncMock, MagicMock

import pytest

_backend_dir = Path(__file__).parent.parent
if str(_backend_dir) not in sys.path:
    sys.path.insert(0, str(_backend_dir))

import aiosqlite
from fastapi import HTTPException

from app.api.routes.analytics import (
    get_history,
    get_stats,
    get_anomalies,
    get_regression,
    get_correlation,
    get_report,
    _retention_hours,
)


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

async def _build_db(db_path: Path) -> aiosqlite.Connection:
    db = await aiosqlite.connect(str(db_path))
    await db.execute("""
        CREATE TABLE IF NOT EXISTS sensor_log (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            timestamp   TEXT    NOT NULL,
            sensor_id   TEXT    NOT NULL,
            sensor_name TEXT    NOT NULL,
            sensor_type TEXT    NOT NULL,
            value       TEXT    NOT NULL,
            unit        TEXT    NOT NULL DEFAULT '°C'
        )
    """)
    await db.execute("""
        CREATE TABLE IF NOT EXISTS settings (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL,
            updated_at TEXT NOT NULL DEFAULT (datetime('now'))
        )
    """)
    await db.commit()
    return db


async def _insert(db, sid, stype, values, hours_ago=2.0):
    now = datetime.now(timezone.utc)
    n = max(len(values), 1)
    fmt = "%Y-%m-%d %H:%M:%S"  # matches the format used by the analytics query
    for i, v in enumerate(values):
        ts = now - timedelta(hours=hours_ago * (n - i) / n)
        await db.execute(
            "INSERT INTO sensor_log (timestamp, sensor_id, sensor_name, sensor_type, value, unit)"
            " VALUES (?, ?, ?, ?, ?, ?)",
            (ts.strftime(fmt), sid, sid.upper(), stype, str(v), "°C"),
        )
    await db.commit()


def _req(db, retention_hours=720):
    req = MagicMock()
    req.app.state.db = db
    repo = MagicMock()
    repo.get_int = AsyncMock(return_value=retention_hours)
    req.app.state.settings_repo = repo
    return req


def _run(coro):
    return asyncio.run(coro)


# ---------------------------------------------------------------------------
# P1: series contract shape
# ---------------------------------------------------------------------------


def test_history_series_is_dict_keyed_by_sensor_id(tmp_path):
    """series must be a dict {sensor_id: [{timestamp, avg, min, max, count}]}."""
    async def _go():
        db = await _build_db(tmp_path / "c.db")
        try:
            await _insert(db, "cpu_temp_0", "cpu_temp", [50.0] * 30)
            await _insert(db, "gpu_temp_0", "gpu_temp", [60.0] * 30)

            result = await get_history(
                _req(db), hours=4.0, start=None, end=None,
                sensor_id=None, sensor_ids=None, bucket_seconds=None,
            )

            series = result["series"]
            assert isinstance(series, dict), "series must be a dict"
            assert "cpu_temp_0" in series
            assert "gpu_temp_0" in series

            for pts in series.values():
                assert isinstance(pts, list)
                assert len(pts) > 0
                pt = pts[0]
                assert set(pt.keys()) == {"timestamp", "avg", "min", "max", "count"}

            # Legacy buckets key must still be a flat list
            assert isinstance(result["buckets"], list)
        finally:
            await db.close()
    _run(_go())


def test_history_returned_range_reflects_actual_data(tmp_path):
    """returned_range.start must be >= requested_range.start when data is within window."""
    async def _go():
        db = await _build_db(tmp_path / "c.db")
        try:
            await _insert(db, "cpu_temp_0", "cpu_temp", [50.0] * 20, hours_ago=1.0)

            result = await get_history(
                _req(db), hours=24.0, start=None, end=None,
                sensor_id=None, sensor_ids=None, bucket_seconds=None,
            )
            req_start = result["requested_range"]["start"]
            ret_start = result["returned_range"]["start"]

            assert ret_start >= req_start, (
                f"returned_range.start ({ret_start}) should be >= requested_range.start ({req_start})"
            )
        finally:
            await db.close()
    _run(_go())


# ---------------------------------------------------------------------------
# P2: anomalies returned_range
# ---------------------------------------------------------------------------


def test_anomalies_has_returned_range(tmp_path):
    async def _go():
        db = await _build_db(tmp_path / "c.db")
        try:
            await _insert(db, "cpu_temp_0", "cpu_temp", [50.0] * 20)
            result = await get_anomalies(
                _req(db), hours=4.0, start=None, end=None,
                sensor_id=None, sensor_ids=None, z_score_threshold=3.0,
            )
            assert "returned_range" in result
            assert "start" in result["returned_range"]
            assert "end" in result["returned_range"]
        finally:
            await db.close()
    _run(_go())


# ---------------------------------------------------------------------------
# P2: correlation — x_sensor_id / y_sensor_id preferred params
# ---------------------------------------------------------------------------


def test_correlation_accepts_x_y_sensor_id_params(tmp_path):
    async def _go():
        db = await _build_db(tmp_path / "c.db")
        try:
            await _insert(db, "cpu_temp_0", "cpu_temp", [50.0 + i * 0.1 for i in range(60)])
            await _insert(db, "gpu_temp_0", "gpu_temp", [60.0 + i * 0.1 for i in range(60)])

            result = await get_correlation(
                _req(db),
                x_sensor_id="cpu_temp_0", y_sensor_id="gpu_temp_0",
                sensor_x=None, sensor_y=None,
                hours=4.0, start=None, end=None,
            )
            assert result["x_sensor_id"] == "cpu_temp_0"
            assert result["y_sensor_id"] == "gpu_temp_0"
            assert "correlation_coefficient" in result
            assert "requested_range" in result
        finally:
            await db.close()
    _run(_go())


def test_correlation_accepts_legacy_sensor_x_y_params(tmp_path):
    """sensor_x/sensor_y aliases must still work."""
    async def _go():
        db = await _build_db(tmp_path / "c.db")
        try:
            await _insert(db, "cpu_temp_0", "cpu_temp", [50.0] * 60)
            await _insert(db, "gpu_temp_0", "gpu_temp", [60.0] * 60)

            result = await get_correlation(
                _req(db),
                x_sensor_id=None, y_sensor_id=None,
                sensor_x="cpu_temp_0", sensor_y="gpu_temp_0",
                hours=4.0, start=None, end=None,
            )
            assert result["x_sensor_id"] == "cpu_temp_0"
            assert result["y_sensor_id"] == "gpu_temp_0"
        finally:
            await db.close()
    _run(_go())


def test_correlation_missing_sensors_raises_400(tmp_path):
    async def _go():
        db = await _build_db(tmp_path / "c.db")
        try:
            with pytest.raises(HTTPException) as exc:
                await get_correlation(
                    _req(db),
                    x_sensor_id=None, y_sensor_id=None,
                    sensor_x=None, sensor_y=None,
                    hours=4.0, start=None, end=None,
                )
            assert exc.value.status_code == 400
        finally:
            await db.close()
    _run(_go())


# ---------------------------------------------------------------------------
# P4: HTTP 400 on dual sensor_id + sensor_ids
# ---------------------------------------------------------------------------


def test_history_dual_sensor_params_raises_400(tmp_path):
    async def _go():
        db = await _build_db(tmp_path / "c.db")
        try:
            with pytest.raises(HTTPException) as exc:
                await get_history(
                    _req(db), hours=4.0, start=None, end=None,
                    sensor_id="cpu_temp_0", sensor_ids="gpu_temp_0",
                    bucket_seconds=None,
                )
            assert exc.value.status_code == 400
        finally:
            await db.close()
    _run(_go())


def test_stats_dual_sensor_params_raises_400(tmp_path):
    async def _go():
        db = await _build_db(tmp_path / "c.db")
        try:
            with pytest.raises(HTTPException) as exc:
                await get_stats(
                    _req(db), hours=4.0, start=None, end=None,
                    sensor_id="cpu_temp_0", sensor_ids="gpu_temp_0",
                )
            assert exc.value.status_code == 400
        finally:
            await db.close()
    _run(_go())


def test_anomalies_dual_sensor_params_raises_400(tmp_path):
    async def _go():
        db = await _build_db(tmp_path / "c.db")
        try:
            with pytest.raises(HTTPException) as exc:
                await get_anomalies(
                    _req(db), hours=4.0, start=None, end=None,
                    sensor_id="cpu_temp_0", sensor_ids="gpu_temp_0",
                    z_score_threshold=3.0,
                )
            assert exc.value.status_code == 400
        finally:
            await db.close()
    _run(_go())


def test_regression_dual_sensor_params_raises_400(tmp_path):
    async def _go():
        db = await _build_db(tmp_path / "c.db")
        try:
            with pytest.raises(HTTPException) as exc:
                await get_regression(
                    _req(db), baseline_days=30, recent_hours=24.0,
                    threshold_delta=5.0,
                    sensor_id="cpu_temp_0", sensor_ids="gpu_temp_0",
                )
            assert exc.value.status_code == 400
        finally:
            await db.close()
    _run(_go())


# ---------------------------------------------------------------------------
# P3: retention defaults
# ---------------------------------------------------------------------------


def test_retention_hours_fallback_is_720():
    """When settings_repo is absent, _retention_hours must return 720."""
    async def _go():
        req = MagicMock()
        # spec=[] means no attributes defined — accessing settings_repo returns MagicMock
        # We need getattr to return None, so spec the state object with no attrs
        state = object.__new__(object)  # plain object, no __dict__ attrs
        req.app.state = state
        hours = await _retention_hours(req)
        assert hours == 720
    _run(_go())


def test_retention_hours_uses_db_value(tmp_path):
    """_retention_hours reads the stored value from settings_repo."""
    async def _go():
        db = await _build_db(tmp_path / "c.db")
        try:
            req = _req(db, retention_hours=48)
            hours = await _retention_hours(req)
            assert hours == 48
        finally:
            await db.close()
    _run(_go())


# ---------------------------------------------------------------------------
# P2: report has returned_range
# ---------------------------------------------------------------------------


def test_report_has_returned_range(tmp_path):
    async def _go():
        db = await _build_db(tmp_path / "c.db")
        try:
            await _insert(db, "cpu_temp_0", "cpu_temp", [50.0] * 20)
            result = await get_report(
                _req(db), hours=4.0, start=None, end=None,
                sensor_id=None, sensor_ids=None,
            )
            assert "returned_range" in result
            assert "requested_range" in result
            assert "start" in result["returned_range"]
        finally:
            await db.close()
    _run(_go())


# ---------------------------------------------------------------------------
# Anomaly severity
# ---------------------------------------------------------------------------


def test_anomaly_severity_critical_vs_warning(tmp_path):
    """Anomalies far from mean (z > threshold * 1.5) must be 'critical'."""
    async def _go():
        db = await _build_db(tmp_path / "c.db")
        try:
            await _insert(db, "cpu_temp_0", "cpu_temp", [50.0] * 40, hours_ago=2.0)
            # Insert extreme outliers
            now = datetime.now(timezone.utc)
            fmt = "%Y-%m-%d %H:%M:%S"
            for v in [150.0, 150.0]:
                ts = (now - timedelta(minutes=5)).strftime(fmt)
                await db.execute(
                    "INSERT INTO sensor_log (timestamp, sensor_id, sensor_name, sensor_type, value, unit)"
                    " VALUES (?, ?, ?, ?, ?, ?)",
                    (ts, "cpu_temp_0", "CPU", "cpu_temp", str(v), "°C"),
                )
            await db.commit()

            result = await get_anomalies(
                _req(db), hours=4.0, start=None, end=None,
                sensor_id=None, sensor_ids=None, z_score_threshold=2.0,
            )
            severities = {a["severity"] for a in result["anomalies"]}
            assert "critical" in severities
        finally:
            await db.close()
    _run(_go())
