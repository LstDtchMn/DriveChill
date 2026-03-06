from datetime import datetime, timezone

from fastapi import APIRouter, Depends, HTTPException, Request

from app.api.dependencies.auth import require_csrf
from pydantic import BaseModel, Field

from app.models.fan_curves import FanCurve, FanCurvePoint
from app.services.curve_engine import check_dangerous_curve
from app.services.fan_test_service import FanTestOptions

router = APIRouter(prefix="/api/fans", tags=["fans"])


class SetSpeedRequest(BaseModel):
    fan_id: str
    speed: float = Field(ge=0.0, le=100.0, description="Fan speed 0–100 %")


@router.get("")
async def get_fans(request: Request):
    """Get list of controllable fans."""
    backend = request.app.state.backend
    fan_ids = await backend.get_fan_ids()
    return {"fans": fan_ids}


@router.post("/speed", dependencies=[Depends(require_csrf)])
async def set_fan_speed(body: SetSpeedRequest, request: Request):
    """Manually set a fan speed."""
    backend = request.app.state.backend
    success = await backend.set_fan_speed(body.fan_id, body.speed)
    return {"success": success, "fan_id": body.fan_id, "speed": body.speed}


@router.get("/curves")
async def get_curves(request: Request):
    """Get all fan curves."""
    fan_service = request.app.state.fan_service
    return {"curves": [c.model_dump() for c in fan_service.curves]}


class UpdateCurveRequest(BaseModel):
    curve: FanCurve
    allow_dangerous: bool = False


@router.put("/curves", dependencies=[Depends(require_csrf)])
async def update_curve(body: UpdateCurveRequest, request: Request):
    """Update or create a fan curve.

    If the curve has dangerously low fan speeds at high temperatures,
    the request is rejected with 409 unless ``allow_dangerous`` is true.
    Overrides are logged to the auth_log audit table.
    """
    warnings = check_dangerous_curve(body.curve.points)

    if warnings and not body.allow_dangerous:
        raise HTTPException(
            status_code=409,
            detail={
                "message": "Curve has dangerous speed settings at high temperatures. "
                           "Set allow_dangerous=true to override.",
                "warnings": warnings,
            },
        )

    fan_service = request.app.state.fan_service
    fan_service.update_curve(body.curve)

    # M-8: write the audit entry BEFORE persisting curves so that if the
    # process crashes between the two commits the override is still logged.
    if warnings and body.allow_dangerous:
        db = request.app.state.db
        await db.execute(
            "INSERT INTO auth_log (timestamp, event_type, ip_address, username, outcome, detail) "
            "VALUES (?, ?, ?, ?, ?, ?)",
            (
                datetime.now(timezone.utc).isoformat(),
                "dangerous_curve_override",
                request.client.host if request.client else "unknown",
                "user",
                "allowed",
                f"Curve {body.curve.id}: {len(warnings)} warning(s) overridden",
            ),
        )
        await db.commit()

    # Persist to the active profile in the database
    profile_repo = request.app.state.profile_repo
    active_profile = await profile_repo.get_active()
    if active_profile:
        await profile_repo.set_curves(active_profile.id, fan_service.curves)

    resp: dict = {"success": True, "curve": body.curve.model_dump()}
    if warnings:
        resp["dangerous_curve_warnings"] = warnings
        resp["override_logged"] = True
    return resp


@router.delete("/curves/{curve_id}", dependencies=[Depends(require_csrf)])
async def delete_curve(curve_id: str, request: Request):
    """Delete a fan curve."""
    fan_service = request.app.state.fan_service
    fan_service.remove_curve(curve_id)

    # Persist to the active profile in the database
    profile_repo = request.app.state.profile_repo
    active_profile = await profile_repo.get_active()
    if active_profile:
        await profile_repo.set_curves(active_profile.id, fan_service.curves)

    return {"success": True}


class ValidateCurveRequest(BaseModel):
    points: list[FanCurvePoint]


@router.post("/curves/validate", dependencies=[Depends(require_csrf)])
async def validate_curve(body: ValidateCurveRequest):
    """Pre-check curve points for dangerous configurations.

    Returns warnings without applying anything.  The frontend calls
    this before saving so it can show a confirmation dialog.
    """
    warnings = check_dangerous_curve(body.points)
    return {"safe": len(warnings) == 0, "warnings": warnings}


class UpdateFanSettingsRequest(BaseModel):
    min_speed_pct: float = Field(ge=0.0, le=100.0, default=0.0)
    zero_rpm_capable: bool = False


@router.get("/settings")
async def get_all_fan_settings(request: Request):
    """Get per-fan settings (min speed floor, zero-RPM capability) for all fans."""
    fan_settings_repo = request.app.state.fan_settings_repo
    all_settings = await fan_settings_repo.get_all()
    return {"fan_settings": all_settings}


