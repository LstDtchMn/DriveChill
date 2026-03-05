"""Performance tests for analytics v2.0 endpoints.

Seeds a 30-day dataset (~130 k rows across 3 sensors at 1-minute intervals)
and asserts that every analytics query completes within 2 seconds.

Each test is marked with the custom ``perf`` marker so CI can run them
separately with ``pytest -m perf`` if needed.
"""

from __future__ import annotations

import asyncio
import sys
import time
from datetime import datetime, timezone, timedelta
from pathlib import Path
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
)

# ---------------------------------------------------------------------------
# Constants
# ---------------------------------------------------------------------------

SENSORS = [
    ("cpu_temp_0", "CPU Package", "cpu_temp", "°C", 55.0, 5.0),
    ("gpu_temp_0", "GPU Core",    "gpu_temp", "°C", 65.0, 8.0),
    ("hdd_temp_0", "HDD 0",       "hdd_temp", "°C", 38.0, 3.0),
]

DAYS = 30
INTERVAL_MINUTES = 1
ROWS_PER_SENSOR = DAYS * 24 * 60 // INTERVAL_MINUTES  # 43 200

# ---------------------------------------------------------------------------
# Fixtures
# ---------------------------------------------------------------------------


async def _build_perf_db(db_path: Path) -> aiosqlite.Connection:
    """Seed *db_path* with ROWS_PER_SENSOR rows per sensor and return a conn."""
    db = await aiosqlite.connect(str(db_path))
    await db.execute("PRAGMA journal_mode=WAL")
    await db.execute("PRAGMA synchronous=OFF")
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
    await db.execute("CREATE INDEX IF NOT EXISTS idx_sl_ts     ON sensor_log(timestamp)")
    await db.execute("CREATE INDEX IF NOT EXISTS idx_sl_sensor ON sensor_log(sensor_id, timestamp)")
    await db.execute("""
        CREATE TABLE IF NOT EXISTS settings (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL,
            updated_at TEXT NOT NULL DEFAULT (datetime('now'))
        )
    """)
    await db.execute(
        "INSERT OR IGNORE INTO settings (key, value) VALUES ('history_retention_hours', '720')"
    )

    now = datetime.now(timezone.utc)
    rows: list[tuple] = []
    import random
    rng = random.Random(42)
    for sid, sname, stype, unit, base_temp, noise in SENSORS:
        for i in range(ROWS_PER_SENSOR):
            ts = now - timedelta(minutes=(ROWS_PER_SENSOR - i) * INTERVAL_MINUTES)
            val = base_temp + rng.uniform(-noise, noise)
            rows.append((ts.strftime("%Y-%m-%d %H:%M:%S"), sid, sname, stype, f"{val:.2f}", unit))

    await db.executemany(
        "INSERT INTO sensor_log (timestamp, sensor_id, sensor_name, sensor_type, value, unit) VALUES (?,?,?,?,?,?)",
        rows,
    )
    await db.commit()
    return db


def _make_request(db: aiosqlite.Connection) -> MagicMock:
    """Return a mock FastAPI Request pointing at the given DB."""
    req = MagicMock()
    req.app.state.db = db
    # Stub settings_repo so _retention_hours() returns 720
    settings_repo = MagicMock()
    settings_repo.get_int = AsyncMock(return_value=720)
    req.app.state.settings_repo = settings_repo
    return req


# ---------------------------------------------------------------------------
# Helper to run an async coroutine and measure elapsed time
# ---------------------------------------------------------------------------

def _timed(coro) -> float:
    """Run *coro* and return elapsed wall-clock seconds."""
    t0 = time.monotonic()
    asyncio.run(coro)
    return time.monotonic() - t0


# ---------------------------------------------------------------------------
# Shared DB — built once per module via a module-scoped fixture
# ---------------------------------------------------------------------------

_perf_db: aiosqlite.Connection | None = None
_perf_db_path: Path | None = None


