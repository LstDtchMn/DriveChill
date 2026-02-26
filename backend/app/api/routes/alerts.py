from fastapi import APIRouter, Depends, Request

from app.api.dependencies.auth import require_csrf

from app.services.alert_service import AlertRule

router = APIRouter(prefix="/api/alerts", tags=["alerts"])


@router.get("")
async def get_alerts(request: Request):
    """Get alert rules and recent events."""
    alert_service = request.app.state.alert_service
    return {
        "rules": [r.model_dump() for r in alert_service.rules],
        "events": [e.model_dump(mode="json") for e in alert_service.events[-50:]],
        "active": alert_service.active_alerts,
    }


@router.post("/rules", dependencies=[Depends(require_csrf)])
async def add_rule(rule: AlertRule, request: Request):
    """Add or update an alert rule."""
    alert_service = request.app.state.alert_service
    await alert_service.add_rule(rule)
    return {"success": True, "rule": rule.model_dump()}


@router.delete("/rules/{rule_id}", dependencies=[Depends(require_csrf)])
async def delete_rule(rule_id: str, request: Request):
    """Delete an alert rule."""
    alert_service = request.app.state.alert_service
    await alert_service.remove_rule(rule_id)
    return {"success": True}


@router.post("/clear", dependencies=[Depends(require_csrf)])
async def clear_events(request: Request):
    """Clear all alert events."""
    alert_service = request.app.state.alert_service
    alert_service.clear_events()
    return {"success": True}
