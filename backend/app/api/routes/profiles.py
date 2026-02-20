import uuid

from fastapi import APIRouter, Request
from pydantic import BaseModel

from app.models.profiles import Profile, ProfilePreset, PRESET_CURVES
from app.models.fan_curves import FanCurve

router = APIRouter(prefix="/api/profiles", tags=["profiles"])

# In-memory store (would be SQLite in production)
_profiles: dict[str, Profile] = {}


def _init_default_profiles() -> None:
    """Create default preset profiles."""
    if _profiles:
        return

    for preset in [ProfilePreset.SILENT, ProfilePreset.BALANCED, ProfilePreset.PERFORMANCE, ProfilePreset.FULL_SPEED]:
        pid = str(uuid.uuid4())[:8]
        profile = Profile(
            id=pid,
            name=preset.value.replace("_", " ").title(),
            preset=preset,
            curves=[],
            is_active=(preset == ProfilePreset.BALANCED),
        )
        _profiles[pid] = profile


_init_default_profiles()


@router.get("")
async def get_profiles():
    """Get all profiles."""
    data = {"profiles": [p.model_dump() for p in _profiles.values()]}
    print(f"DEBUG: get_profiles returning: {data}")
    return data


@router.get("/{profile_id}")
async def get_profile(profile_id: str):
    """Get a specific profile."""
    profile = _profiles.get(profile_id)
    if not profile:
        return {"error": "Profile not found"}, 404
    return profile.model_dump()


class CreateProfileRequest(BaseModel):
    name: str
    preset: ProfilePreset = ProfilePreset.CUSTOM
    curves: list[FanCurve] = []


@router.post("")
async def create_profile(body: CreateProfileRequest):
    """Create a new profile."""
    pid = str(uuid.uuid4())[:8]
    profile = Profile(
        id=pid,
        name=body.name,
        preset=body.preset,
        curves=body.curves,
    )
    _profiles[pid] = profile
    return {"success": True, "profile": profile.model_dump()}


@router.put("/{profile_id}/activate")
async def activate_profile(profile_id: str, request: Request):
    """Activate a profile and apply its curves."""
    profile = _profiles.get(profile_id)
    if not profile:
        return {"error": "Profile not found"}

    # Deactivate all, activate this one
    for p in _profiles.values():
        p.is_active = False
    profile.is_active = True

    # Apply curves
    fan_service = request.app.state.fan_service

    if profile.preset != ProfilePreset.CUSTOM and profile.preset in PRESET_CURVES:
        # For presets, create curves for all fans using the preset points
        backend = request.app.state.backend
        fan_ids = await backend.get_fan_ids()
        sensor_service = request.app.state.sensor_service
        readings = sensor_service.latest

        # Find a primary temp sensor
        temp_sensor_id = "cpu_temp_0"
        for r in readings:
            if r.sensor_type.value == "cpu_temp":
                temp_sensor_id = r.id
                break

        preset_curves = []
        for fan_id in fan_ids:
            curve = FanCurve(
                id=f"{profile.id}_{fan_id}",
                name=f"{profile.name} - {fan_id}",
                sensor_id=temp_sensor_id,
                fan_id=fan_id,
                points=PRESET_CURVES[profile.preset],
            )
            preset_curves.append(curve)

        fan_service.set_curves(preset_curves)
    else:
        fan_service.set_curves(profile.curves)

    return {"success": True, "active_profile": profile.model_dump()}


@router.delete("/{profile_id}")
async def delete_profile(profile_id: str):
    """Delete a profile."""
    if profile_id in _profiles:
        del _profiles[profile_id]
    return {"success": True}


@router.get("/{profile_id}/preset-curves")
async def get_preset_curves(profile_id: str):
    """Get the preset curve points for a profile."""
    profile = _profiles.get(profile_id)
    if not profile or profile.preset not in PRESET_CURVES:
        return {"points": []}
    return {"points": [p.model_dump() for p in PRESET_CURVES[profile.preset]]}
