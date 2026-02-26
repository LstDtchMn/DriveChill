from datetime import datetime, timezone

from fastapi import APIRouter, HTTPException, Request
from pydantic import BaseModel, Field

from app.models.fan_curves import FanCurve, FanCurvePoint
from app.services.curve_engine import check_dangerous_curve

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


@router.post("/speed")
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


@router.put("/curves")
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


@router.delete("/curves/{curve_id}")
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


@router.post("/curves/validate")
async def validate_curve(body: ValidateCurveRequest):
    """Pre-check curve points for dangerous configurations.

    Returns warnings without applying anything.  The frontend calls
    this before saving so it can show a confirmation dialog.
    """
    warnings = check_dangerous_curve(body.points)
    return {"safe": len(warnings) == 0, "warnings": warnings}


@router.get("/status")
async def get_fan_status(request: Request):
    """Get current fan control status including safe-mode state."""
    fan_service = request.app.state.fan_service
    return {
        "safe_mode": fan_service.safe_mode_status,
        "curves_active": len(fan_service.curves),
        "applied_speeds": fan_service.last_applied_speeds,
    }


@router.post("/release")
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


@router.post("/resume")
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
