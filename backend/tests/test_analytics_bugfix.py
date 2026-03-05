"""Tests for analytics bug fixes: timezone parsing, custom range regression/report,
and drive health snapshot pruning.
"""

from __future__ import annotations

import asyncio
import sys
from datetime import datetime, timezone, timedelta
from pathlib import Path

import pytest

_backend_dir = Path(__file__).parent.parent
if str(_backend_dir) not in sys.path:
    sys.path.insert(0, str(_backend_dir))

from app.api.routes.analytics import _parse_utc, _resolve_range


# ---------------------------------------------------------------------------
# _parse_utc tests
# ---------------------------------------------------------------------------

class TestParseUtc:
    def test_z_suffix(self):
        dt = _parse_utc("2026-03-01T12:00:00Z")
        assert dt.tzinfo is not None
        assert dt == datetime(2026, 3, 1, 12, 0, 0, tzinfo=timezone.utc)

    def test_lowercase_z_suffix(self):
        dt = _parse_utc("2026-03-01T12:00:00z")
        assert dt == datetime(2026, 3, 1, 12, 0, 0, tzinfo=timezone.utc)

    def test_positive_offset_converts_to_utc(self):
        dt = _parse_utc("2026-03-01T17:00:00+05:00")
        assert dt.tzinfo is not None
        assert dt == datetime(2026, 3, 1, 12, 0, 0, tzinfo=timezone.utc)

    def test_negative_offset_converts_to_utc(self):
        dt = _parse_utc("2026-03-01T07:00:00-05:00")
        assert dt.tzinfo is not None
        assert dt == datetime(2026, 3, 1, 12, 0, 0, tzinfo=timezone.utc)

    def test_naive_treated_as_utc(self):
        dt = _parse_utc("2026-03-01T12:00:00")
        assert dt.tzinfo is not None
        assert dt == datetime(2026, 3, 1, 12, 0, 0, tzinfo=timezone.utc)

    def test_utc_offset_zero(self):
        dt = _parse_utc("2026-03-01T12:00:00+00:00")
        assert dt == datetime(2026, 3, 1, 12, 0, 0, tzinfo=timezone.utc)

    def test_whitespace_stripped(self):
        dt = _parse_utc("  2026-03-01T12:00:00Z  ")
        assert dt == datetime(2026, 3, 1, 12, 0, 0, tzinfo=timezone.utc)


# ---------------------------------------------------------------------------
# _resolve_range tests
# ---------------------------------------------------------------------------

class TestResolveRange:
    def test_z_timestamps(self):
        s, e = _resolve_range(None, "2026-03-01T00:00:00Z", "2026-03-02T00:00:00Z")
        assert s == datetime(2026, 3, 1, tzinfo=timezone.utc)
        assert e == datetime(2026, 3, 2, tzinfo=timezone.utc)

    def test_offset_aware_timestamps(self):
        s, e = _resolve_range(
            None,
            "2026-03-01T12:00:00-05:00",
            "2026-03-01T22:00:00-05:00",
        )
        # -05:00 should convert to UTC: 17:00 and 03:00 next day
        assert s == datetime(2026, 3, 1, 17, 0, 0, tzinfo=timezone.utc)
        assert e == datetime(2026, 3, 2, 3, 0, 0, tzinfo=timezone.utc)

    def test_naive_timestamps(self):
        s, e = _resolve_range(None, "2026-03-01T06:00:00", "2026-03-01T18:00:00")
        assert s == datetime(2026, 3, 1, 6, 0, 0, tzinfo=timezone.utc)
        assert e == datetime(2026, 3, 1, 18, 0, 0, tzinfo=timezone.utc)

    def test_fallback_to_hours_when_no_range(self):
        s, e = _resolve_range(6.0, None, None)
        delta = e - s
        assert abs(delta.total_seconds() - 6 * 3600) < 2

    def test_swapped_range_falls_back(self):
        # start > end should fall back to hours
        s, e = _resolve_range(
            12.0,
            "2026-03-02T00:00:00Z",
            "2026-03-01T00:00:00Z",
        )
        delta = e - s
        assert abs(delta.total_seconds() - 12 * 3600) < 2

    def test_only_start_falls_back(self):
        # One-sided (start only) must fall back to hours
        s, e = _resolve_range(6.0, "2026-03-01T00:00:00Z", None)
        delta = e - s
        assert abs(delta.total_seconds() - 6 * 3600) < 2

    def test_only_end_falls_back(self):
        # One-sided (end only) must fall back to hours
        s, e = _resolve_range(6.0, None, "2026-03-02T12:00:00Z")
        delta = e - s
        assert abs(delta.total_seconds() - 6 * 3600) < 2


# ---------------------------------------------------------------------------
# Drive health pruning tests
# ---------------------------------------------------------------------------

class TestDriveHealthPruning:
    def test_prune_health_history_returns_count(self):
        import aiosqlite

        async def _go():
            db = await aiosqlite.connect(":memory:")
            await db.execute("""
                CREATE TABLE drive_health_snapshots (
                    drive_id TEXT, recorded_at TEXT, temperature_c REAL
                )
            """)
            now = datetime.now(timezone.utc)
            old = (now - timedelta(hours=100)).strftime("%Y-%m-%d %H:%M:%S")
            recent = (now - timedelta(hours=1)).strftime("%Y-%m-%d %H:%M:%S")
            await db.execute(
                "INSERT INTO drive_health_snapshots VALUES (?,?,?)",
                ("d1", old, 35.0),
            )
            await db.execute(
                "INSERT INTO drive_health_snapshots VALUES (?,?,?)",
                ("d2", recent, 40.0),
            )
            await db.commit()

            from app.db.repositories.drive_repo import DriveRepo
            repo = DriveRepo(db)
            deleted = await repo.prune_health_history(48.0)
            assert deleted == 1  # only the old row

            cursor = await db.execute("SELECT COUNT(*) FROM drive_health_snapshots")
            row = await cursor.fetchone()
            assert row[0] == 1  # recent row survives

            await db.close()

        asyncio.run(_go())
