"""Tests for profile schedule CRUD endpoints and evaluation logic."""

from __future__ import annotations

import sys
from datetime import datetime, timezone, time
from pathlib import Path
from unittest.mock import AsyncMock, MagicMock

import pytest

_backend_dir = Path(__file__).parent.parent
if str(_backend_dir) not in sys.path:
    sys.path.insert(0, str(_backend_dir))

from fastapi import HTTPException

from app.api.routes.profile_schedules import (
    ProfileScheduleBody,
    _row_to_dict,
    create_profile_schedule,
    delete_profile_schedule,
    list_profile_schedules,
    update_profile_schedule,
)
from app.services.profile_scheduler_service import _find_active_schedule


# ---------------------------------------------------------------------------
# Sample data
# ---------------------------------------------------------------------------

_SAMPLE_ROW = (
    "psched_abc123def456",
    "profile_quiet",
    "22:00",
    "06:00",
    "0,1,2,3,4",
    "UTC",
    1,
    "2026-03-10T00:00:00+00:00",
)


def _mock_request(rows=None, rowcount=1, profile_exists=True):
    req = MagicMock()
    db = AsyncMock()
    cursor = AsyncMock()
    cursor.fetchall = AsyncMock(return_value=rows or [])
    cursor.fetchone = AsyncMock(return_value=(rows[0] if rows else None))
    cursor.rowcount = rowcount
    db.execute = AsyncMock(return_value=cursor)
    db.commit = AsyncMock()
    req.app.state.db = db
    profile_repo = AsyncMock()
    if profile_exists:
        profile_repo.get = AsyncMock(return_value=MagicMock(id="profile_quiet"))
    else:
        profile_repo.get = AsyncMock(return_value=None)
    req.app.state.profile_repo = profile_repo
    return req, db, cursor


# ---------------------------------------------------------------------------
# _row_to_dict
# ---------------------------------------------------------------------------


def test_row_to_dict():
    result = _row_to_dict(_SAMPLE_ROW)
    assert result["id"] == "psched_abc123def456"
    assert result["profile_id"] == "profile_quiet"
    assert result["start_time"] == "22:00"
    assert result["end_time"] == "06:00"
    assert result["days_of_week"] == "0,1,2,3,4"
    assert result["timezone"] == "UTC"
    assert result["enabled"] is True
    assert result["created_at"] == "2026-03-10T00:00:00+00:00"


# ---------------------------------------------------------------------------
# CRUD route tests
# ---------------------------------------------------------------------------


@pytest.mark.anyio
async def test_list_empty():
    req, db, _ = _mock_request(rows=[])
    result = await list_profile_schedules(req)
    assert result == {"schedules": []}


@pytest.mark.anyio
async def test_list_with_rows():
    req, db, _ = _mock_request(rows=[_SAMPLE_ROW])
    result = await list_profile_schedules(req)
    assert len(result["schedules"]) == 1
    assert result["schedules"][0]["id"] == "psched_abc123def456"


@pytest.mark.anyio
async def test_create_returns_dict():
    req, db, cursor = _mock_request(rows=[_SAMPLE_ROW])
    body = ProfileScheduleBody(
        profile_id="profile_quiet",
        start_time="22:00",
        end_time="06:00",
        days_of_week="0,1,2,3,4",
        timezone="UTC",
    )
    result = await create_profile_schedule(body, req)
    assert result["profile_id"] == "profile_quiet"
    assert db.commit.call_count == 1


@pytest.mark.anyio
async def test_create_missing_profile_raises_404():
    req, db, _ = _mock_request(profile_exists=False)
    body = ProfileScheduleBody(
        profile_id="nonexistent",
        start_time="22:00",
        end_time="06:00",
        days_of_week="0,1",
    )
    with pytest.raises(HTTPException) as exc_info:
        await create_profile_schedule(body, req)
    assert exc_info.value.status_code == 404


@pytest.mark.anyio
async def test_update_returns_success():
    req, db, cursor = _mock_request()
    cursor.rowcount = 1
    body = ProfileScheduleBody(
        profile_id="profile_quiet",
        start_time="23:00",
        end_time="07:00",
        days_of_week="0,1,2,3,4,5,6",
    )
    result = await update_profile_schedule("psched_abc", body, req)
    assert result == {"success": True}


@pytest.mark.anyio
async def test_update_missing_raises_404():
    req, db, cursor = _mock_request()
    cursor.rowcount = 0
    body = ProfileScheduleBody(
        profile_id="profile_quiet",
        start_time="23:00",
        end_time="07:00",
        days_of_week="0",
    )
    with pytest.raises(HTTPException) as exc_info:
        await update_profile_schedule("nonexistent", body, req)
    assert exc_info.value.status_code == 404


