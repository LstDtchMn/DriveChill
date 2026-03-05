"""Cross-backend parity: Python and C# analytics endpoints must return identical shapes.

Strategy
--------
- Python backend is exercised in-process (direct function calls, same as other tests).
- C# backend is exercised over HTTP when CSHARP_BASE_URL is set in the environment;
  those tests are skipped otherwise.

Minimum assertions (per feedback §3):
  - same top-level keys
  - same `series` shape
  - same query validation (dual sensor_id+sensor_ids → HTTP 400)
  - same metadata fields present
  - preferred correlation params (x_sensor_id/y_sensor_id) accepted
  - legacy aliases (sensor_x/sensor_y) still accepted

Run against a live C# backend:
  CSHARP_BASE_URL=http://localhost:8086 pytest tests/test_analytics_cross_backend_parity.py -v
"""
from __future__ import annotations

import asyncio
import os
import sys
from datetime import datetime, timezone, timedelta
from pathlib import Path
from typing import Any
from unittest.mock import AsyncMock, MagicMock

import pytest

_backend_dir = Path(__file__).parent.parent
if str(_backend_dir) not in sys.path:
    sys.path.insert(0, str(_backend_dir))

import aiosqlite

from app.api.routes.analytics import (
    get_history,
    get_stats,
    get_anomalies,
    get_regression,
    get_correlation,
    get_report,
)

# ---------------------------------------------------------------------------
# Optional: C# live-backend URL
# ---------------------------------------------------------------------------

CSHARP_BASE_URL: str | None = os.environ.get("CSHARP_BASE_URL")
_cs_available = CSHARP_BASE_URL is not None

# ---------------------------------------------------------------------------
# Shared helpers (identical to test_analytics_contract.py)
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


async def _insert(
    db: aiosqlite.Connection,
    sid: str,
    stype: str,
    values: list[float],
    hours_ago: float = 2.0,
) -> None:
    now = datetime.now(timezone.utc)
    n   = max(len(values), 1)
    fmt = "%Y-%m-%d %H:%M:%S"
    for i, v in enumerate(values):
        ts = now - timedelta(hours=hours_ago * (n - i) / n)
        await db.execute(
            "INSERT INTO sensor_log (timestamp, sensor_id, sensor_name, sensor_type, value, unit)"
            " VALUES (?, ?, ?, ?, ?, ?)",
            (ts.strftime(fmt), sid, sid.upper(), stype, str(v), "°C"),
        )
    await db.commit()


def _req(db: aiosqlite.Connection, retention_hours: int = 720) -> MagicMock:
    req = MagicMock()
    req.app.state.db = db
    repo = MagicMock()
    repo.get_int = AsyncMock(return_value=retention_hours)
    req.app.state.settings_repo = repo
    return req


# ---------------------------------------------------------------------------
# Shape assertions (backend-agnostic)
# ---------------------------------------------------------------------------

def _assert_history_shape(result: dict[str, Any]) -> None:
    """Assert the history response has the canonical v2.0 shape."""
    assert "buckets"          in result, "missing key: buckets"
    assert "series"           in result, "missing key: series"
    assert "bucket_seconds"   in result, "missing key: bucket_seconds"
    assert "requested_range"  in result, "missing key: requested_range"
    assert "returned_range"   in result, "missing key: returned_range"
    assert "retention_limited" in result, "missing key: retention_limited"

    series = result["series"]
    assert isinstance(series, dict), "series must be a dict"
    for sid, pts in series.items():
        assert isinstance(pts, list), f"series[{sid!r}] must be a list"
        for pt in pts:
            assert "timestamp" in pt,  f"series point missing 'timestamp': {pt}"
            assert "avg"       in pt,  f"series point missing 'avg': {pt}"
            assert "min"       in pt,  f"series point missing 'min': {pt}"
            assert "max"       in pt,  f"series point missing 'max': {pt}"
            assert "count"     in pt,  f"series point missing 'count': {pt}"

    for key in ("start", "end"):
        assert key in result["requested_range"], f"requested_range missing '{key}'"
        assert key in result["returned_range"],  f"returned_range missing '{key}'"


def _assert_stats_shape(result: dict[str, Any]) -> None:
    assert "stats"           in result, "missing key: stats"
    assert "requested_range" in result, "missing key: requested_range"
    assert "returned_range"  in result, "missing key: returned_range"
    for s in result["stats"]:
        for field in ("sensor_id", "min_value", "max_value", "avg_value", "sample_count"):
            assert field in s, f"stat entry missing '{field}': {s}"


def _assert_anomalies_shape(result: dict[str, Any]) -> None:
    assert "anomalies"        in result, "missing key: anomalies"
    assert "z_score_threshold" in result, "missing key: z_score_threshold"
    assert "requested_range"  in result, "missing key: requested_range"
    assert "returned_range"   in result, "missing key: returned_range"
    for a in result["anomalies"]:
        for field in ("timestamp_utc", "sensor_id", "value", "z_score", "severity"):
            assert field in a, f"anomaly entry missing '{field}': {a}"


