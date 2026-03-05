"""Temperature target model — links a drive sensor to fan(s) with proportional control."""

from __future__ import annotations

from pydantic import BaseModel, Field


class TemperatureTarget(BaseModel):
    id: str
    name: str = ""
    drive_id: str | None = None
    sensor_id: str  # e.g. "hdd_temp_{drive_id}"
    fan_ids: list[str] = Field(default_factory=list)
    target_temp_c: float = Field(ge=20.0, le=85.0)
    tolerance_c: float = Field(ge=1.0, le=20.0, default=5.0)
    min_fan_speed: float = Field(ge=0.0, le=100.0, default=20.0)
    enabled: bool = True
    pid_mode: bool = False
    pid_kp: float = Field(ge=0.0, le=100.0, default=5.0)
    pid_ki: float = Field(ge=0.0, le=10.0, default=0.05)
    pid_kd: float = Field(ge=0.0, le=100.0, default=1.0)


class TemperatureTargetCreate(BaseModel):
    """Request body for POST /api/temperature-targets."""
    name: str = ""
    drive_id: str | None = None
    sensor_id: str
    fan_ids: list[str] = Field(default_factory=list)
    target_temp_c: float = Field(ge=20.0, le=85.0)
    tolerance_c: float = Field(ge=1.0, le=20.0, default=5.0)
    min_fan_speed: float = Field(ge=0.0, le=100.0, default=20.0)
    pid_mode: bool = False
    pid_kp: float = Field(ge=0.0, le=100.0, default=5.0)
    pid_ki: float = Field(ge=0.0, le=10.0, default=0.05)
    pid_kd: float = Field(ge=0.0, le=100.0, default=1.0)


class TemperatureTargetUpdate(BaseModel):
    """Request body for PUT /api/temperature-targets/{id}."""
    name: str = ""
    drive_id: str | None = None
    sensor_id: str
    fan_ids: list[str] = Field(default_factory=list)
    target_temp_c: float = Field(ge=20.0, le=85.0)
    tolerance_c: float = Field(ge=1.0, le=20.0, default=5.0)
    min_fan_speed: float = Field(ge=0.0, le=100.0, default=20.0)
    pid_mode: bool = False
    pid_kp: float = Field(ge=0.0, le=100.0, default=5.0)
    pid_ki: float = Field(ge=0.0, le=10.0, default=0.05)
    pid_kd: float = Field(ge=0.0, le=100.0, default=1.0)


class TemperatureTargetToggle(BaseModel):
    """Request body for PATCH /api/temperature-targets/{id}/enabled."""
    enabled: bool
