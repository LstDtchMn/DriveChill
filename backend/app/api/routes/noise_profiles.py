"""Noise profile CRUD — stores fan noise-vs-RPM sweep data."""

import json
import uuid
from datetime import datetime, timezone

from fastapi import APIRouter, Depends, HTTPException, Request
from pydantic import BaseModel, Field

from app.api.dependencies.auth import require_csrf

router = APIRouter(prefix="/api/noise-profiles", tags=["noise-profiles"])


class NoiseDataPoint(BaseModel):
    rpm: float = Field(ge=0.0)
    db: float = Field(ge=0.0)


class NoiseProfileBody(BaseModel):
    fan_id: str = Field(min_length=1, max_length=200)
    mode: str = Field(default="quick")
    data: list[NoiseDataPoint] = Field(min_length=1)

    @classmethod
    def validate_mode(cls, v: str) -> str:
        if v not in {"quick", "precise"}:
            raise ValueError("mode must be 'quick' or 'precise'")
        return v

    def model_post_init(self, __context) -> None:
        if self.mode not in {"quick", "precise"}:
            raise ValueError("mode must be 'quick' or 'precise'")


def _row_to_dict(row) -> dict:
    return {
        "id": row[0],
        "fan_id": row[1],
        "mode": row[2],
        "data": json.loads(row[3]) if row[3] else [],
        "created_at": row[4],
        "updated_at": row[5],
    }


@router.get("")
async def list_noise_profiles(request: Request):
    """List all noise profiles."""
    db = request.app.state.db
    cursor = await db.execute(
        "SELECT id, fan_id, mode, data_json, created_at, updated_at "
        "FROM noise_profiles ORDER BY created_at DESC"
    )
    rows = await cursor.fetchall()
    return {"profiles": [_row_to_dict(r) for r in rows]}


@router.get("/{profile_id}")
async def get_noise_profile(profile_id: str, request: Request):
    """Get a single noise profile by ID."""
    db = request.app.state.db
    cursor = await db.execute(
        "SELECT id, fan_id, mode, data_json, created_at, updated_at "
        "FROM noise_profiles WHERE id=?",
        (profile_id,),
    )
    row = await cursor.fetchone()
    if row is None:
        raise HTTPException(status_code=404, detail="Noise profile not found")
    return _row_to_dict(row)


@router.post("", dependencies=[Depends(require_csrf)])
async def create_noise_profile(body: NoiseProfileBody, request: Request):
    """Create a new noise profile."""
    db = request.app.state.db
    profile_id = f"np_{uuid.uuid4().hex[:12]}"
    now = datetime.now(timezone.utc).isoformat()
    data_json = json.dumps([{"rpm": p.rpm, "db": p.db} for p in body.data])
    await db.execute(
        "INSERT INTO noise_profiles (id, fan_id, mode, data_json, created_at, updated_at) "
        "VALUES (?, ?, ?, ?, ?, ?)",
        (profile_id, body.fan_id, body.mode, data_json, now, now),
    )
    await db.commit()
    cursor = await db.execute(
        "SELECT id, fan_id, mode, data_json, created_at, updated_at "
        "FROM noise_profiles WHERE id=?",
        (profile_id,),
    )
    row = await cursor.fetchone()
    return _row_to_dict(row)


@router.delete("/{profile_id}", dependencies=[Depends(require_csrf)])
async def delete_noise_profile(profile_id: str, request: Request):
    """Delete a noise profile."""
    db = request.app.state.db
    cursor = await db.execute(
        "DELETE FROM noise_profiles WHERE id=?", (profile_id,)
    )
    await db.commit()
    if cursor.rowcount == 0:
        raise HTTPException(status_code=404, detail="Noise profile not found")
    return {"success": True}
