from fastapi import APIRouter, Request

router = APIRouter(prefix="/api/sensors", tags=["sensors"])


@router.get("")
async def get_sensors(request: Request):
    """Get current sensor readings."""
    sensor_service = request.app.state.sensor_service
    readings = sensor_service.latest
    return {
        "readings": [r.model_dump() for r in readings],
        "backend": request.app.state.backend.get_backend_name(),
    }


@router.get("/history")
async def get_history(
    request: Request,
    sensor_id: str | None = None,
    hours: int = 1,
):
    """Get historical sensor data."""
    logging_service = request.app.state.logging_service
    data = await logging_service.get_history(sensor_id=sensor_id, hours=hours)
    return {"data": data}


@router.get("/export")
async def export_csv(
    request: Request,
    sensor_id: str | None = None,
    hours: int = 24,
):
    """Export sensor history as CSV."""
    from fastapi.responses import PlainTextResponse

    logging_service = request.app.state.logging_service
    csv = await logging_service.export_csv(sensor_id=sensor_id, hours=hours)
    return PlainTextResponse(
        csv,
        media_type="text/csv",
        headers={"Content-Disposition": "attachment; filename=drivechill_export.csv"},
    )