@router.get("/{fan_id}/settings")
async def get_fan_settings(fan_id: str, request: Request):
    """Get per-fan settings (min speed floor, zero-RPM capability)."""
    fan_settings_repo = request.app.state.fan_settings_repo
    fs = await fan_settings_repo.get(fan_id)
    if fs is None:
        return {"fan_id": fan_id, "min_speed_pct": 0, "zero_rpm_capable": False}
    return {"fan_id": fan_id, **fs}


@router.put("/{fan_id}/settings", dependencies=[Depends(require_csrf)])
async def update_fan_settings(fan_id: str, body: UpdateFanSettingsRequest,
                              request: Request):
    """Update per-fan settings (min speed floor, zero-RPM capability)."""
    backend = request.app.state.backend
    fan_ids = await backend.get_fan_ids()
    if fan_id not in fan_ids:
        raise HTTPException(status_code=404, detail=f"Fan '{fan_id}' not found")
    fan_settings_repo = request.app.state.fan_settings_repo
    await fan_settings_repo.set(fan_id, body.min_speed_pct, body.zero_rpm_capable)
    fan_service = request.app.state.fan_service
    fan_service.update_fan_settings(fan_id, body.min_speed_pct, body.zero_rpm_capable)
    return {"success": True, "fan_id": fan_id,
            "min_speed_pct": body.min_speed_pct,
            "zero_rpm_capable": body.zero_rpm_capable}


@router.post("/{fan_id}/test", status_code=202, dependencies=[Depends(require_csrf)])
async def start_fan_test(fan_id: str, request: Request, body: FanTestOptions | None = None):
    """Start a benchmark sweep for one fan."""
    fan_test_service = request.app.state.fan_test_service
    options = body or FanTestOptions()
    ok, error = await fan_test_service.try_start(fan_id, options)
    if not ok:
        if "not found" in error.lower():
            raise HTTPException(status_code=404, detail=error)
        raise HTTPException(status_code=409, detail=error)

    estimated_seconds = (options.steps + 1) * (options.settle_ms / 1000.0)
    return {
        "ok": True,
        "fan_id": fan_id,
        "estimated_duration_s": estimated_seconds,
    }


@router.get("/{fan_id}/test")
async def get_fan_test_result(fan_id: str, request: Request):
    """Get current/last benchmark result for a fan."""
    fan_test_service = request.app.state.fan_test_service
    result = fan_test_service.get_result(fan_id)
    if result is None:
        raise HTTPException(status_code=404, detail=f"No test result for fan '{fan_id}'")
    return result.model_dump(mode="json")


@router.delete("/{fan_id}/test", dependencies=[Depends(require_csrf)])
async def cancel_fan_test(fan_id: str, request: Request):
    """Cancel a running fan benchmark."""
    fan_test_service = request.app.state.fan_test_service
    if not fan_test_service.cancel(fan_id):
        raise HTTPException(status_code=404, detail=f"No running test for fan '{fan_id}'")
    return {"ok": True}


@router.get("/status")
async def get_fan_status(request: Request):
    """Get current fan control status including safe-mode state and control sources."""
    fan_service = request.app.state.fan_service
    return {
        "safe_mode": fan_service.safe_mode_status,
        "curves_active": len(fan_service.curves),
        "applied_speeds": fan_service.last_applied_speeds,
        "control_sources": fan_service.control_sources,
        "startup_safety_active": fan_service.startup_safety_active,
    }


@router.post("/release", dependencies=[Depends(require_csrf)])
async def release_fan_control(request: Request):
    """Release all fans to BIOS/firmware automatic control.

    Sets all fans to auto mode and suspends software curve control until
    the user explicitly activates a profile.  One click, no confirmation.
    """
    fan_service = request.app.state.fan_service
    await fan_service.release_fan_control()
    # Notify quiet hours service that the user made a manual override.
    if hasattr(request.app.state, "quiet_hours_service"):
        request.app.state.quiet_hours_service.notify_manual_override()
    return {"success": True, "message": "Fan control released to BIOS/auto mode"}


@router.post("/resume", dependencies=[Depends(require_csrf)])
async def resume_fan_control(request: Request):
    """Resume software fan control by reactivating the current active profile.

    If no profile is active, returns a 409 telling the user to activate one.
    """
    profile_repo = request.app.state.profile_repo
    active_profile = await profile_repo.get_active()
    if not active_profile:
        raise HTTPException(
            status_code=409,
            detail="No active profile to resume. Activate a profile first.",
        )
    fan_service = request.app.state.fan_service
    await fan_service.apply_profile(active_profile)
    return {"success": True, "active_profile": active_profile.model_dump()}
