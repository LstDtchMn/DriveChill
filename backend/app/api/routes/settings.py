from fastapi import APIRouter, Depends, Request

from app.api.dependencies.auth import require_csrf
from pydantic import BaseModel, field_validator

from app.config import settings as app_config

router = APIRouter(prefix="/api/settings", tags=["settings"])


class SettingsResponse(BaseModel):
    sensor_poll_interval: float
    history_retention_hours: int
    temp_unit: str
    hardware_backend: str
    backend_name: str
    fan_ramp_rate_pct_per_sec: float


class UpdateSettingsRequest(BaseModel):
    sensor_poll_interval: float | None = None
    history_retention_hours: int | None = None
    temp_unit: str | None = None
    fan_ramp_rate_pct_per_sec: float | None = None

    @field_validator("sensor_poll_interval")
    @classmethod
    def validate_poll_interval(cls, v: float | None) -> float | None:
        # H-6: 0 or negative would create a busy-loop in the sensor service
        if v is not None and v < 0.5:
            raise ValueError("sensor_poll_interval must be at least 0.5 seconds")
        return v

    @field_validator("temp_unit")
    @classmethod
    def validate_temp_unit(cls, v: str | None) -> str | None:
        # H-6: only "C" and "F" are valid - reject anything else silently stored
        if v is not None and v not in ("C", "F"):
            raise ValueError("temp_unit must be 'C' or 'F'")
        return v

    @field_validator("history_retention_hours")
    @classmethod
    def validate_history_retention(cls, v: int | None) -> int | None:
        # Keep retention bounded to the same one-year cap used by history/export APIs.
        if v is not None and not (1 <= v <= 8760):
            raise ValueError("history_retention_hours must be between 1 and 8760")
        return v


@router.get("")
async def get_settings(request: Request):
    """Get current application settings."""
    repo = request.app.state.settings_repo
    return SettingsResponse(
        sensor_poll_interval=await repo.get_float("sensor_poll_interval", 1.0),
        history_retention_hours=await repo.get_int("history_retention_hours", 720),
        temp_unit=(await repo.get("temp_unit")) or "C",
        hardware_backend=app_config.hardware_backend,
        backend_name=request.app.state.backend.get_backend_name(),
        fan_ramp_rate_pct_per_sec=await repo.get_float("fan_ramp_rate_pct_per_sec", 0.0),
    ).model_dump()


@router.put("", dependencies=[Depends(require_csrf)])
async def update_settings(body: UpdateSettingsRequest, request: Request):
    """Update application settings (persisted to SQLite)."""
    repo = request.app.state.settings_repo
    updates: dict[str, str] = {}

    if body.sensor_poll_interval is not None:
        updates["sensor_poll_interval"] = str(body.sensor_poll_interval)
    if body.history_retention_hours is not None:
        updates["history_retention_hours"] = str(body.history_retention_hours)
    if body.temp_unit is not None:
        updates["temp_unit"] = body.temp_unit
    if body.fan_ramp_rate_pct_per_sec is not None:
        updates["fan_ramp_rate_pct_per_sec"] = str(body.fan_ramp_rate_pct_per_sec)

    if updates:
        await repo.set_many(updates)

    # Apply runtime-adjustable settings to running services so changes
    # take effect immediately (no restart required).
    if body.sensor_poll_interval is not None:
        sensor_svc = request.app.state.sensor_service
        sensor_svc.poll_interval = body.sensor_poll_interval

    if body.fan_ramp_rate_pct_per_sec is not None:
        fan_svc = request.app.state.fan_service
        fan_svc.configure_ramp_rate(body.fan_ramp_rate_pct_per_sec)

    return {"success": True, "settings": {
        "sensor_poll_interval": await repo.get_float("sensor_poll_interval", 1.0),
        "history_retention_hours": await repo.get_int("history_retention_hours", 720),
        "temp_unit": (await repo.get("temp_unit")) or "C",
        "fan_ramp_rate_pct_per_sec": await repo.get_float("fan_ramp_rate_pct_per_sec", 0.0),
    }}
