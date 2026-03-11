"""Tests for report schedule CRUD endpoints and due-logic."""

from __future__ import annotations

import asyncio
import sys
from datetime import datetime, timezone
from pathlib import Path
from unittest.mock import AsyncMock, MagicMock

import pytest

_backend_dir = Path(__file__).parent.parent
if str(_backend_dir) not in sys.path:
    sys.path.insert(0, str(_backend_dir))

from fastapi import HTTPException

from app.api.routes.report_schedules import (
    ReportScheduleBody,
    UpdateReportScheduleBody,
    _row_to_dict,
    create_report_schedule,
    delete_report_schedule,
    list_report_schedules,
    update_report_schedule,
)
from app.services.report_scheduler_service import _is_due


# ---------------------------------------------------------------------------
# Sample data
# ---------------------------------------------------------------------------

_SAMPLE_ROW = (
    "rs_abc123def456",
    "daily",
    "08:00",
    "UTC",
    1,
    None,
    "2026-03-10T00:00:00+00:00",
)


def _mock_request(rows=None, rowcount=1):
    req = MagicMock()
    db = AsyncMock()
    cursor = AsyncMock()
    cursor.fetchall = AsyncMock(return_value=rows or [])
    cursor.fetchone = AsyncMock(return_value=(rows[0] if rows else None))
    cursor.rowcount = rowcount
    db.execute = AsyncMock(return_value=cursor)
    db.commit = AsyncMock()
    req.app.state.db = db
    return req, db, cursor


# ---------------------------------------------------------------------------
# _row_to_dict
# ---------------------------------------------------------------------------


class TestRowToDict:
    def test_basic_parse(self):
        result = _row_to_dict(_SAMPLE_ROW)
        assert result["id"] == "rs_abc123def456"
        assert result["frequency"] == "daily"
        assert result["time_utc"] == "08:00"
        assert result["timezone"] == "UTC"
        assert result["enabled"] is True
        assert result["last_sent_at"] is None

    def test_enabled_zero_is_false(self):
        row = list(_SAMPLE_ROW)
        row[4] = 0
        assert _row_to_dict(tuple(row))["enabled"] is False

    def test_last_sent_at_preserved(self):
        row = list(_SAMPLE_ROW)
        row[5] = "2026-03-10T08:00:00+00:00"
        assert _row_to_dict(tuple(row))["last_sent_at"] == "2026-03-10T08:00:00+00:00"


# ---------------------------------------------------------------------------
# ReportScheduleBody validation
# ---------------------------------------------------------------------------


class TestReportScheduleBody:
    def test_valid_daily(self):
        body = ReportScheduleBody(frequency="daily", time_utc="09:30", timezone="UTC")
        assert body.frequency == "daily"
        assert body.enabled is True

    def test_valid_weekly(self):
        body = ReportScheduleBody(frequency="weekly", time_utc="06:00")
        assert body.frequency == "weekly"

    def test_invalid_frequency_raises(self):
        with pytest.raises(Exception):
            ReportScheduleBody(frequency="monthly", time_utc="08:00")

    def test_enabled_defaults_true(self):
        body = ReportScheduleBody(frequency="daily", time_utc="10:00")
        assert body.enabled is True


# ---------------------------------------------------------------------------
# list_report_schedules
# ---------------------------------------------------------------------------


class TestListReportSchedules:
    def test_returns_empty_list(self):
        req, db, cursor = _mock_request(rows=[])
        result = asyncio.run(list_report_schedules(req))
        assert result == {"schedules": []}

    def test_returns_schedules(self):
        req, db, cursor = _mock_request(rows=[_SAMPLE_ROW])
        cursor.fetchall = AsyncMock(return_value=[_SAMPLE_ROW])
        result = asyncio.run(list_report_schedules(req))
        assert len(result["schedules"]) == 1
        assert result["schedules"][0]["id"] == "rs_abc123def456"


# ---------------------------------------------------------------------------
# create_report_schedule
# ---------------------------------------------------------------------------


