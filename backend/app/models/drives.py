"""Shared data models for the drive monitoring subsystem."""
from __future__ import annotations

import re
from enum import Enum
from typing import Any
from pydantic import BaseModel, field_validator


class BusType(str, Enum):
    SATA = "sata"
    NVME = "nvme"
    USB = "usb"
    RAID = "raid"
    UNKNOWN = "unknown"


class MediaType(str, Enum):
    HDD = "hdd"
    SSD = "ssd"
    NVME = "nvme"
    UNKNOWN = "unknown"


class HealthStatus(str, Enum):
    HEALTHY = "healthy"
    WARNING = "warning"
    CRITICAL = "critical"
    UNKNOWN = "unknown"


class SelfTestType(str, Enum):
    SHORT = "short"
    EXTENDED = "extended"
    CONVEYANCE = "conveyance"


class SelfTestStatus(str, Enum):
    QUEUED = "queued"
    RUNNING = "running"
    PASSED = "passed"
    FAILED = "failed"
    ABORTED = "aborted"
    UNSUPPORTED = "unsupported"
    UNKNOWN = "unknown"


class TemperatureSource(str, Enum):
    NATIVE = "native"
    SMARTCTL = "smartctl"
    NONE = "none"


class HealthSource(str, Enum):
    NATIVE = "native"
    SMARTCTL = "smartctl"
    NONE = "none"


class AttributeStatus(str, Enum):
    OK = "ok"
    WARNING = "warning"
    CRITICAL = "critical"
    UNKNOWN = "unknown"


class AttributeSourceKind(str, Enum):
    ATA_SMART = "ata_smart"
    NVME_SMART = "nvme_smart"
    VENDOR = "vendor"
    DERIVED = "derived"


class DriveCapabilitySet(BaseModel):
    smart_read: bool = False
    smart_self_test_short: bool = False
    smart_self_test_extended: bool = False
    smart_self_test_conveyance: bool = False
    smart_self_test_abort: bool = False
    temperature_source: TemperatureSource = TemperatureSource.NONE
    health_source: HealthSource = HealthSource.NONE


class DriveRawAttribute(BaseModel):
    key: str
    name: str
    normalized_value: int | None = None
    worst_value: int | None = None
    threshold: int | None = None
    raw_value: str = ""
    status: AttributeStatus = AttributeStatus.UNKNOWN
    source_kind: AttributeSourceKind = AttributeSourceKind.ATA_SMART


class DriveSelfTestRun(BaseModel):
    id: str
    drive_id: str
    type: SelfTestType
    status: SelfTestStatus
    progress_percent: float | None = None
    started_at: str
    finished_at: str | None = None
    failure_message: str | None = None


class DriveSummary(BaseModel):
    """Returned in list responses — serial is masked."""
    id: str
    name: str
    model: str
    serial_masked: str
    device_path_masked: str
    bus_type: BusType
    media_type: MediaType
    capacity_bytes: int
    temperature_c: float | None = None
    health_status: HealthStatus
    health_percent: float | None = None
    smart_available: bool
    native_available: bool
    supports_self_test: bool
    supports_abort: bool
    last_updated_at: str


class DriveDetail(BaseModel):
    """Returned in detail responses — includes full serial and device path."""
    # All DriveSummary fields
    id: str
    name: str
    model: str
    serial_masked: str
    device_path_masked: str
    bus_type: BusType
    media_type: MediaType
    capacity_bytes: int
    temperature_c: float | None = None
    health_status: HealthStatus
    health_percent: float | None = None
    smart_available: bool
    native_available: bool
    supports_self_test: bool
    supports_abort: bool
    last_updated_at: str
    # Detail-only fields
    serial_full: str
    device_path: str
    firmware_version: str
    interface_speed: str | None = None
    rotation_rate_rpm: int | None = None
    power_on_hours: int | None = None
    power_cycle_count: int | None = None
    unsafe_shutdowns: int | None = None
    wear_percent_used: float | None = None
    available_spare_percent: float | None = None
    reallocated_sectors: int | None = None
    pending_sectors: int | None = None
    uncorrectable_errors: int | None = None
    media_errors: int | None = None
    predicted_failure: bool = False
    temperature_warning_c: float
    temperature_critical_c: float
    capabilities: DriveCapabilitySet
    warnings: list[str] = []
    last_self_test: DriveSelfTestRun | None = None
    raw_attributes: list[DriveRawAttribute] = []
    history_retention_hours_effective: int = 168


_SMARTCTL_PATH_RE = re.compile(r"^[a-zA-Z0-9_./ :\\-]{1,260}$")


class DriveSettings(BaseModel):
    """Global drive monitoring settings."""
    enabled: bool = True
    native_provider_enabled: bool = True
    smartctl_provider_enabled: bool = True
    smartctl_path: str = "smartctl"
    fast_poll_seconds: int = 15
    health_poll_seconds: int = 300
    rescan_poll_seconds: int = 900

    @field_validator("smartctl_path")
    @classmethod
    def _validate_smartctl_path(cls, v: str) -> str:
        if not _SMARTCTL_PATH_RE.match(v):
            raise ValueError("smartctl_path contains invalid characters")
        return v

    @field_validator("fast_poll_seconds", "health_poll_seconds", "rescan_poll_seconds")
    @classmethod
    def _validate_poll_seconds(cls, v: int) -> int:
        if v < 5:
            raise ValueError("Poll interval must be at least 5 seconds")
        return v
    hdd_temp_warning_c: float = 45.0
    hdd_temp_critical_c: float = 50.0
    ssd_temp_warning_c: float = 55.0
    ssd_temp_critical_c: float = 65.0
    nvme_temp_warning_c: float = 65.0
    nvme_temp_critical_c: float = 75.0
    wear_warning_percent_used: float = 80.0
    wear_critical_percent_used: float = 90.0


class DriveSettingsOverride(BaseModel):
    """Per-drive settings override (None means use global default)."""
    temp_warning_c: float | None = None
    temp_critical_c: float | None = None
    alerts_enabled: bool | None = None
    curve_picker_enabled: bool | None = None


class DriveRawData(BaseModel):
    """Internal model passed between provider and normalizer."""
    id: str
    name: str
    model: str
    serial: str
    device_path: str
    bus_type: BusType
    media_type: MediaType
    capacity_bytes: int
    firmware_version: str
    interface_speed: str | None = None
    rotation_rate_rpm: int | None = None
    temperature_c: float | None = None
    power_on_hours: int | None = None
    power_cycle_count: int | None = None
    unsafe_shutdowns: int | None = None
    wear_percent_used: float | None = None
    available_spare_percent: float | None = None
    reallocated_sectors: int | None = None
    pending_sectors: int | None = None
    uncorrectable_errors: int | None = None
    media_errors: int | None = None
    predicted_failure: bool = False
    smart_overall_health: str | None = None  # "PASSED"/"FAILED"
    nvme_critical_warning: int | None = None  # NVMe critical_warning bitmask
    capabilities: DriveCapabilitySet = DriveCapabilitySet()
    raw_attributes: list[DriveRawAttribute] = []
    raw_smartctl: dict[str, Any] | None = None  # full parsed JSON for debug
