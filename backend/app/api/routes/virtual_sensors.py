import json
import uuid
from datetime import datetime, timezone

from fastapi import APIRouter, Depends, Request, HTTPException
from pydantic import BaseModel, Field, field_validator

from app.api.dependencies.auth import require_csrf
from app.services.virtual_sensor_service import (
    VirtualSensorDef,
    VIRTUAL_SENSOR_TYPES,
)

router = APIRouter(prefix="/api/virtual-sensors", tags=["virtual-sensors"])


class VirtualSensorBody(BaseModel):
    name: str = Field(min_length=1, max_length=100)
    type: str = Field(default="max")
    source_ids: list[str] = Field(min_length=1)
    weights: list[float] | None = None
    window_seconds: float | None = None
    offset: float = 0.0
    enabled: bool = True

    @field_validator("type")
    @classmethod
    def validate_type(cls, v: str) -> str:
        if v not in VIRTUAL_SENSOR_TYPES:
            raise ValueError(
                f"Invalid type '{v}'. Must be one of: {', '.join(sorted(VIRTUAL_SENSOR_TYPES))}"
            )
        return v

    @field_validator("source_ids")
    @classmethod
    def validate_source_ids(cls, v: list[str]) -> list[str]:
        if not v:
            raise ValueError("At least one source sensor ID is required")
        return v


def _row_to_dict(row) -> dict:
    return {
        "id": row[0],
        "name": row[1],
        "type": row[2],
        "source_ids": json.loads(row[3]) if row[3] else [],
        "weights": json.loads(row[4]) if row[4] else None,
        "window_seconds": row[5],
        "offset": row[6],
        "enabled": bool(row[7]),
        "created_at": row[8],
        "updated_at": row[9],
    }


async def _reload_virtual_sensors(request: Request) -> None:
    """Reload virtual sensor definitions from DB into the service."""
    vs_service = getattr(request.app.state, "virtual_sensor_service", None)
    if vs_service is None:
        return
    db = request.app.state.db
    cursor = await db.execute(
        "SELECT id, name, type, source_ids_json, weights_json, "
        "window_seconds, \"offset\", enabled, created_at, updated_at "
        "FROM virtual_sensors ORDER BY name"
    )
    rows = await cursor.fetchall()
    defs = [
        VirtualSensorDef(
            id=r[0],
            name=r[1],
            type=r[2],
            source_ids=json.loads(r[3]) if r[3] else [],
            weights=json.loads(r[4]) if r[4] else None,
            window_seconds=r[5],
            offset=r[6] or 0.0,
            enabled=bool(r[7]),
        )
        for r in rows
    ]
    vs_service.load(defs)


@router.get("")
async def list_virtual_sensors(request: Request):
    """Get all virtual sensors."""
    db = request.app.state.db
    cursor = await db.execute(
        "SELECT id, name, type, source_ids_json, weights_json, "
        "window_seconds, \"offset\", enabled, created_at, updated_at "
        "FROM virtual_sensors ORDER BY name"
    )
    rows = await cursor.fetchall()
    return {"virtual_sensors": [_row_to_dict(r) for r in rows]}


@router.post("", dependencies=[Depends(require_csrf)])
async def create_virtual_sensor(body: VirtualSensorBody, request: Request):
    """Create a virtual sensor."""
    db = request.app.state.db
    vs_id = f"vs_{uuid.uuid4().hex[:12]}"
    now = datetime.now(timezone.utc).isoformat()
    await db.execute(
        "INSERT INTO virtual_sensors "
        "(id, name, type, source_ids_json, weights_json, window_seconds, "
        "\"offset\", enabled, created_at, updated_at) "
        "VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
        (
            vs_id,
            body.name,
            body.type,
            json.dumps(body.source_ids),
            json.dumps(body.weights) if body.weights else None,
            body.window_seconds,
            body.offset,
            int(body.enabled),
            now,
            now,
        ),
    )
    await db.commit()
    await _reload_virtual_sensors(request)
    return {"success": True, "id": vs_id}


@router.put("/{sensor_id}", dependencies=[Depends(require_csrf)])
async def update_virtual_sensor(
    sensor_id: str, body: VirtualSensorBody, request: Request
):
    """Update a virtual sensor."""
    db = request.app.state.db
    now = datetime.now(timezone.utc).isoformat()
    cursor = await db.execute(
        "UPDATE virtual_sensors SET name=?, type=?, source_ids_json=?, "
        "weights_json=?, window_seconds=?, \"offset\"=?, enabled=?, updated_at=? "
        "WHERE id=?",
        (
            body.name,
            body.type,
            json.dumps(body.source_ids),
            json.dumps(body.weights) if body.weights else None,
            body.window_seconds,
            body.offset,
            int(body.enabled),
            now,
            sensor_id,
        ),
    )
    await db.commit()
    if cursor.rowcount == 0:
        raise HTTPException(status_code=404, detail="Virtual sensor not found")
    await _reload_virtual_sensors(request)
    return {"success": True}


@router.delete("/{sensor_id}", dependencies=[Depends(require_csrf)])
async def delete_virtual_sensor(sensor_id: str, request: Request):
    """Delete a virtual sensor."""
    db = request.app.state.db
    cursor = await db.execute(
        "DELETE FROM virtual_sensors WHERE id=?", (sensor_id,)
    )
    await db.commit()
    if cursor.rowcount == 0:
        raise HTTPException(status_code=404, detail="Virtual sensor not found")
    await _reload_virtual_sensors(request)
    return {"success": True}
