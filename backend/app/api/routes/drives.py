"""Drive monitoring API routes."""
from __future__ import annotations

import re
from typing import Any

from fastapi import APIRouter, Depends, HTTPException, Request
from pydantic import BaseModel, Field

from app.api.dependencies.auth import require_auth, require_csrf
from app.models.drives import DriveRawData, DriveSettings, DriveSettingsOverride, SelfTestType

router = APIRouter(prefix="/api/drives", tags=["drives"])

# Drive IDs are SHA-256 hex substrings — 24 hex chars
_DRIVE_ID_RE = re.compile(r"^[a-f0-9]{24}$")
# Run IDs are hex tokens — 16 hex chars
_RUN_ID_RE = re.compile(r"^[a-f0-9]{16}$")


def _mask_serial(serial: str) -> str:
    """Show only the last 4 characters."""
    if not serial:
        return "****"
    return "****" + serial[-4:]


def _mask_device_path(path: str) -> str:
    """Show only the last component (handles both / and \\ separators)."""
    import os.path
    return os.path.basename(path) or path


def _raw_to_summary(raw: DriveRawData, normalizer=None) -> dict[str, Any]:
    health = normalizer.health_status(raw).value if normalizer else "unknown"
    health_pct = normalizer.health_percent(raw) if normalizer else None
    return {
        "id": raw.id,
        "name": raw.name,
        "model": raw.model,
        "serial_masked": _mask_serial(raw.serial),
        "device_path_masked": _mask_device_path(raw.device_path),
        "bus_type": raw.bus_type.value,
        "media_type": raw.media_type.value,
        "capacity_bytes": raw.capacity_bytes,
        "temperature_c": raw.temperature_c,
        "health_status": health,
        "health_percent": health_pct,
        "smart_available": raw.capabilities.smart_read,
        "native_available": raw.capabilities.health_source.value != "none",
        "supports_self_test": raw.capabilities.smart_self_test_short,
        "supports_abort": raw.capabilities.smart_self_test_abort,
        "last_updated_at": None,
    }


def _raw_to_detail(raw: DriveRawData, normalizer=None, last_self_test=None) -> dict[str, Any]:
    base = _raw_to_summary(raw, normalizer)
    temp_warn = normalizer.temp_warning_c(raw) if normalizer else 45.0
    temp_crit = normalizer.temp_critical_c(raw) if normalizer else 50.0
    base.update({
        "serial_full": raw.serial,
        "device_path": raw.device_path,
        "firmware_version": raw.firmware_version,
        "interface_speed": raw.interface_speed,
        "rotation_rate_rpm": raw.rotation_rate_rpm,
        "power_on_hours": raw.power_on_hours,
        "power_cycle_count": raw.power_cycle_count,
        "unsafe_shutdowns": raw.unsafe_shutdowns,
        "wear_percent_used": raw.wear_percent_used,
        "available_spare_percent": raw.available_spare_percent,
        "reallocated_sectors": raw.reallocated_sectors,
        "pending_sectors": raw.pending_sectors,
        "uncorrectable_errors": raw.uncorrectable_errors,
        "media_errors": raw.media_errors,
        "predicted_failure": raw.predicted_failure,
        "temperature_warning_c": temp_warn,
        "temperature_critical_c": temp_crit,
        "capabilities": raw.capabilities.model_dump(),
        "warnings": [],
        "last_self_test": last_self_test,
        "raw_attributes": [a.model_dump() for a in raw.raw_attributes],
        "history_retention_hours_effective": 168,
    })
    return base


# ── Helper to get drive monitor service ─────────────────────────────────────

def _get_monitor(request: Request):
    monitor = getattr(request.app.state, "drive_monitor_service", None)
    if monitor is None:
        raise HTTPException(status_code=503, detail="Drive monitoring not initialized")
    return monitor


def _get_self_test_svc(request: Request):
    svc = getattr(request.app.state, "drive_self_test_service", None)
    if svc is None:
        raise HTTPException(status_code=503, detail="Drive monitoring not initialized")
    return svc


def _get_repo(request: Request):
    from app.db.repositories.drive_repo import DriveRepo
    return DriveRepo(request.app.state.db)


