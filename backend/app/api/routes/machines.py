from __future__ import annotations

import re
from datetime import datetime, timezone

import httpx
from fastapi import APIRouter, Depends, HTTPException, Request
from pydantic import BaseModel, Field, field_validator

from app.api.dependencies.auth import require_csrf
from app.config import settings
from app.utils.url_security import validate_outbound_url

router = APIRouter(prefix="/api/machines", tags=["machines"])

# Allowlist for IDs forwarded to remote agents as path segments.
# Prevents path traversal (e.g. "../../admin") in proxied requests.
_SAFE_ID_RE = re.compile(r'^[a-zA-Z0-9_\-]{1,128}$')

# Sentinel: when api_key is NOT in the PUT body, keep the existing value.
_KEEP_KEY = object()


class CreateMachineRequest(BaseModel):
    name: str = Field(min_length=1, max_length=120)
    base_url: str = Field(min_length=1, max_length=255)
    api_key: str | None = Field(default=None, max_length=512)
    api_key_id: str | None = Field(default=None, max_length=64)
    enabled: bool = True
    poll_interval_seconds: float = Field(default=2.0, ge=0.5, le=30.0)
    timeout_ms: int = Field(default=1200, ge=200, le=15000)

    @field_validator("base_url")
    @classmethod
    def validate_base_url(cls, v: str) -> str:
        value = v.strip()
        ok, reason = validate_outbound_url(
            value,
            allow_private=settings.allow_private_outbound_targets,
        )
        if not ok:
            raise ValueError(reason or "base_url is not allowed")
        return value.rstrip("/")


class UpdateMachineRequest(BaseModel):
    name: str | None = Field(default=None, min_length=1, max_length=120)
    base_url: str | None = Field(default=None, min_length=1, max_length=255)
    api_key: str | None = Field(default=None, max_length=512)
    api_key_id: str | None = Field(default=None, max_length=64)
    enabled: bool | None = None
    poll_interval_seconds: float | None = Field(default=None, ge=0.5, le=30.0)
    timeout_ms: int | None = Field(default=None, ge=200, le=15000)

    @field_validator("base_url")
    @classmethod
    def validate_base_url(cls, v: str | None) -> str | None:
        if v is None:
            return None
        value = v.strip()
        ok, reason = validate_outbound_url(
            value,
            allow_private=settings.allow_private_outbound_targets,
        )
        if not ok:
            raise ValueError(reason or "base_url is not allowed")
        return value.rstrip("/")


def _iso_to_dt(value: str | None) -> datetime | None:
    if not value:
        return None
    dt = datetime.fromisoformat(value)
    if dt.tzinfo is None:
        dt = dt.replace(tzinfo=timezone.utc)
    return dt


def _decorate_machine(machine: dict, snapshot: dict | None) -> dict:
    now = datetime.now(timezone.utc)
    ts = None
    if snapshot and isinstance(snapshot.get("timestamp"), str):
        ts = _iso_to_dt(snapshot["timestamp"])
    if ts is None:
        ts = _iso_to_dt(machine.get("last_seen_at"))

    freshness_seconds: float | None = None
    if ts:
        freshness_seconds = max(0.0, (now - ts).total_seconds())

    effective_status = machine["status"]
    if effective_status == "online" and freshness_seconds is not None and freshness_seconds > 10:
        effective_status = "offline"

    public_machine = _public_machine(machine)
    return {
        **public_machine,
        "status": effective_status,
        "freshness_seconds": round(freshness_seconds, 2) if freshness_seconds is not None else None,
        "snapshot": snapshot,
    }


def _public_machine(machine: dict) -> dict:
    """Redact secrets from machine responses."""
    return {
        **{k: v for k, v in machine.items() if k != "api_key"},
        "has_api_key": bool(machine.get("api_key")),
    }


@router.get("")
async def list_machines(request: Request):
    repo = request.app.state.machine_repo
    monitor = request.app.state.machine_monitor_service
    rows = await repo.list_all()
    return {
        "machines": [
            _decorate_machine(m, monitor.get_snapshot(m["id"]))
            for m in rows
        ]
    }


@router.post("", dependencies=[Depends(require_csrf)])
async def create_machine(body: CreateMachineRequest, request: Request):
    repo = request.app.state.machine_repo
    machine = await repo.create(
        name=body.name,
        base_url=body.base_url,
        api_key=body.api_key,
        api_key_id=body.api_key_id,
        enabled=body.enabled,
        poll_interval_seconds=body.poll_interval_seconds,
        timeout_ms=body.timeout_ms,
    )
    return {"machine": _public_machine(machine)}


@router.put("/{machine_id}", dependencies=[Depends(require_csrf)])
async def update_machine(machine_id: str, body: UpdateMachineRequest, request: Request):
    repo = request.app.state.machine_repo
    # When api_key is not in the request body, pass the sentinel so the repo
    # keeps the existing value instead of NULLing it.
    api_key_value = body.api_key if "api_key" in body.model_fields_set else _KEEP_KEY
    api_key_id_value = body.api_key_id if "api_key_id" in body.model_fields_set else _KEEP_KEY
    machine = await repo.update(
        machine_id,
        name=body.name,
        base_url=body.base_url,
        api_key=api_key_value,
        api_key_id=api_key_id_value,
        enabled=body.enabled,
        poll_interval_seconds=body.poll_interval_seconds,
        timeout_ms=body.timeout_ms,
    )
    if machine is None:
        raise HTTPException(status_code=404, detail="Machine not found")
    return {"machine": _public_machine(machine)}


