"""Notification settings and test endpoints (email and Web Push)."""

from __future__ import annotations

from fastapi import APIRouter, Depends, HTTPException, Request
from pydantic import BaseModel, Field

from app.api.dependencies.auth import require_csrf

router = APIRouter(prefix="/api/notifications", tags=["notifications"])


# ---------------------------------------------------------------------------
# Request schemas
# ---------------------------------------------------------------------------


class UpdateEmailSettingsRequest(BaseModel):
    enabled: bool | None = None
    smtp_host: str | None = Field(default=None, max_length=253)
    smtp_port: int | None = Field(default=None, ge=1, le=65535)
    smtp_username: str | None = Field(default=None, max_length=254)
    smtp_password: str | None = Field(default=None, max_length=512)  # write-only
    sender_address: str | None = Field(default=None, max_length=254)
    recipient_list: list[str] | None = None
    use_tls: bool | None = None
    use_ssl: bool | None = None


# ---------------------------------------------------------------------------
# Email endpoints
# ---------------------------------------------------------------------------


@router.get("/email")
async def get_email_settings(request: Request):
    """Return email notification settings (smtp_password is never returned)."""
    repo = request.app.state.email_notification_repo
    settings = await repo.get()
    return {"settings": settings}


@router.put("/email", dependencies=[Depends(require_csrf)])
async def update_email_settings(
    body: UpdateEmailSettingsRequest, request: Request
):
    """Update email notification settings (only supplied fields are changed)."""
    repo = request.app.state.email_notification_repo
    # Build a kwargs dict from only the fields that were explicitly set in the
    # request body (i.e. present in model_fields_set).
    updates = {
        field: getattr(body, field)
        for field in body.model_fields_set
    }
    updated = await repo.update(**updates)
    return {"success": True, "settings": updated}


@router.post("/email/test", dependencies=[Depends(require_csrf)])
async def test_email(request: Request):
    """Send a test email using the current configuration.

    Returns {"success": bool, "error": str | None}.
    """
    svc = request.app.state.email_notification_service
    try:
        ok = await svc.send_test()
        return {"success": ok, "error": None}
    except Exception as exc:  # noqa: BLE001
        return {"success": False, "error": str(exc)}


# ---------------------------------------------------------------------------
# Push subscription schemas
# ---------------------------------------------------------------------------


class CreatePushSubscriptionRequest(BaseModel):
    endpoint: str = Field(min_length=1, max_length=2048)
    p256dh: str = Field(min_length=1, max_length=512)
    auth: str = Field(min_length=1, max_length=256)
    user_agent: str | None = Field(default=None, max_length=512)


class TestPushRequest(BaseModel):
    subscription_id: str


def _redact_subscription(sub: dict) -> dict:
    """Return a subscription dict with sensitive key material removed."""
    d: dict = sub
    return {
        "id": d["id"],
        "endpoint": d["endpoint"],
        "user_agent": d["user_agent"],
        "created_at": d["created_at"],
        "last_used_at": d["last_used_at"],
    }


# ---------------------------------------------------------------------------
# Push subscription endpoints
# ---------------------------------------------------------------------------


@router.get("/push-subscriptions")
async def list_push_subscriptions(request: Request):
    """List all push subscriptions (key material redacted)."""
    repo = request.app.state.push_subscription_repo
    subscriptions = await repo.list_all()
    return {"subscriptions": [_redact_subscription(s) for s in subscriptions]}


@router.post("/push-subscriptions", dependencies=[Depends(require_csrf)])
async def create_push_subscription(
    body: CreatePushSubscriptionRequest, request: Request
):
    """Register a new push subscription. Returns existing record on duplicate endpoint."""
    repo = request.app.state.push_subscription_repo

    existing = await repo.get_by_endpoint(body.endpoint)
    if existing is not None:
        return {"success": True, "subscription": _redact_subscription(existing)}

    subscription = await repo.create(
        endpoint=body.endpoint,
        p256dh=body.p256dh,
        auth=body.auth,
        user_agent=body.user_agent,
    )
    if subscription is None:
        raise HTTPException(status_code=500, detail="Failed to create push subscription")
    return {"success": True, "subscription": _redact_subscription(subscription)}


@router.delete("/push-subscriptions/{subscription_id}", dependencies=[Depends(require_csrf)])
async def delete_push_subscription(subscription_id: str, request: Request):
    """Remove a push subscription."""
    repo = request.app.state.push_subscription_repo
    deleted = await repo.delete(subscription_id)
    if not deleted:
        raise HTTPException(status_code=404, detail="Subscription not found")
    return {"success": True}


@router.post("/push-subscriptions/test", dependencies=[Depends(require_csrf)])
async def test_push_subscription(body: TestPushRequest, request: Request):
    """Send a test push notification to a single subscription."""
    push_svc = request.app.state.push_notification_service
    ok = await push_svc.send_test(body.subscription_id)
    if not ok:
        raise HTTPException(
            status_code=502,
            detail="Test push delivery failed. Check server logs for details.",
        )
    return {"success": True}
