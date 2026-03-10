"""Profile schedule CRUD — automatic profile switching on time-of-day rules."""

import uuid
from datetime import datetime, timezone

from fastapi import APIRouter, Depends, HTTPException, Request
from pydantic import BaseModel, Field, field_validator

from app.api.dependencies.auth import require_csrf

router = APIRouter(prefix="/api/profile-schedules", tags=["profile-schedules"])


class ProfileScheduleBody(BaseModel):
    profile_id: str = Field(min_length=1)
    start_time: str = Field(pattern=r"^\d{2}:\d{2}$")
    end_time: str = Field(pattern=r"^\d{2}:\d{2}$")
    days_of_week: str = Field(min_length=1)
    timezone: str = Field(default="UTC", max_length=100)
    enabled: bool = True

    @field_validator("start_time", "end_time")
    @classmethod
    def validate_time(cls, v: str) -> str:
        parts = v.split(":")
        h, m = int(parts[0]), int(parts[1])
        if not (0 <= h <= 23 and 0 <= m <= 59):
            raise ValueError("Time must be HH:MM with hours 00-23 and minutes 00-59")
        return v

    @field_validator("days_of_week")
    @classmethod
    def validate_days(cls, v: str) -> str:
        for d in v.split(","):
            d = d.strip()
            if not d.isdigit() or int(d) < 0 or int(d) > 6:
                raise ValueError("days_of_week must be comma-separated integers 0-6")
        return v


def _row_to_dict(row) -> dict:
    return {
        "id": row[0],
        "profile_id": row[1],
        "start_time": row[2],
        "end_time": row[3],
        "days_of_week": row[4],
        "timezone": row[5],
        "enabled": bool(row[6]),
        "created_at": row[7],
    }


@router.get("")
async def list_profile_schedules(request: Request):
    """List all profile schedules."""
    db = request.app.state.db
    cursor = await db.execute(
        "SELECT id, profile_id, start_time, end_time, days_of_week, timezone, enabled, created_at "
        "FROM profile_schedules ORDER BY created_at"
    )
    rows = await cursor.fetchall()
    return {"schedules": [_row_to_dict(r) for r in rows]}


@router.post("", dependencies=[Depends(require_csrf)])
async def create_profile_schedule(body: ProfileScheduleBody, request: Request):
    """Create a new profile schedule."""
    # Verify profile exists
    profile_repo = request.app.state.profile_repo
    profile = await profile_repo.get(body.profile_id)
    if profile is None:
        raise HTTPException(status_code=404, detail=f"Profile '{body.profile_id}' not found")

    db = request.app.state.db
    schedule_id = f"psched_{uuid.uuid4().hex[:12]}"
    now = datetime.now(timezone.utc).isoformat()
    await db.execute(
        "INSERT INTO profile_schedules (id, profile_id, start_time, end_time, days_of_week, timezone, enabled, created_at) "
        "VALUES (?, ?, ?, ?, ?, ?, ?, ?)",
        (schedule_id, body.profile_id, body.start_time, body.end_time,
         body.days_of_week, body.timezone, int(body.enabled), now),
    )
    await db.commit()
    cursor = await db.execute(
        "SELECT id, profile_id, start_time, end_time, days_of_week, timezone, enabled, created_at "
        "FROM profile_schedules WHERE id = ?",
        (schedule_id,),
    )
    row = await cursor.fetchone()
    return _row_to_dict(row)


@router.put("/{schedule_id}", dependencies=[Depends(require_csrf)])
async def update_profile_schedule(schedule_id: str, body: ProfileScheduleBody, request: Request):
    """Update a profile schedule."""
    # Verify profile exists
    profile_repo = request.app.state.profile_repo
    profile = await profile_repo.get(body.profile_id)
    if profile is None:
        raise HTTPException(status_code=404, detail=f"Profile '{body.profile_id}' not found")

    db = request.app.state.db
    cursor = await db.execute(
        "UPDATE profile_schedules SET profile_id=?, start_time=?, end_time=?, "
        "days_of_week=?, timezone=?, enabled=? WHERE id=?",
        (body.profile_id, body.start_time, body.end_time,
         body.days_of_week, body.timezone, int(body.enabled), schedule_id),
    )
    await db.commit()
    if cursor.rowcount == 0:
        raise HTTPException(status_code=404, detail="Schedule not found")
    return {"success": True}


@router.delete("/{schedule_id}", dependencies=[Depends(require_csrf)], status_code=204)
async def delete_profile_schedule(schedule_id: str, request: Request):
    """Delete a profile schedule."""
    db = request.app.state.db
    cursor = await db.execute(
        "DELETE FROM profile_schedules WHERE id = ?",
        (schedule_id,),
    )
    await db.commit()
    if cursor.rowcount == 0:
        raise HTTPException(status_code=404, detail="Schedule not found")
    return None