def _get_normalizer(request: Request):
    monitor = _get_monitor(request)
    return monitor._normalizer


def _validate_drive_id(drive_id: str) -> None:
    if not _DRIVE_ID_RE.match(drive_id):
        raise HTTPException(status_code=400, detail="Invalid drive_id")


def _validate_run_id(run_id: str) -> None:
    if not _RUN_ID_RE.match(run_id):
        raise HTTPException(status_code=400, detail="Invalid run_id")


# ── Endpoints ────────────────────────────────────────────────────────────────

@router.get("", dependencies=[Depends(require_auth)])
async def list_drives(request: Request):
    monitor = _get_monitor(request)
    normalizer = _get_normalizer(request)
    drives = monitor.get_all_drives()
    return {
        "drives": [_raw_to_summary(d, normalizer) for d in drives],
        "smartctl_available": await monitor.is_smartctl_available(),
        "total": len(drives),
    }


@router.post("/rescan", dependencies=[Depends(require_csrf)])
async def rescan_drives(request: Request):
    monitor = _get_monitor(request)
    count = await monitor.rescan_now()
    return {"drives_found": count}


@router.get("/settings", dependencies=[Depends(require_auth)])
async def get_drive_settings(request: Request):
    monitor = _get_monitor(request)
    s = await monitor.get_settings()
    return s.model_dump()


class DriveSettingsUpdate(BaseModel):
    enabled: bool | None = None
    smartctl_provider_enabled: bool | None = None
    native_provider_enabled: bool | None = None
    smartctl_path: str | None = None
    fast_poll_seconds: int | None = Field(default=None, ge=1)
    health_poll_seconds: int | None = Field(default=None, ge=1)
    rescan_poll_seconds: int | None = Field(default=None, ge=1)
    hdd_temp_warning_c: float | None = None
    hdd_temp_critical_c: float | None = None
    ssd_temp_warning_c: float | None = None
    ssd_temp_critical_c: float | None = None
    nvme_temp_warning_c: float | None = None
    nvme_temp_critical_c: float | None = None
    wear_warning_percent_used: float | None = None
    wear_critical_percent_used: float | None = None


@router.put("/settings", dependencies=[Depends(require_csrf)])
async def update_drive_settings(request: Request, body: DriveSettingsUpdate):
    repo = request.app.state.settings_repo
    field_map = {
        "enabled": "drive_monitoring_enabled",
        "smartctl_provider_enabled": "drive_smartctl_provider_enabled",
        "native_provider_enabled": "drive_native_provider_enabled",
        "smartctl_path": "drive_smartctl_path",
        "fast_poll_seconds": "drive_fast_poll_seconds",
        "health_poll_seconds": "drive_health_poll_seconds",
        "rescan_poll_seconds": "drive_rescan_poll_seconds",
        "hdd_temp_warning_c": "drive_hdd_temp_warning_c",
        "hdd_temp_critical_c": "drive_hdd_temp_critical_c",
        "ssd_temp_warning_c": "drive_ssd_temp_warning_c",
        "ssd_temp_critical_c": "drive_ssd_temp_critical_c",
        "nvme_temp_warning_c": "drive_nvme_temp_warning_c",
        "nvme_temp_critical_c": "drive_nvme_temp_critical_c",
        "wear_warning_percent_used": "drive_wear_warning_percent_used",
        "wear_critical_percent_used": "drive_wear_critical_percent_used",
    }
    for field, key in field_map.items():
        val = getattr(body, field, None)
        if val is not None:
            await repo.set(key, str(int(val) if isinstance(val, bool) else val))
    monitor = _get_monitor(request)
    return (await monitor.get_settings()).model_dump()


@router.get("/{drive_id}", dependencies=[Depends(require_auth)])
async def get_drive(drive_id: str, request: Request):
    _validate_drive_id(drive_id)
    monitor = _get_monitor(request)
    raw = monitor.get_drive(drive_id)
    if raw is None:
        raise HTTPException(status_code=404, detail="Drive not found")
    normalizer = _get_normalizer(request)
    repo = _get_repo(request)
    runs = await repo.get_self_test_runs(drive_id, limit=1)
    last_run = runs[0] if runs else None
    return _raw_to_detail(raw, normalizer, last_self_test=last_run)


