from fastapi import APIRouter, Request
from pydantic import BaseModel

from app.config import settings

router = APIRouter(prefix="/api/settings", tags=["settings"])


class SettingsResponse(BaseModel):
    sensor_poll_interval: float
    history_retention_hours: int
    temp_unit: str
    hardware_backend: str
    backend_name: str


class UpdateSettingsRequest(BaseModel):
    sensor_poll_interval: float | None = None
    history_retention_hours: int | None = None
    temp_unit: str | None = None


@router.get("")
async def get_settings(request: Request):
    """Get current application settings."""
    return SettingsResponse(
        sensor_poll_interval=settings.sensor_poll_interval,
        history_retention_hours=settings.history_retention_hours,
        temp_unit=settings.temp_unit,
        hardware_backend=settings.hardware_backend,
        backend_name=request.app.state.backend.get_backend_name(),
    ).model_dump()


@router.put("")
async def update_settings(body: UpdateSettingsRequest):
    """Update application settings."""
    if body.sensor_poll_interval is not None:
        settings.sensor_poll_interval = body.sensor_poll_interval
    if body.history_retention_hours is not None:
        settings.history_retention_hours = body.history_retention_hours
    if body.temp_unit is not None:
        settings.temp_unit = body.temp_unit

    return {"success": True, "settings": {
        "sensor_poll_interval": settings.sensor_poll_interval,
        "history_retention_hours": settings.history_retention_hours,
        "temp_unit": settings.temp_unit,
    }}