class TestCreateReportSchedule:
    def test_creates_and_returns(self):
        req, db, cursor = _mock_request(rows=[_SAMPLE_ROW])
        cursor.fetchone = AsyncMock(return_value=_SAMPLE_ROW)
        body = ReportScheduleBody(frequency="daily", time_utc="08:00")
        result = asyncio.run(create_report_schedule(body, req))
        assert result["frequency"] == "daily"
        assert result["time_utc"] == "08:00"
        db.commit.assert_called_once()

    def test_insert_called_with_correct_args(self):
        req, db, cursor = _mock_request(rows=[_SAMPLE_ROW])
        cursor.fetchone = AsyncMock(return_value=_SAMPLE_ROW)
        body = ReportScheduleBody(frequency="weekly", time_utc="14:00", timezone="Europe/Berlin")
        asyncio.run(create_report_schedule(body, req))
        insert_call = db.execute.call_args_list[0]
        sql = insert_call[0][0]
        params = insert_call[0][1]
        assert "INSERT INTO report_schedules" in sql
        assert params[1] == "weekly"
        assert params[2] == "14:00"
        assert params[3] == "Europe/Berlin"


# ---------------------------------------------------------------------------
# update_report_schedule
# ---------------------------------------------------------------------------


class TestUpdateReportSchedule:
    def test_404_when_not_found(self):
        req, db, cursor = _mock_request(rows=[])
        cursor.fetchone = AsyncMock(return_value=None)
        body = UpdateReportScheduleBody(enabled=False)
        with pytest.raises(HTTPException) as exc:
            asyncio.run(update_report_schedule("rs_missing", body, req))
        assert exc.value.status_code == 404

    def test_400_when_no_fields(self):
        req, db, cursor = _mock_request(rows=[_SAMPLE_ROW])
        cursor.fetchone = AsyncMock(return_value=_SAMPLE_ROW)
        body = UpdateReportScheduleBody()
        with pytest.raises(HTTPException) as exc:
            asyncio.run(update_report_schedule("rs_abc123def456", body, req))
        assert exc.value.status_code == 400

    def test_updates_enabled(self):
        req, db, cursor = _mock_request(rows=[_SAMPLE_ROW])
        # First call: SELECT id; second call: UPDATE; third call: SELECT full row
        call_count = 0
        async def _execute(sql, params=()):
            nonlocal call_count
            call_count += 1
            if call_count == 1:
                # EXISTS check
                c = AsyncMock()
                c.fetchone = AsyncMock(return_value=(_SAMPLE_ROW[0],))
                return c
            elif call_count == 2:
                # UPDATE
                c = AsyncMock()
                c.fetchone = AsyncMock(return_value=None)
                return c
            else:
                # SELECT after update
                c = AsyncMock()
                c.fetchone = AsyncMock(return_value=_SAMPLE_ROW)
                return c
        db.execute = _execute
        body = UpdateReportScheduleBody(enabled=False)
        result = asyncio.run(update_report_schedule("rs_abc123def456", body, req))
        assert result["id"] == "rs_abc123def456"


# ---------------------------------------------------------------------------
# delete_report_schedule
# ---------------------------------------------------------------------------


class TestDeleteReportSchedule:
    def test_deletes_successfully(self):
        req, db, cursor = _mock_request()
        cursor.rowcount = 1
        result = asyncio.run(delete_report_schedule("rs_abc123def456", req))
        assert result == {"success": True}
        db.commit.assert_called_once()

    def test_404_when_not_found(self):
        req, db, cursor = _mock_request()
        cursor.rowcount = 0
        with pytest.raises(HTTPException) as exc:
            asyncio.run(delete_report_schedule("rs_missing", req))
        assert exc.value.status_code == 404


# ---------------------------------------------------------------------------
# _is_due logic
# ---------------------------------------------------------------------------