@router.get("/{drive_id}/attributes", dependencies=[Depends(require_auth)])
async def get_drive_attributes(drive_id: str, request: Request):
    _validate_drive_id(drive_id)
    repo = _get_repo(request)
    attrs = await repo.get_attributes(drive_id)
    return {"drive_id": drive_id, "attributes": attrs}


@router.get("/{drive_id}/history", dependencies=[Depends(require_auth)])
async def get_drive_history(
    drive_id: str,
    request: Request,
    hours: float = 168.0,
):
    _validate_drive_id(drive_id)
    if hours <= 0 or hours > 8760:
        raise HTTPException(status_code=400, detail="hours must be between 0 and 8760")
    # Cap to the retention setting
    retention = await request.app.state.settings_repo.get_int("history_retention_hours", 168)
    effective_hours = min(hours, float(retention))
    repo = _get_repo(request)
    history = await repo.get_health_history(drive_id, hours=effective_hours)
    return {
        "drive_id": drive_id,
        "history": history,
        "retention_limited": effective_hours < hours,
    }


@router.post("/{drive_id}/refresh", dependencies=[Depends(require_csrf)])
async def refresh_drive(drive_id: str, request: Request):
    _validate_drive_id(drive_id)
    monitor = _get_monitor(request)
    raw = monitor.get_drive(drive_id)
    if raw is None:
        raise HTTPException(status_code=404, detail="Drive not found")
    refreshed = await monitor.refresh_drive(drive_id)
    normalizer = _get_normalizer(request)
    return _raw_to_summary(refreshed or raw, normalizer)


# ── Self-test endpoints ──────────────────────────────────────────────────────

class StartSelfTestRequest(BaseModel):
    type: SelfTestType


@router.post("/{drive_id}/self-tests", dependencies=[Depends(require_csrf)])
async def start_self_test(drive_id: str, request: Request, body: StartSelfTestRequest):
    _validate_drive_id(drive_id)
    svc = _get_self_test_svc(request)
    try:
        run = await svc.start_test(drive_id, body.type)
    except ValueError as exc:
        raise HTTPException(status_code=400, detail=str(exc)) from exc
    return run


@router.get("/{drive_id}/self-tests", dependencies=[Depends(require_auth)])
async def list_self_tests(drive_id: str, request: Request):
    _validate_drive_id(drive_id)
    repo = _get_repo(request)
    runs = await repo.get_self_test_runs(drive_id, limit=10)
    return {"drive_id": drive_id, "runs": runs}


@router.post("/{drive_id}/self-tests/{run_id}/abort", dependencies=[Depends(require_csrf)])
async def abort_self_test(drive_id: str, run_id: str, request: Request):
    _validate_drive_id(drive_id)
    _validate_run_id(run_id)
    svc = _get_self_test_svc(request)
    aborted = await svc.abort_test(drive_id, run_id)
    if not aborted:
        raise HTTPException(status_code=409, detail="Abort failed or not supported")
    return {"success": True}


# ── Per-drive settings ───────────────────────────────────────────────────────

@router.get("/{drive_id}/settings", dependencies=[Depends(require_auth)])
async def get_per_drive_settings(drive_id: str, request: Request):
    _validate_drive_id(drive_id)
    repo = _get_repo(request)
    override = await repo.get_drive_settings_override(drive_id)
    return override or {
        "drive_id": drive_id,
        "temp_warning_c": None,
        "temp_critical_c": None,
        "alerts_enabled": None,
        "curve_picker_enabled": None,
    }


@router.put("/{drive_id}/settings", dependencies=[Depends(require_csrf)])
async def update_per_drive_settings(
    drive_id: str,
    request: Request,
    body: DriveSettingsOverride,
):
    _validate_drive_id(drive_id)
    monitor = _get_monitor(request)
    if monitor.get_drive(drive_id) is None:
        raise HTTPException(status_code=404, detail="Drive not found")
    repo = _get_repo(request)
    await repo.upsert_drive_settings_override(drive_id, body)
    return await repo.get_drive_settings_override(drive_id)