def _assert_regression_shape(result: dict[str, Any]) -> None:
    assert "regressions"         in result, "missing key: regressions"
    assert "baseline_period_days" in result, "missing key: baseline_period_days"
    assert "recent_period_hours"  in result, "missing key: recent_period_hours"
    assert "threshold_delta"      in result, "missing key: threshold_delta"
    assert "load_band_aware"      in result, "missing key: load_band_aware"


def _assert_correlation_shape(result: dict[str, Any]) -> None:
    assert "x_sensor_id"             in result, "missing key: x_sensor_id"
    assert "y_sensor_id"             in result, "missing key: y_sensor_id"
    assert "correlation_coefficient" in result, "missing key: correlation_coefficient"
    assert "sample_count"            in result, "missing key: sample_count"
    assert "samples"                 in result, "missing key: samples"


def _assert_report_shape(result: dict[str, Any]) -> None:
    for key in ("generated_at", "stats", "anomalies", "regressions",
                "requested_range", "returned_range"):
        assert key in result, f"missing key: {key}"


# ---------------------------------------------------------------------------
# Python in-process tests
# ---------------------------------------------------------------------------

class TestPythonShape:
    """Verify Python analytics routes produce the canonical v2.0 shapes."""

    def test_history_shape(self, tmp_path: Path) -> None:
        async def _go() -> None:
            db = await _build_db(tmp_path / "h.db")
            await _insert(db, "cpu_temp_0", "cpu_temp", [50.0] * 30)
            await _insert(db, "gpu_temp_0", "gpu_temp", [60.0] * 30)
            result = await get_history(
                _req(db), hours=4.0, start=None, end=None,
                sensor_id=None, sensor_ids=None, bucket_seconds=None,
            )
            _assert_history_shape(result)
            await db.close()
        asyncio.run(_go())

    def test_stats_shape(self, tmp_path: Path) -> None:
        async def _go() -> None:
            db = await _build_db(tmp_path / "s.db")
            await _insert(db, "cpu_temp_0", "cpu_temp", [50.0] * 30)
            result = await get_stats(
                _req(db), hours=4.0, start=None, end=None,
                sensor_id=None, sensor_ids=None,
            )
            _assert_stats_shape(result)
            await db.close()
        asyncio.run(_go())

    def test_anomalies_shape(self, tmp_path: Path) -> None:
        async def _go() -> None:
            db = await _build_db(tmp_path / "a.db")
            # 20 normal readings + 1 spike to produce an anomaly
            vals = [50.0] * 19 + [99.9]
            await _insert(db, "cpu_temp_0", "cpu_temp", vals)
            result = await get_anomalies(
                _req(db), hours=4.0, start=None, end=None,
                sensor_id=None, sensor_ids=None, z_score_threshold=2.0,
            )
            _assert_anomalies_shape(result)
            await db.close()
        asyncio.run(_go())

    def test_regression_shape(self, tmp_path: Path) -> None:
        async def _go() -> None:
            db = await _build_db(tmp_path / "r.db")
            await _insert(db, "cpu_temp_0", "cpu_temp", [50.0] * 30)
            result = await get_regression(
                _req(db),
                sensor_id=None, sensor_ids=None,
                baseline_days=30, recent_hours=24.0, threshold_delta=5.0,
            )
            _assert_regression_shape(result)
            await db.close()
        asyncio.run(_go())

    def test_correlation_shape_preferred_params(self, tmp_path: Path) -> None:
        async def _go() -> None:
            db = await _build_db(tmp_path / "c.db")
            await _insert(db, "cpu_temp_0", "cpu_temp", [float(i) for i in range(30)])
            await _insert(db, "cpu_load_0", "cpu_load", [float(i) for i in range(30)])
            result = await get_correlation(
                _req(db), hours=4.0, start=None, end=None,
                x_sensor_id="cpu_temp_0", y_sensor_id="cpu_load_0",
                sensor_x=None, sensor_y=None,
            )
            _assert_correlation_shape(result)
            assert result["x_sensor_id"] == "cpu_temp_0"
            assert result["y_sensor_id"] == "cpu_load_0"
            await db.close()
        asyncio.run(_go())

    def test_correlation_shape_legacy_aliases(self, tmp_path: Path) -> None:
        """Legacy sensor_x / sensor_y aliases must still work."""
        async def _go() -> None:
            db = await _build_db(tmp_path / "ca.db")
            await _insert(db, "cpu_temp_0", "cpu_temp", [float(i) for i in range(30)])
            await _insert(db, "cpu_load_0", "cpu_load", [float(i) for i in range(30)])
            result = await get_correlation(
                _req(db), hours=4.0, start=None, end=None,
                x_sensor_id=None, y_sensor_id=None,
                sensor_x="cpu_temp_0", sensor_y="cpu_load_0",
            )
            _assert_correlation_shape(result)
            await db.close()
        asyncio.run(_go())

    def test_report_shape(self, tmp_path: Path) -> None:
        async def _go() -> None:
            db = await _build_db(tmp_path / "rep.db")
            await _insert(db, "cpu_temp_0", "cpu_temp", [50.0] * 30)
            result = await get_report(
                _req(db), hours=4.0, start=None, end=None,
                sensor_id=None, sensor_ids=None,
            )
            _assert_report_shape(result)
            await db.close()
        asyncio.run(_go())

    def test_dual_sensor_params_rejected(self, tmp_path: Path) -> None:
        """Providing both sensor_id and sensor_ids must raise HTTPException(400)."""
        from fastapi import HTTPException

        async def _go() -> None:
            db = await _build_db(tmp_path / "dual.db")
            with pytest.raises(HTTPException) as exc_info:
                await get_history(
                    _req(db), hours=1.0, start=None, end=None,
                    sensor_id="cpu_temp_0", sensor_ids="gpu_temp_0",
                    bucket_seconds=None,
                )
            assert exc_info.value.status_code == 400
            await db.close()
        asyncio.run(_go())

    def test_returned_range_reflects_data_extent(self, tmp_path: Path) -> None:
        """returned_range.start must be later than requested_range.start when data is sparse."""
        async def _go() -> None:
            db = await _build_db(tmp_path / "range.db")
            # Insert data only in the last 0.5 hours, but request 4 hours
            await _insert(db, "cpu_temp_0", "cpu_temp", [50.0] * 20, hours_ago=0.5)
            result = await get_stats(
                _req(db), hours=4.0, start=None, end=None,
                sensor_id=None, sensor_ids=None,
            )
            req_start  = result["requested_range"]["start"]
            ret_start  = result["returned_range"]["start"]
            # Actual data starts later than the 4-hour window boundary
            assert ret_start > req_start, (
                f"returned_range.start ({ret_start!r}) should be later than "
                f"requested_range.start ({req_start!r}) when data is sparse"
            )
            await db.close()
        asyncio.run(_go())


