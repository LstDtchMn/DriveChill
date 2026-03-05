from datetime import datetime, timezone

from fastapi import APIRouter, Depends, Request, HTTPException
from pydantic import BaseModel, Field

from app.api.dependencies.auth import require_auth, require_csrf

router = APIRouter(prefix="/api/sensors", tags=["sensors"])


async def _get_labels(request: Request) -> dict[str, str]:
    """Read all sensor labels from DB."""
    db = request.app.state.db
    cursor = await db.execute("SELECT sensor_id, label FROM sensor_labels")
    rows = await cursor.fetchall()
    return {row[0]: row[1] for row in rows}


@router.get("", dependencies=[Depends(require_auth)])
async def get_sensors(request: Request):
    """Get current sensor readings with user labels applied."""
    sensor_service = request.app.state.sensor_service
    readings = sensor_service.latest
    labels = await _get_labels(request)
    dumped = []
    for r in readings:
        d = r.model_dump()
        if r.id in labels:
            d["label"] = labels[r.id]
        dumped.append(d)
    return {
        "readings": dumped,
        "backend": request.app.state.backend.get_backend_name(),
    }


@router.get("/labels", dependencies=[Depends(require_auth)])
async def get_labels(request: Request):
    """Get all user-defined sensor labels."""
    labels = await _get_labels(request)
    return {"labels": labels}


class SetLabelRequest(BaseModel):
    label: str = Field(min_length=1, max_length=100)


@router.put("/{sensor_id}/label", dependencies=[Depends(require_csrf)])
async def set_label(sensor_id: str, body: SetLabelRequest, request: Request):
    """Set a custom label for a sensor."""
    if len(sensor_id) > 200:
        raise HTTPException(status_code=422, detail="sensor_id too long")
    db = request.app.state.db
    now = datetime.now(timezone.utc).isoformat()
    await db.execute(
        "INSERT OR REPLACE INTO sensor_labels (sensor_id, label, updated_at) "
        "VALUES (?, ?, ?)",
        (sensor_id, body.label, now),
    )
    await db.commit()
    return {"success": True, "sensor_id": sensor_id, "label": body.label}


@router.delete("/{sensor_id}/label", dependencies=[Depends(require_csrf)])
async def delete_label(sensor_id: str, request: Request):
    """Remove the custom label for a sensor (revert to hardware name)."""
    db = request.app.state.db
    cursor = await db.execute(
        "DELETE FROM sensor_labels WHERE sensor_id = ?", (sensor_id,)
    )
    await db.commit()
    if cursor.rowcount == 0:
        raise HTTPException(status_code=404, detail="No label found for this sensor")
    return {"success": True, "sensor_id": sensor_id}


@router.get("/history", dependencies=[Depends(require_auth)])
async def get_history(
    request: Request,
    sensor_id: str | None = None,
    hours: int = 1,
):
    """Get historical sensor data."""
    hours = max(1, min(hours, 8760))  # Cap at 1 year
    logging_service = request.app.state.logging_service
    data = await logging_service.get_history(sensor_id=sensor_id, hours=hours)
    return {"data": data}


@router.get("/export", dependencies=[Depends(require_auth)])
async def export_csv(
    request: Request,
    sensor_id: str | None = None,
    hours: int = 24,
):
    """Export sensor history as CSV."""
    from fastapi.responses import PlainTextResponse

    hours = max(1, min(hours, 8760))  # Cap at 1 year
    logging_service = request.app.state.logging_service
    csv = await logging_service.export_csv(sensor_id=sensor_id, hours=hours)
    return PlainTextResponse(
        csv,
        media_type="text/csv",
        headers={"Content-Disposition": "attachment; filename=drivechill_export.csv"},
    )
