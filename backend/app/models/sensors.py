from enum import Enum
from datetime import datetime

from pydantic import BaseModel


class SensorType(str, Enum):
    CPU_TEMP = "cpu_temp"
    GPU_TEMP = "gpu_temp"
    HDD_TEMP = "hdd_temp"
    CASE_TEMP = "case_temp"
    CPU_LOAD = "cpu_load"
    GPU_LOAD = "gpu_load"
    FAN_RPM = "fan_rpm"
    FAN_PERCENT = "fan_percent"


class SensorReading(BaseModel):
    id: str  # e.g. "cpu_temp_0", "hdd_temp_sda"
    name: str  # Human-readable name
    sensor_type: SensorType
    value: float
    min_value: float | None = None
    max_value: float | None = None
    unit: str = "°C"
    # Additive drive metadata — only set for hdd_temp sensors from drive monitoring
    drive_id: str | None = None
    entity_name: str | None = None  # Human-readable drive name
    source_kind: str | None = None  # "native" | "smartctl" | "unknown"


class SensorSnapshot(BaseModel):
    timestamp: datetime
    readings: list[SensorReading]
