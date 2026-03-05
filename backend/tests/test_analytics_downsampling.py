"""Downsampling correctness: raw maxima and minima must survive bucketing.

Design spec §6.3: the max_value and min_value fields in each bucket must
exactly equal the extremes of the raw readings within that window — they
must not be averaged away.  Similarly, avg_value must be the true mean of
all raw samples and sample_count must be exact.
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

from app.api.routes.analytics import get_history


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

async def _make_db(db_path: Path) -> aiosqlite.Connection:
    db = await aiosqlite.connect(str(db_path))
    await db.execute("PRAGMA journal_mode=WAL")
    await db.execute("""
        CREATE TABLE sensor_log (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            timestamp   TEXT NOT NULL,
            sensor_id   TEXT NOT NULL,
            sensor_name TEXT NOT NULL,
            sensor_type TEXT NOT NULL,
            value       TEXT NOT NULL,
            unit        TEXT NOT NULL DEFAULT '°C'
        )
    """)
    await db.execute("""
        CREATE TABLE settings (
            key        TEXT PRIMARY KEY,
            value      TEXT NOT NULL,
            updated_at TEXT NOT NULL DEFAULT (datetime('now'))
        )
    """)
    await db.execute(
        "INSERT INTO settings (key, value) VALUES ('history_retention_hours', '8760')"
    )
    await db.commit()
    return db


def _make_request(db: aiosqlite.Connection) -> MagicMock:
    req = MagicMock()
    req.app.state.db = db
    settings_repo = MagicMock()
    settings_repo.get_int = AsyncMock(return_value=8760)
    req.app.state.settings_repo = settings_repo
    return req


def _bucket_aligned(ts: datetime, bucket_seconds: int) -> datetime:
    """Return *ts* rounded down to the nearest bucket boundary."""
    epoch = int(ts.timestamp())
    aligned = (epoch // bucket_seconds) * bucket_seconds
    return datetime.fromtimestamp(aligned, tz=timezone.utc)


class TestDownsamplingCorrectness:
    """§6.3: raw min/max must be preserved verbatim in each downsampled bucket."""

    def test_spike_max_preserved(self, tmp_path: Path) -> None:
        """A single spike value must appear exactly as max_value in its bucket."""
        async def _run() -> None:
            db = await _make_db(tmp_path / "spike.db")
            fmt = "%Y-%m-%d %H:%M:%S"
            bucket_seconds = 600  # 10-minute buckets

            # Anchor all readings at the start of a clean 10-minute boundary
            # so they all fall into exactly one bucket.
            anchor = _bucket_aligned(
                datetime.now(timezone.utc) - timedelta(hours=1),
                bucket_seconds,
            )
            readings = [
                (0,   50.0),   # normal
                (120, 50.5),   # normal
                (240, 99.9),   # spike — must survive as max_value
                (360, 50.0),   # normal
            ]
            rows = [
                (
                    (anchor + timedelta(seconds=off)).strftime(fmt),
                    "cpu_temp_0", "CPU Package", "cpu_temp", f"{val}", "°C",
                )
                for off, val in readings
            ]
            await db.executemany(
                "INSERT INTO sensor_log "
                "(timestamp, sensor_id, sensor_name, sensor_type, value, unit) "
                "VALUES (?,?,?,?,?,?)",
                rows,
            )
            await db.commit()

            req = _make_request(db)
            result = await get_history(req, hours=2.0, start=None, end=None, sensor_id=None, sensor_ids=None, bucket_seconds=bucket_seconds)

            buckets = result["buckets"]
            assert len(buckets) == 1, f"Expected 1 bucket, got {len(buckets)}"

            b = buckets[0]
            assert b["sensor_id"] == "cpu_temp_0"
            assert b["max_value"] == pytest.approx(99.9, abs=0.01), (
                f"Spike max 99.9°C was lost; got max_value={b['max_value']}"
            )
            assert b["min_value"] == pytest.approx(50.0, abs=0.01), (
                f"Min 50.0°C was lost; got min_value={b['min_value']}"
            )
            expected_avg = sum(v for _, v in readings) / len(readings)
            assert b["avg_value"] == pytest.approx(expected_avg, abs=0.1), (
                f"avg_value wrong; expected {expected_avg:.2f} got {b['avg_value']}"
            )
            assert b["sample_count"] == len(readings)

            await db.close()

        asyncio.run(_run())

    def test_min_trough_preserved(self, tmp_path: Path) -> None:
        """A trough (very low reading) must appear exactly as min_value."""
        async def _run() -> None:
            db = await _make_db(tmp_path / "trough.db")
            fmt = "%Y-%m-%d %H:%M:%S"
            bucket_seconds = 300  # 5-minute buckets

            anchor = _bucket_aligned(
                datetime.now(timezone.utc) - timedelta(hours=1),
                bucket_seconds,
            )
            readings = [
                (0,   70.0),
                (60,  70.5),
                (120, 10.1),   # trough — must survive as min_value
                (180, 70.0),
                (240, 71.0),
            ]
            rows = [
                (
                    (anchor + timedelta(seconds=off)).strftime(fmt),
                    "gpu_temp_0", "GPU Core", "gpu_temp", f"{val}", "°C",
                )
                for off, val in readings
            ]
            await db.executemany(
                "INSERT INTO sensor_log "
                "(timestamp, sensor_id, sensor_name, sensor_type, value, unit) "
                "VALUES (?,?,?,?,?,?)",
                rows,
            )
            await db.commit()

            req = _make_request(db)
            result = await get_history(req, hours=2.0, start=None, end=None, sensor_id=None, sensor_ids=None, bucket_seconds=bucket_seconds)

            buckets = [b for b in result["buckets"] if b["sensor_id"] == "gpu_temp_0"]
            assert len(buckets) == 1, f"Expected 1 bucket, got {len(buckets)}"

            b = buckets[0]
            assert b["min_value"] == pytest.approx(10.1, abs=0.01), (
                f"Trough 10.1°C was lost; got min_value={b['min_value']}"
            )
            assert b["max_value"] == pytest.approx(71.0, abs=0.01), (
                f"Max 71.0°C was lost; got max_value={b['max_value']}"
            )

            await db.close()

        asyncio.run(_run())

    def test_per_bucket_maxima_are_independent(self, tmp_path: Path) -> None:
        """Each bucket tracks its own max independently — one spike doesn't bleed across."""
        async def _run() -> None:
            db = await _make_db(tmp_path / "multi.db")
            fmt = "%Y-%m-%d %H:%M:%S"
            bucket_seconds = 300  # 5-minute buckets

            anchor = _bucket_aligned(
                datetime.now(timezone.utc) - timedelta(hours=1),
                bucket_seconds,
            )

            # Bucket 0 (anchor + 0s to +299s): spike at 95.0
            # Bucket 1 (anchor + 300s to +599s): normal values only, max ~65.0
            rows = []
            for off, val in [(0, 60.0), (60, 95.0), (120, 60.0)]:
                rows.append((
                    (anchor + timedelta(seconds=off)).strftime(fmt),
                    "cpu_temp_0", "CPU Package", "cpu_temp", f"{val}", "°C",
                ))
            for off, val in [(300, 63.0), (360, 65.0), (420, 64.0)]:
                rows.append((
                    (anchor + timedelta(seconds=off)).strftime(fmt),
                    "cpu_temp_0", "CPU Package", "cpu_temp", f"{val}", "°C",
                ))

            await db.executemany(
                "INSERT INTO sensor_log "
                "(timestamp, sensor_id, sensor_name, sensor_type, value, unit) "
                "VALUES (?,?,?,?,?,?)",
                rows,
            )
            await db.commit()

            req = _make_request(db)
            result = await get_history(req, hours=2.0, start=None, end=None, sensor_id=None, sensor_ids=None, bucket_seconds=bucket_seconds)

            buckets = sorted(
                [b for b in result["buckets"] if b["sensor_id"] == "cpu_temp_0"],
                key=lambda b: b["timestamp_utc"],
            )
            assert len(buckets) == 2, f"Expected 2 buckets, got {len(buckets)}"

            b0, b1 = buckets
            # Bucket 0: spike bucket
            assert b0["max_value"] == pytest.approx(95.0, abs=0.01), (
                f"Bucket 0 spike max wrong: {b0['max_value']}"
            )
            # Bucket 1: no spike; max should be 65.0
            assert b1["max_value"] == pytest.approx(65.0, abs=0.01), (
                f"Bucket 1 max wrong (spike from bucket 0 should not bleed): {b1['max_value']}"
            )
            assert b1["min_value"] == pytest.approx(63.0, abs=0.01)

            await db.close()

        asyncio.run(_run())

    def test_series_max_matches_buckets_max(self, tmp_path: Path) -> None:
        """The series dict's max field must match the flat buckets list max_value."""
        async def _run() -> None:
            db = await _make_db(tmp_path / "series.db")
            fmt = "%Y-%m-%d %H:%M:%S"
            bucket_seconds = 600

            anchor = _bucket_aligned(
                datetime.now(timezone.utc) - timedelta(hours=1),
                bucket_seconds,
            )
            rows = [
                ((anchor + timedelta(seconds=off)).strftime(fmt),
                 "cpu_temp_0", "CPU Package", "cpu_temp", f"{val}", "°C")
                for off, val in [(0, 55.0), (60, 88.8), (120, 55.0)]
            ]
            await db.executemany(
                "INSERT INTO sensor_log "
                "(timestamp, sensor_id, sensor_name, sensor_type, value, unit) "
                "VALUES (?,?,?,?,?,?)",
                rows,
            )
            await db.commit()

            req = _make_request(db)
            result = await get_history(req, hours=2.0, start=None, end=None, sensor_id=None, sensor_ids=None, bucket_seconds=bucket_seconds)

            flat = result["buckets"]
            series = result["series"]

            assert len(flat) == 1
            assert "cpu_temp_0" in series
            assert len(series["cpu_temp_0"]) == 1

            series_pt = series["cpu_temp_0"][0]
            flat_b = flat[0]

            assert series_pt["max"] == pytest.approx(flat_b["max_value"], abs=0.01), (
                "series max does not match buckets max_value"
            )
            assert series_pt["min"] == pytest.approx(flat_b["min_value"], abs=0.01), (
                "series min does not match buckets min_value"
            )
            assert series_pt["avg"] == pytest.approx(flat_b["avg_value"], abs=0.01), (
                "series avg does not match buckets avg_value"
            )

            # The spike must appear in both
            assert flat_b["max_value"] == pytest.approx(88.8, abs=0.01)
            assert series_pt["max"] == pytest.approx(88.8, abs=0.01)

            await db.close()

        asyncio.run(_run())
