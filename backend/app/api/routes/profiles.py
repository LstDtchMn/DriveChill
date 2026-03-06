from fastapi import APIRouter, Depends, Request, HTTPException
from pydantic import BaseModel, Field

from app.api.dependencies.auth import require_csrf

from app.models.profiles import ProfilePreset, PRESET_CURVES
from app.models.fan_curves import FanCurve

router = APIRouter(prefix="/api/profiles", tags=["profiles"])


@router.get("")
async def get_profiles(request: Request):
    """Get all profiles."""
    repo = request.app.state.profile_repo
    profiles = await repo.list_all()
    return {"profiles": [p.model_dump() for p in profiles]}


@router.get("/{profile_id}")
async def get_profile(profile_id: str, request: Request):
    """Get a specific profile."""
    repo = request.app.state.profile_repo
    profile = await repo.get(profile_id)
    if not profile:
        raise HTTPException(status_code=404, detail="Profile not found")
    return profile.model_dump()


class CreateProfileRequest(BaseModel):
    name: str = Field(min_length=1, max_length=200)
    preset: ProfilePreset = ProfilePreset.CUSTOM
    curves: list[FanCurve] = Field(default=[], max_length=50)


@router.post("", dependencies=[Depends(require_csrf)])
async def create_profile(body: CreateProfileRequest, request: Request):
    """Create a new profile."""
    repo = request.app.state.profile_repo
    profile = await repo.create(name=body.name, preset=body.preset, curves=body.curves)
    return {"success": True, "profile": profile.model_dump()}


@router.put("/{profile_id}/activate", dependencies=[Depends(require_csrf)])
async def activate_profile(profile_id: str, request: Request):
    """Activate a profile and apply its curves."""
    repo = request.app.state.profile_repo
    profile = await repo.get(profile_id)
    if not profile:
        raise HTTPException(status_code=404, detail="Profile not found")

    await repo.activate(profile_id)
    profile.is_active = True

    # Notify quiet hours that the user manually switched profiles
    if hasattr(request.app.state, "quiet_hours_service"):
        request.app.state.quiet_hours_service.notify_manual_override()

    # Update alert service's pre-alert profile so future alert-triggered
    # switches know which profile to revert to.
    if hasattr(request.app.state, "alert_service"):
        request.app.state.alert_service.set_pre_alert_profile(profile_id)

    # M-3: delegate to FanService.apply_profile() — single canonical
    # implementation instead of duplicating preset-expansion logic here.
    fan_service = request.app.state.fan_service
    await fan_service.apply_profile(profile)

    return {"success": True, "active_profile": profile.model_dump()}


@router.delete("/{profile_id}", dependencies=[Depends(require_csrf)])
async def delete_profile(profile_id: str, request: Request):
    """Delete a profile.

    H-4: returns 404 for unknown profiles, 409 if attempting to delete
    the currently active profile (which would leave fans uncontrolled on
    the next restart).
    """
    repo = request.app.state.profile_repo
    profile = await repo.get(profile_id)
    if not profile:
        raise HTTPException(status_code=404, detail="Profile not found")
    if profile.is_active:
        raise HTTPException(
            status_code=409,
            detail="Cannot delete the active profile. Activate a different profile first.",
        )
    await repo.delete(profile_id)
    return {"success": True}


@router.get("/{profile_id}/preset-curves")
async def get_preset_curves(profile_id: str, request: Request):
    """Get the preset curve points for a profile."""
    repo = request.app.state.profile_repo
    profile = await repo.get(profile_id)
    if not profile or profile.preset not in PRESET_CURVES:
        return {"points": []}
    return {"points": [p.model_dump() for p in PRESET_CURVES[profile.preset]]}


@router.get("/{profile_id}/export")
async def export_profile(profile_id: str, request: Request):
    """Export a profile (name, preset, curves) as a portable JSON object."""
    repo = request.app.state.profile_repo
    profile = await repo.get(profile_id)
    if not profile:
        raise HTTPException(status_code=404, detail="Profile not found")
    return {
        "export_version": 1,
        "profile": {
            "name": profile.name,
            "preset": profile.preset.value,
            "curves": [c.model_dump() for c in profile.curves],
        },
    }


class ImportProfileRequest(BaseModel):
    name: str | None = Field(default=None, max_length=200)
    preset: ProfilePreset = ProfilePreset.CUSTOM
    curves: list[FanCurve] = Field(default=[], max_length=50)


@router.post("/import", dependencies=[Depends(require_csrf)])
async def import_profile(body: ImportProfileRequest, request: Request):
    """Import a profile from JSON.  Accepts the 'profile' object from export."""
    import secrets
    repo = request.app.state.profile_repo
    name = body.name or f"Imported {secrets.token_hex(3)}"
    # Assign fresh IDs to curves so they don't collide with existing ones
    fresh_curves: list[FanCurve] = []
    for curve in body.curves:
        fresh_curves.append(curve.model_copy(update={"id": f"curve_{secrets.token_hex(6)}"}))
    profile = await repo.create(name=name, preset=body.preset, curves=fresh_curves)
    return {"success": True, "profile": profile.model_dump()}
