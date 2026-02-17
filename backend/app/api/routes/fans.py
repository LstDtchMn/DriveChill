from fastapi import APIRouter, Request
from pydantic import BaseModel

from app.models.fan_curves import FanCurve

router = APIRouter(prefix="/api/fans", tags=["fans"])


class SetSpeedRequest(BaseModel):
    fan_id: str
    speed: float  # 0-100


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


@router.put("/curves")
async def update_curve(curve: FanCurve, request: Request):
    """Update or create a fan curve."""
    fan_service = request.app.state.fan_service
    fan_service.update_curve(curve)
    return {"success": True, "curve": curve.model_dump()}


@router.delete("/curves/{curve_id}")
async def delete_curve(curve_id: str, request: Request):
    """Delete a fan curve."""
    fan_service = request.app.state.fan_service
    fan_service.remove_curve(curve_id)
    return {"success": True}
