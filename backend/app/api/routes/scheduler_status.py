"""Scheduler observability endpoint."""

from __future__ import annotations

from datetime import datetime, timedelta, timezone
from zoneinfo import ZoneInfo

from fastapi import APIRouter, Request

router = APIRouter(prefix="/api/scheduler", tags=["scheduler"])


def _next_due_at(schedule: dict, now: datetime) -> str | None:
    """Compute the next UTC time this schedule will fire."""
    try:
        hh, mm = schedule["time_utc"].split(":")
        hour, minute = int(hh), int(mm)
    except (ValueError, AttributeError):
        return None

    tz_name = schedule.get("timezone")
    try:
        tz = ZoneInfo(tz_name) if tz_name else timezone.utc
    except (KeyError, TypeError):
        tz = timezone.utc

    local_now = now.astimezone(tz)
    candidate = local_now.replace(hour=hour, minute=minute, second=0, microsecond=0)

    if candidate <= local_now:
        days = 7 if schedule.get("frequency") == "weekly" else 1
        candidate = candidate + timedelta(days=days)

    return candidate.astimezone(timezone.utc).isoformat()


@router.get("/status")
async def get_scheduler_status(request: Request):
    """Return observability status for all schedulers."""
    db = request.app.state.db
    now = datetime.now(timezone.utc)

    # Profile scheduler status
    profile_scheduler = getattr(request.app.state, "profile_scheduler", None)
    profile_status = {
        "running": getattr(profile_scheduler, "_running", False) if profile_scheduler else False,
        "active_schedule_id": getattr(profile_scheduler, "_active_schedule_id", None) if profile_scheduler else None,
        "last_check_at": profile_scheduler.last_check_at.isoformat() if profile_scheduler and profile_scheduler.last_check_at else None,
    }

    # Report scheduler status
    report_scheduler = getattr(request.app.state, "report_scheduler", None)
    cursor = await db.execute(
        "SELECT id, frequency, time_utc, timezone, enabled, last_sent_at, created_at, "
        "last_error, last_attempted_at, consecutive_failures "
        "FROM report_schedules ORDER BY created_at ASC"
    )
    rows = await cursor.fetchall()

    schedule_items = []
    for r in rows:
        s = {
            "id": r[0],
            "frequency": r[1],
            "time_utc": r[2],
            "timezone": r[3],
            "enabled": bool(r[4]),
            "last_sent_at": r[5],
            "last_attempted_at": r[8],
            "last_error": r[7],
            "consecutive_failures": r[9] or 0,
            "next_due_at": _next_due_at(
                {"time_utc": r[2], "timezone": r[3], "frequency": r[1]}, now
            ) if bool(r[4]) else None,
        }
        schedule_items.append(s)

    report_status = {
        "running": report_scheduler.running if report_scheduler else False,
        "last_check_at": report_scheduler.last_check_at.isoformat() if report_scheduler and report_scheduler.last_check_at else None,
        "schedules": schedule_items,
    }

    return {
        "profile_scheduler": profile_status,
        "report_scheduler": report_status,
    }