@pytest.fixture(scope="module")
def perf_db(tmp_path_factory):
    """Module-scoped fixture: seed the performance DB once, reuse across tests."""
    global _perf_db, _perf_db_path
    p = tmp_path_factory.mktemp("perf") / "perf.db"
    _perf_db_path = p
    _perf_db = asyncio.run(_build_perf_db(p))
    yield _perf_db
    asyncio.run(_perf_db.close())


# ---------------------------------------------------------------------------
# Tests
# ---------------------------------------------------------------------------

BUDGET_SECONDS = 2.0


def test_history_30d_perf(perf_db):
    """30-day history query (auto-bucket=2h) completes under 2 s."""
    req = _make_request(perf_db)
    elapsed = _timed(get_history(req, hours=720.0, start=None, end=None, sensor_id=None, sensor_ids=None, bucket_seconds=None))
    assert elapsed < BUDGET_SECONDS, f"history 30d took {elapsed:.2f}s (budget {BUDGET_SECONDS}s)"


def test_stats_30d_perf(perf_db):
    """30-day stats query completes under 2 s."""
    req = _make_request(perf_db)
    elapsed = _timed(get_stats(req, hours=720.0, start=None, end=None, sensor_id=None, sensor_ids=None))
    assert elapsed < BUDGET_SECONDS, f"stats 30d took {elapsed:.2f}s (budget {BUDGET_SECONDS}s)"


def test_anomalies_30d_perf(perf_db):
    """30-day anomaly detection completes under 2 s."""
    req = _make_request(perf_db)
    elapsed = _timed(get_anomalies(req, hours=720.0, start=None, end=None, sensor_id=None, sensor_ids=None, z_score_threshold=3.0))
    assert elapsed < BUDGET_SECONDS, f"anomalies 30d took {elapsed:.2f}s (budget {BUDGET_SECONDS}s)"


def test_regression_30d_perf(perf_db):
    """Thermal regression (30d baseline, 24h recent) completes under 2 s."""
    req = _make_request(perf_db)
    elapsed = _timed(get_regression(req, baseline_days=30, recent_hours=24.0, threshold_delta=5.0, sensor_id=None, sensor_ids=None))
    assert elapsed < BUDGET_SECONDS, f"regression 30d took {elapsed:.2f}s (budget {BUDGET_SECONDS}s)"


def test_correlation_30d_perf(perf_db):
    """Pearson correlation over 30 days completes under 2 s."""
    req = _make_request(perf_db)
    elapsed = _timed(get_correlation(req, x_sensor_id="cpu_temp_0", y_sensor_id="gpu_temp_0", sensor_x=None, sensor_y=None, hours=720.0, start=None, end=None))
    assert elapsed < BUDGET_SECONDS, f"correlation 30d took {elapsed:.2f}s (budget {BUDGET_SECONDS}s)"


def test_history_single_sensor_30d_perf(perf_db):
    """Single-sensor 30-day history completes under 2 s."""
    req = _make_request(perf_db)
    elapsed = _timed(get_history(req, hours=720.0, start=None, end=None, sensor_id="cpu_temp_0", sensor_ids=None, bucket_seconds=None))
    assert elapsed < BUDGET_SECONDS, f"history single 30d took {elapsed:.2f}s (budget {BUDGET_SECONDS}s)"


def test_history_multi_sensor_30d_perf(perf_db):
    """Multi-sensor 30-day history (all 3 sensors explicit) completes under 2 s."""
    req = _make_request(perf_db)
    elapsed = _timed(
        get_history(req, hours=720.0, start=None, end=None, sensor_id=None, sensor_ids="cpu_temp_0,gpu_temp_0,hdd_temp_0", bucket_seconds=None)
    )
    assert elapsed < BUDGET_SECONDS, f"history multi 30d took {elapsed:.2f}s (budget {BUDGET_SECONDS}s)"