@pytest.mark.anyio
async def test_delete_missing_raises_404():
    req, db, cursor = _mock_request()
    cursor.rowcount = 0
    with pytest.raises(HTTPException) as exc_info:
        await delete_profile_schedule("nonexistent", req)
    assert exc_info.value.status_code == 404


@pytest.mark.anyio
async def test_delete_success():
    req, db, cursor = _mock_request()
    cursor.rowcount = 1
    result = await delete_profile_schedule("psched_abc", req)
    assert result is None
    assert db.commit.call_count == 1


# ---------------------------------------------------------------------------
# Validation
# ---------------------------------------------------------------------------


def test_body_rejects_invalid_time():
    with pytest.raises(Exception):
        ProfileScheduleBody(
            profile_id="p", start_time="25:00", end_time="06:00", days_of_week="0"
        )


def test_body_rejects_invalid_days():
    with pytest.raises(Exception):
        ProfileScheduleBody(
            profile_id="p", start_time="22:00", end_time="06:00", days_of_week="7"
        )


def test_body_accepts_valid_input():
    body = ProfileScheduleBody(
        profile_id="p", start_time="22:00", end_time="06:00", days_of_week="0,1,2"
    )
    assert body.start_time == "22:00"
    assert body.days_of_week == "0,1,2"


# ---------------------------------------------------------------------------
# Schedule evaluation logic
# ---------------------------------------------------------------------------


def _make_schedule(
    sid="s1", profile_id="p1", start="09:00", end="17:00",
    days="0,1,2,3,4", enabled=True, created_at="2026-01-01T00:00:00"
):
    return {
        "id": sid,
        "profile_id": profile_id,
        "start_time": start,
        "end_time": end,
        "days_of_week": days,
        "timezone": "UTC",
        "enabled": enabled,
        "created_at": created_at,
    }


def test_find_active_schedule_match():
    # Monday at 10:00
    now = datetime(2026, 3, 9, 10, 0, tzinfo=timezone.utc)  # Monday
    schedules = [_make_schedule(days="0,1,2,3,4", start="09:00", end="17:00")]
    result = _find_active_schedule(schedules, now=now)
    assert result is not None
    assert result["id"] == "s1"


def test_find_active_schedule_no_match_wrong_day():
    # Saturday at 10:00
    now = datetime(2026, 3, 14, 10, 0, tzinfo=timezone.utc)  # Saturday
    schedules = [_make_schedule(days="0,1,2,3,4", start="09:00", end="17:00")]
    result = _find_active_schedule(schedules, now=now)
    assert result is None


def test_find_active_schedule_no_match_wrong_time():
    # Monday at 18:00
    now = datetime(2026, 3, 9, 18, 0, tzinfo=timezone.utc)  # Monday
    schedules = [_make_schedule(days="0,1,2,3,4", start="09:00", end="17:00")]
    result = _find_active_schedule(schedules, now=now)
    assert result is None


def test_find_active_schedule_overnight_match():
    # Monday at 23:00
    now = datetime(2026, 3, 9, 23, 0, tzinfo=timezone.utc)  # Monday
    schedules = [_make_schedule(days="0", start="22:00", end="06:00")]
    result = _find_active_schedule(schedules, now=now)
    assert result is not None


def test_find_active_schedule_overnight_after_midnight():
    # Tuesday at 03:00 — the schedule is on Monday but spans overnight
    # For the current simple logic, Tuesday (day 1) must be in days_of_week
    now = datetime(2026, 3, 10, 3, 0, tzinfo=timezone.utc)  # Tuesday
    schedules = [_make_schedule(days="1", start="22:00", end="06:00")]
    result = _find_active_schedule(schedules, now=now)
    assert result is not None


def test_find_active_schedule_disabled():
    now = datetime(2026, 3, 9, 10, 0, tzinfo=timezone.utc)  # Monday
    schedules = [_make_schedule(enabled=False)]
    result = _find_active_schedule(schedules, now=now)
    assert result is None


def test_find_active_schedule_most_specific_wins():
    now = datetime(2026, 3, 9, 10, 0, tzinfo=timezone.utc)  # Monday
    s1 = _make_schedule(sid="broad", profile_id="p1", days="0,1,2,3,4")
    s2 = _make_schedule(sid="specific", profile_id="p2", days="0")
    result = _find_active_schedule([s1, s2], now=now)
    assert result["id"] == "specific"


def test_find_active_schedule_tie_uses_created_at():
    now = datetime(2026, 3, 9, 10, 0, tzinfo=timezone.utc)  # Monday
    s1 = _make_schedule(sid="old", profile_id="p1", days="0", created_at="2026-01-01")
    s2 = _make_schedule(sid="new", profile_id="p2", days="0", created_at="2026-02-01")
    result = _find_active_schedule([s1, s2], now=now)
    # Both have 1 day, so sort by created_at ascending — oldest first
    assert result["id"] == "old"


def test_find_active_schedule_empty():
    result = _find_active_schedule([])
    assert result is None