@router.delete("/{machine_id}", dependencies=[Depends(require_csrf)])
async def delete_machine(machine_id: str, request: Request):
    repo = request.app.state.machine_repo
    monitor = request.app.state.machine_monitor_service
    deleted = await repo.delete(machine_id)
    if not deleted:
        raise HTTPException(status_code=404, detail="Machine not found")
    monitor.forget_machine(machine_id)
    return {"success": True}


@router.get("/{machine_id}/snapshot")
async def get_machine_snapshot(machine_id: str, request: Request):
    repo = request.app.state.machine_repo
    monitor = request.app.state.machine_monitor_service
    machine = await repo.get(machine_id)
    if machine is None:
        raise HTTPException(status_code=404, detail="Machine not found")
    snapshot = monitor.get_snapshot(machine_id)
    if snapshot is None:
        raise HTTPException(status_code=404, detail="No snapshot available yet")
    return {"machine_id": machine_id, "snapshot": snapshot}


@router.post("/{machine_id}/verify", dependencies=[Depends(require_csrf)])
async def verify_machine(machine_id: str, request: Request):
    repo = request.app.state.machine_repo
    monitor = request.app.state.machine_monitor_service
    machine = await repo.get(machine_id)
    if machine is None:
        raise HTTPException(status_code=404, detail="Machine not found")
    result = await monitor.verify_machine(machine)
    return result


class RemoteFanSettingsRequest(BaseModel):
    min_speed_pct: float | None = Field(default=None, ge=0, le=100)
    zero_rpm_capable: bool | None = None


@router.get("/{machine_id}/state")
async def get_machine_state(machine_id: str, request: Request):
    """Fetch full remote state: profiles, fans, sensors."""
    repo = request.app.state.machine_repo
    monitor = request.app.state.machine_monitor_service
    machine = await repo.get(machine_id)
    if machine is None:
        raise HTTPException(status_code=404, detail="Machine not found")
    try:
        state = await monitor.get_remote_state(machine)
    except httpx.HTTPStatusError as exc:
        raise HTTPException(
            status_code=502,
            detail=f"Remote agent returned {exc.response.status_code}",
        ) from exc
    except RuntimeError as exc:
        raise HTTPException(status_code=502, detail=str(exc)) from exc
    await repo.update_last_command_at(machine_id, datetime.now(timezone.utc).isoformat())
    return {"state": state}


@router.post("/{machine_id}/profiles/{profile_id}/activate", dependencies=[Depends(require_csrf)])
async def activate_remote_profile(machine_id: str, profile_id: str, request: Request):
    """Activate a profile on a remote agent."""
    if not _SAFE_ID_RE.match(profile_id):
        raise HTTPException(status_code=400, detail="Invalid profile_id")
    repo = request.app.state.machine_repo
    monitor = request.app.state.machine_monitor_service
    machine = await repo.get(machine_id)
    if machine is None:
        raise HTTPException(status_code=404, detail="Machine not found")
    try:
        result = await monitor.send_command(
            machine,
            "POST",
            f"/api/profiles/{profile_id}/activate",
        )
    except httpx.HTTPStatusError as exc:
        raise HTTPException(
            status_code=502,
            detail=f"Remote agent returned {exc.response.status_code}",
        ) from exc
    except RuntimeError as exc:
        raise HTTPException(status_code=502, detail=str(exc)) from exc
    await repo.update_last_command_at(machine_id, datetime.now(timezone.utc).isoformat())
    return result


@router.post("/{machine_id}/fans/release", dependencies=[Depends(require_csrf)])
async def release_remote_fans(machine_id: str, request: Request):
    """Release fan control on a remote agent."""
    repo = request.app.state.machine_repo
    monitor = request.app.state.machine_monitor_service
    machine = await repo.get(machine_id)
    if machine is None:
        raise HTTPException(status_code=404, detail="Machine not found")
    try:
        result = await monitor.send_command(machine, "POST", "/api/fans/release")
    except httpx.HTTPStatusError as exc:
        raise HTTPException(
            status_code=502,
            detail=f"Remote agent returned {exc.response.status_code}",
        ) from exc
    except RuntimeError as exc:
        raise HTTPException(status_code=502, detail=str(exc)) from exc
    await repo.update_last_command_at(machine_id, datetime.now(timezone.utc).isoformat())
    return result


@router.put("/{machine_id}/fans/{fan_id}/settings", dependencies=[Depends(require_csrf)])
async def update_remote_fan_settings(
    machine_id: str, fan_id: str, body: RemoteFanSettingsRequest, request: Request
):
    """Update fan settings on a remote agent."""
    if not _SAFE_ID_RE.match(fan_id):
        raise HTTPException(status_code=400, detail="Invalid fan_id")
    repo = request.app.state.machine_repo
    monitor = request.app.state.machine_monitor_service
    machine = await repo.get(machine_id)
    if machine is None:
        raise HTTPException(status_code=404, detail="Machine not found")
    payload = {
        k: v
        for k, v in {
            "min_speed_pct": body.min_speed_pct,
            "zero_rpm_capable": body.zero_rpm_capable,
        }.items()
        if v is not None
    }
    try:
        result = await monitor.send_command(
            machine,
            "PUT",
            f"/api/fans/{fan_id}/settings",
            body=payload,
        )
    except httpx.HTTPStatusError as exc:
        raise HTTPException(
            status_code=502,
            detail=f"Remote agent returned {exc.response.status_code}",
        ) from exc
    except RuntimeError as exc:
        raise HTTPException(status_code=502, detail=str(exc)) from exc
    await repo.update_last_command_at(machine_id, datetime.now(timezone.utc).isoformat())
    return result
