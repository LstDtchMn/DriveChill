"""API routes for temperature targets (drive temp → fan control)."""

from __future__ import annotations

import re

from fastapi import APIRouter, Depends, HTTPException, Request

from app.api.dependencies.auth import require_auth, require_csrf
from app.db.repositories.temperature_target_repo import TemperatureTargetRepo
from app.models.temperature_targets import (
    TemperatureTarget,
    TemperatureTargetCreate,
    TemperatureTargetToggle,
    TemperatureTargetUpdate,
)

router = APIRouter(prefix="/api/temperature-targets", tags=["temperature-targets"])

_HDD_TEMP_RE = re.compile(r"^hdd_temp_")


def _get_service(request: Request):
    return request.app.state.temperature_target_service


def _get_sensor_ids(request: Request) -> set[str]:
    """Return the set of currently known sensor IDs."""
    sensor_svc = getattr(request.app.state, "sensor_service", None)
    if sensor_svc is None:
        return set()
    return {r.id for r in sensor_svc.latest}


def _get_controllable_fan_ids(request: Request) -> set[str]:
    """Return the set of fan IDs the backend can control."""
    fan_svc = getattr(request.app.state, "fan_service", None)
    if fan_svc is None:
        return set()
    # Fan IDs are the union of curve fan_ids + any fans the backend knows about.
    # Use last_applied_speeds keys as the known controllable set; if empty,
    # fall back to fan readings from sensor service.
    fan_ids = set(fan_svc.last_applied_speeds.keys())
    if not fan_ids:
        sensor_svc = getattr(request.app.state, "sensor_service", None)
        if sensor_svc:
            fan_ids = {r.id for r in sensor_svc.latest if r.sensor_type.value == "fan_rpm"}
    return fan_ids


def _validate_sensor_id(sensor_id: str, request: Request, *, is_new: bool = True) -> None:
    """Validate sensor_id format and existence."""
    if not _HDD_TEMP_RE.match(sensor_id):
        raise HTTPException(status_code=422, detail="sensor_id must match hdd_temp_* pattern")
    if is_new:
        known = _get_sensor_ids(request)
        if known and sensor_id not in known:
            raise HTTPException(
                status_code=422,
                detail=f"sensor not found: {sensor_id} — drive may be offline or not yet detected",
            )


def _validate_fan_ids(fan_ids: list[str], request: Request) -> None:
    """Validate fan_ids is non-empty and all IDs exist."""
    if not fan_ids:
        raise HTTPException(status_code=422, detail="fan_ids must not be empty")
    known = _get_controllable_fan_ids(request)
    if known:
        for fid in fan_ids:
            if fid not in known:
                raise HTTPException(status_code=422, detail=f"fan not found: {fid}")


@router.get("", dependencies=[Depends(require_auth)])
async def list_targets(request: Request):
    svc = _get_service(request)
    targets = svc.targets
    return {"targets": [t.model_dump() for t in targets]}


@router.post("", status_code=201, dependencies=[Depends(require_auth), Depends(require_csrf)])
async def create_target(request: Request, body: TemperatureTargetCreate):
    _validate_sensor_id(body.sensor_id, request, is_new=True)
    _validate_fan_ids(body.fan_ids, request)

    svc = _get_service(request)
    target = TemperatureTarget(
        id=TemperatureTargetRepo.generate_id(),
        name=body.name,
        drive_id=body.drive_id,
        sensor_id=body.sensor_id,
        fan_ids=body.fan_ids,
        target_temp_c=body.target_temp_c,
        tolerance_c=body.tolerance_c,
        min_fan_speed=body.min_fan_speed,
        pid_mode=body.pid_mode,
        pid_kp=body.pid_kp,
        pid_ki=body.pid_ki,
        pid_kd=body.pid_kd,
    )
    created = await svc.add(target)
    return created.model_dump()


@router.get("/{target_id}", dependencies=[Depends(require_auth)])
async def get_target(request: Request, target_id: str):
    svc = _get_service(request)
    targets = svc.targets
    for t in targets:
        if t.id == target_id:
            return t.model_dump()
    raise HTTPException(status_code=404, detail="Not found")


@router.put("/{target_id}", dependencies=[Depends(require_auth), Depends(require_csrf)])
async def update_target(request: Request, target_id: str, body: TemperatureTargetUpdate):
    svc = _get_service(request)
    # Check if sensor_id changed — only validate existence if it did
    existing = next((t for t in svc.targets if t.id == target_id), None)
    if existing is None:
        raise HTTPException(status_code=404, detail="Not found")

    sensor_changed = body.sensor_id != existing.sensor_id
    _validate_sensor_id(body.sensor_id, request, is_new=sensor_changed)
    _validate_fan_ids(body.fan_ids, request)

    updated = await svc.update(
        target_id,
        name=body.name,
        drive_id=body.drive_id,
        sensor_id=body.sensor_id,
        fan_ids=body.fan_ids,
        target_temp_c=body.target_temp_c,
        tolerance_c=body.tolerance_c,
        min_fan_speed=body.min_fan_speed,
        pid_mode=body.pid_mode,
        pid_kp=body.pid_kp,
        pid_ki=body.pid_ki,
        pid_kd=body.pid_kd,
    )
    if updated is None:
        raise HTTPException(status_code=404, detail="Not found")
    return updated.model_dump()


@router.delete("/{target_id}", dependencies=[Depends(require_auth), Depends(require_csrf)])
async def delete_target(request: Request, target_id: str):
    svc = _get_service(request)
    deleted = await svc.remove(target_id)
    if not deleted:
        raise HTTPException(status_code=404, detail="Not found")
    return {"success": True}


@router.patch("/{target_id}/enabled", dependencies=[Depends(require_auth), Depends(require_csrf)])
async def toggle_target(request: Request, target_id: str, body: TemperatureTargetToggle):
    svc = _get_service(request)
    updated = await svc.set_enabled(target_id, body.enabled)
    if updated is None:
        raise HTTPException(status_code=404, detail="Not found")
    return updated.model_dump()
