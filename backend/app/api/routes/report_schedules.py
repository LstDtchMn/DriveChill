"""Report schedule CRUD endpoints."""

from __future__ import annotations

import uuid
from datetime import datetime, timezone
from typing import Literal

from zoneinfo import ZoneInfo

from fastapi import APIRouter, Depends, HTTPException, Request
from pydantic import BaseModel, Field, field_validator

from app.api.dependencies.auth import require_csrf

router = APIRouter(prefix="/api/report-schedules", tags=["report-schedules"])


# ---------------------------------------------------------------------------
# Request schemas
# ---------------------------------------------------------------------------


def _validate_iana_tz(v: str) -> str:
    try:
        ZoneInfo(v)
    except (KeyError, Exception):
        raise ValueError(f"Invalid IANA timezone: {v}")
    return v


class ReportScheduleBody(BaseModel):
    frequency: Literal["daily", "weekly"]
    time_utc: str = Field(min_length=1, max_length=10)
    timezone: str = Field(default="UTC", max_length=100)
    enabled: bool = True

    @field_validator("timezone")
    @classmethod
    def check_tz(cls, v: str) -> str:
        return _validate_iana_tz(v)


class UpdateReportScheduleBody(BaseModel):
    frequency: Literal["daily", "weekly"] | None = None
    time_utc: str | None = Field(default=None, max_length=10)
    timezone: str | None = Field(default=None, max_length=100)
    enabled: bool | None = None

    @field_validator("timezone")
    @classmethod
    def check_tz(cls, v: str | None) -> str | None:
        if v is not None:
            return _validate_iana_tz(v)
        return v


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------


def _row_to_dict(row) -> dict:
    d = {
        "id": row[0],
        "frequency": row[1],
        "time_utc": row[2],
        "timezone": row[3],
        "enabled": bool(row[4]),
        "last_sent_at": row[5],
        "created_at": row[6],
    }
    # Observability columns (present after migration 021)
    if len(row) > 7:
        d["last_error"] = row[7]
        d["last_attempted_at"] = row[8]
        d["consecutive_failures"] = row[9] or 0
    return d


# ---------------------------------------------------------------------------
# Endpoints
# ---------------------------------------------------------------------------


@router.get("")
async def list_report_schedules(request: Request):
    """List all report schedules."""
    db = request.app.state.db
    cursor = await db.execute(
        "SELECT id, frequency, time_utc, timezone, enabled, last_sent_at, created_at, last_error, last_attempted_at, consecutive_failures "
        "FROM report_schedules ORDER BY created_at ASC"
    )
    rows = await cursor.fetchall()
    return {"schedules": [_row_to_dict(r) for r in rows]}


@router.post("", dependencies=[Depends(require_csrf)])
async def create_report_schedule(body: ReportScheduleBody, request: Request):
    """Create a new report schedule."""
    db = request.app.state.db
    schedule_id = f"rs_{uuid.uuid4().hex[:12]}"
    now = datetime.now(timezone.utc).isoformat()
    await db.execute(
        "INSERT INTO report_schedules (id, frequency, time_utc, timezone, enabled, created_at) "
        "VALUES (?, ?, ?, ?, ?, ?)",
        (schedule_id, body.frequency, body.time_utc, body.timezone, int(body.enabled), now),
    )
    await db.commit()
    cursor = await db.execute(
        "SELECT id, frequency, time_utc, timezone, enabled, last_sent_at, created_at, last_error, last_attempted_at, consecutive_failures "
        "FROM report_schedules WHERE id=?",
        (schedule_id,),
    )
    row = await cursor.fetchone()
    return _row_to_dict(row)


@router.put("/{schedule_id}", dependencies=[Depends(require_csrf)])
async def update_report_schedule(
    schedule_id: str, body: UpdateReportScheduleBody, request: Request
):
    """Update an existing report schedule (only supplied fields are changed)."""
    db = request.app.state.db
    cursor = await db.execute(
        "SELECT id FROM report_schedules WHERE id=?", (schedule_id,)
    )
    if await cursor.fetchone() is None:
        raise HTTPException(status_code=404, detail="Report schedule not found")

    fields = body.model_fields_set
    if not fields:
        raise HTTPException(status_code=400, detail="No fields to update")

    _ALLOWED_COLUMNS = {"frequency": "frequency", "time_utc": "time_utc",
                        "timezone": "timezone", "enabled": "enabled"}
    set_clauses = []
    params: list = []
    for field in fields:
        col = _ALLOWED_COLUMNS.get(field)
        if col is None:
            continue
        value = getattr(body, field)
        if field == "enabled":
            value = int(value)
        set_clauses.append(f"{col}=?")
        params.append(value)

    if not set_clauses:
        raise HTTPException(status_code=400, detail="No valid fields to update")

    params.append(schedule_id)
    await db.execute(
        f"UPDATE report_schedules SET {', '.join(set_clauses)} WHERE id=?",
        params,
    )
    await db.commit()

    cursor = await db.execute(
        "SELECT id, frequency, time_utc, timezone, enabled, last_sent_at, created_at, last_error, last_attempted_at, consecutive_failures "
        "FROM report_schedules WHERE id=?",
        (schedule_id,),
    )
    row = await cursor.fetchone()
    return _row_to_dict(row)


@router.delete("/{schedule_id}", dependencies=[Depends(require_csrf)])
async def delete_report_schedule(schedule_id: str, request: Request):
    """Delete a report schedule."""
    db = request.app.state.db
    cursor = await db.execute(
        "DELETE FROM report_schedules WHERE id=?", (schedule_id,)
    )
    await db.commit()
    if cursor.rowcount == 0:
        raise HTTPException(status_code=404, detail="Report schedule not found")
    return {"success": True}