class TestIsDue:
    def _sched(self, frequency="daily", time_utc="08:00", last_sent_at=None, enabled=True):
        return {
            "frequency": frequency,
            "time_utc": time_utc,
            "timezone": "UTC",
            "enabled": enabled,
            "last_sent_at": last_sent_at,
        }

    def test_due_when_never_sent_and_time_matches(self):
        now = datetime(2026, 3, 10, 8, 0, 0, tzinfo=timezone.utc)
        assert _is_due(self._sched(time_utc="08:00"), now) is True

    def test_not_due_when_wrong_hour(self):
        now = datetime(2026, 3, 10, 9, 0, 0, tzinfo=timezone.utc)
        assert _is_due(self._sched(time_utc="08:00"), now) is False

    def test_not_due_when_wrong_minute(self):
        now = datetime(2026, 3, 10, 8, 30, 0, tzinfo=timezone.utc)
        assert _is_due(self._sched(time_utc="08:00"), now) is False

    def test_daily_not_due_when_sent_today(self):
        now = datetime(2026, 3, 10, 8, 0, 0, tzinfo=timezone.utc)
        last_sent = "2026-03-10T06:00:00+00:00"  # sent earlier today
        assert _is_due(self._sched(time_utc="08:00", last_sent_at=last_sent), now) is False

    def test_daily_due_when_sent_yesterday(self):
        now = datetime(2026, 3, 10, 8, 0, 0, tzinfo=timezone.utc)
        last_sent = "2026-03-09T08:00:00+00:00"
        assert _is_due(self._sched(time_utc="08:00", last_sent_at=last_sent), now) is True

    def test_weekly_not_due_when_sent_this_week(self):
        # 2026-03-10 is a Tuesday. Last sent Monday.
        now = datetime(2026, 3, 10, 8, 0, 0, tzinfo=timezone.utc)
        last_sent = "2026-03-09T08:00:00+00:00"  # Monday same week
        assert _is_due(self._sched(frequency="weekly", time_utc="08:00", last_sent_at=last_sent), now) is False

    def test_weekly_due_when_sent_last_week(self):
        now = datetime(2026, 3, 10, 8, 0, 0, tzinfo=timezone.utc)
        last_sent = "2026-03-02T08:00:00+00:00"  # previous week
        assert _is_due(self._sched(frequency="weekly", time_utc="08:00", last_sent_at=last_sent), now) is True

    def test_invalid_time_utc_returns_false(self):
        now = datetime(2026, 3, 10, 8, 0, 0, tzinfo=timezone.utc)
        sched = self._sched(time_utc="not-a-time")
        assert _is_due(sched, now) is False

    def test_invalid_last_sent_treated_as_never(self):
        now = datetime(2026, 3, 10, 8, 0, 0, tzinfo=timezone.utc)
        sched = self._sched(time_utc="08:00", last_sent_at="not-a-date")
        assert _is_due(sched, now) is True


# ---------------------------------------------------------------------------
# Weekly _is_due boundary (replaces former _week_start_utc unit tests)
# ---------------------------------------------------------------------------


class TestWeeklyIsDue:
    def _sched(self, last_sent_at: str) -> dict:
        return {
            "frequency": "weekly",
            "time_utc": "08:00",
            "timezone": "UTC",
            "last_sent_at": last_sent_at,
        }

    def test_monday_after_last_week_send_is_due(self):
        now = datetime(2026, 3, 9, 8, 0, 0, tzinfo=timezone.utc)  # Monday 08:00
        sched = self._sched(last_sent_at="2026-03-02T08:00:00+00:00")  # prev Monday
        assert _is_due(sched, now) is True

    def test_tuesday_same_week_as_send_is_not_due(self):
        now = datetime(2026, 3, 10, 8, 0, 0, tzinfo=timezone.utc)  # Tuesday
        sched = self._sched(last_sent_at="2026-03-09T08:00:00+00:00")  # Monday same week
        assert _is_due(sched, now) is False

    def test_sunday_same_week_as_send_is_not_due(self):
        now = datetime(2026, 3, 15, 23, 59, 0, tzinfo=timezone.utc)  # Sunday
        sched = self._sched(last_sent_at="2026-03-09T08:00:00+00:00")  # Monday same week
        assert _is_due(sched, now) is False