# ---------------------------------------------------------------------------
# C# live-backend tests (skipped unless CSHARP_BASE_URL is set)
# ---------------------------------------------------------------------------

def _cs_get(path: str, params: dict | None = None) -> dict[str, Any]:
    """GET from C# backend and return parsed JSON. Raises on non-2xx."""
    import urllib.request
    import urllib.parse
    import json

    assert CSHARP_BASE_URL is not None, "CSHARP_BASE_URL must be set"
    url = CSHARP_BASE_URL.rstrip("/") + path
    if params:
        url += "?" + urllib.parse.urlencode(params)
    with urllib.request.urlopen(url, timeout=10) as resp:
        return json.loads(resp.read())


@pytest.mark.skipif(not _cs_available, reason="CSHARP_BASE_URL not set")
class TestCSharpShape:
    """Verify the live C# backend returns the same shapes as Python."""

    def test_history_shape(self) -> None:
        result = _cs_get("/api/analytics/history", {"hours": 1})
        _assert_history_shape(result)

    def test_stats_shape(self) -> None:
        result = _cs_get("/api/analytics/stats", {"hours": 1})
        _assert_stats_shape(result)

    def test_anomalies_shape(self) -> None:
        result = _cs_get("/api/analytics/anomalies", {"hours": 1})
        _assert_anomalies_shape(result)

    def test_regression_shape(self) -> None:
        result = _cs_get("/api/analytics/regression")
        _assert_regression_shape(result)

    def test_correlation_preferred_params(self) -> None:
        """x_sensor_id / y_sensor_id must be accepted by the C# backend."""
        import urllib.error
        try:
            result = _cs_get("/api/analytics/correlation", {
                "x_sensor_id": "cpu_temp_0",
                "y_sensor_id": "cpu_load_0",
                "hours": 1,
            })
            _assert_correlation_shape(result)
        except urllib.error.HTTPError as exc:
            # 400 is acceptable here (no data); what we're testing is the
            # route accepts the preferred param names (not 404 or 422).
            assert exc.code == 400, (
                f"C# correlation with x_sensor_id/y_sensor_id returned {exc.code}; "
                "expected 200 or 400, not 404/422/500"
            )

    def test_correlation_legacy_aliases(self) -> None:
        """sensor_x / sensor_y legacy aliases must still be accepted."""
        import urllib.error
        try:
            result = _cs_get("/api/analytics/correlation", {
                "sensor_x": "cpu_temp_0",
                "sensor_y": "cpu_load_0",
                "hours": 1,
            })
            _assert_correlation_shape(result)
        except urllib.error.HTTPError as exc:
            assert exc.code == 400, (
                f"C# correlation with sensor_x/sensor_y returned {exc.code}; "
                "aliases should yield 200 or 400, not 404/422/500"
            )

    def test_report_shape(self) -> None:
        result = _cs_get("/api/analytics/report", {"hours": 1})
        _assert_report_shape(result)

    def test_dual_sensor_params_rejected(self) -> None:
        """Both sensor_id and sensor_ids together must return HTTP 400."""
        import urllib.error
        with pytest.raises(urllib.error.HTTPError) as exc_info:
            _cs_get("/api/analytics/history", {
                "sensor_id": "cpu_temp_0",
                "sensor_ids": "gpu_temp_0",
                "hours": 1,
            })
        assert exc_info.value.code == 400
