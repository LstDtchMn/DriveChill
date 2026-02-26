from fastapi import APIRouter, Depends, Request, HTTPException

from app.api.dependencies.auth import require_csrf
from pydantic import BaseModel, Field, field_validator

router = APIRouter(prefix="/api/quiet-hours", tags=["quiet-hours"])


class QuietHoursRule(BaseModel):
    day_of_week: int = Field(ge=0, le=6, description="0=Monday, 6=Sunday")
    start_time: str = Field(pattern=r"^\d{2}:\d{2}$", description="HH:MM (24h)")
    end_time: str = Field(pattern=r"^\d{2}:\d{2}$", description="HH:MM (24h)")
    profile_id: str
    enabled: bool = True

    @field_validator("start_time", "end_time")
    @classmethod
    def validate_time(cls, v: str) -> str:
        parts = v.split(":")
        h, m = int(parts[0]), int(parts[1])
        if not (0 <= h <= 23 and 0 <= m <= 59):
            raise ValueError("Time must be HH:MM with hours 00-23 and minutes 00-59")
        return v


class QuietHoursRuleResponse(QuietHoursRule):
    id: int


async def _check_profile_exists(request: Request, profile_id: str) -> None:
    profile_repo = request.app.state.profile_repo
    profile = await profile_repo.get(profile_id)
    if profile is None:
        raise HTTPException(status_code=404, detail=f"Profile '{profile_id}' not found")


@router.get("")
async def list_rules(request: Request):
    """Get all quiet hours rules."""
    db = request.app.state.db
    cursor = await db.execute(
        "SELECT id, day_of_week, start_time, end_time, profile_id, enabled "
        "FROM quiet_hours ORDER BY day_of_week, start_time"
    )
    rows = await cursor.fetchall()
    rules = [
        {
            "id": r[0], "day_of_week": r[1], "start_time": r[2],
            "end_time": r[3], "profile_id": r[4], "enabled": bool(r[5]),
        }
        for r in rows
    ]
    return {"rules": rules}


@router.post("", dependencies=[Depends(require_csrf)])
async def create_rule(body: QuietHoursRule, request: Request):
    """Create a quiet hours rule."""
    await _check_profile_exists(request, body.profile_id)
    db = request.app.state.db
    cursor = await db.execute(
        "INSERT INTO quiet_hours (day_of_week, start_time, end_time, profile_id, enabled) "
        "VALUES (?, ?, ?, ?, ?)",
        (body.day_of_week, body.start_time, body.end_time,
         body.profile_id, int(body.enabled)),
    )
    await db.commit()
    return {"success": True, "id": cursor.lastrowid}


@router.put("/{rule_id}", dependencies=[Depends(require_csrf)])
async def update_rule(rule_id: int, body: QuietHoursRule, request: Request):
    """Update a quiet hours rule."""
    await _check_profile_exists(request, body.profile_id)
    db = request.app.state.db
    cursor = await db.execute(
        "UPDATE quiet_hours SET day_of_week=?, start_time=?, end_time=?, "
        "profile_id=?, enabled=? WHERE id=?",
        (body.day_of_week, body.start_time, body.end_time,
         body.profile_id, int(body.enabled), rule_id),
    )
    await db.commit()
    if cursor.rowcount == 0:
        raise HTTPException(status_code=404, detail="Rule not found")
    return {"success": True}


@router.delete("/{rule_id}", dependencies=[Depends(require_csrf)])
async def delete_rule(rule_id: int, request: Request):
    """Delete a quiet hours rule."""
    db = request.app.state.db
    # H-5: check rowcount so deleting a non-existent ID returns 404, not 200
    cursor = await db.execute("DELETE FROM quiet_hours WHERE id=?", (rule_id,))
    await db.commit()
    if cursor.rowcount == 0:
        raise HTTPException(status_code=404, detail="Rule not found")
    return {"success": True}
