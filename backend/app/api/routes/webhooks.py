from __future__ import annotations

from fastapi import APIRouter, Depends, Query, Request
from pydantic import BaseModel, Field, field_validator

from app.api.dependencies.auth import require_csrf
from app.config import settings
from app.services.webhook_service import _KEEP_SECRET
from app.utils.url_security import validate_outbound_url

router = APIRouter(prefix="/api/webhooks", tags=["webhooks"])


class UpdateWebhookRequest(BaseModel):
    enabled: bool = False
    target_url: str = ""
    signing_secret: str | None = Field(default=None, max_length=512)
    timeout_seconds: float = Field(default=3.0, ge=0.5, le=30.0)
    max_retries: int = Field(default=2, ge=0, le=10)
    retry_backoff_seconds: float = Field(default=1.0, ge=0.1, le=30.0)

    @field_validator("target_url")
    @classmethod
    def validate_target_url(cls, value: str) -> str:
        v = value.strip()
        if not v:
            return ""
        ok, reason = validate_outbound_url(
            v,
            allow_private=settings.allow_private_outbound_targets,
        )
        if not ok:
            raise ValueError(reason or "target_url is not allowed")
        return v


@router.get("")
async def get_webhook_config(request: Request):
    svc = request.app.state.webhook_service
    cfg = await svc.get_config()
    return {"config": cfg}


@router.put("", dependencies=[Depends(require_csrf)])
async def update_webhook_config(body: UpdateWebhookRequest, request: Request):
    svc = request.app.state.webhook_service
    secret_value: str | None | object
    if "signing_secret" in body.model_fields_set:
        secret_value = body.signing_secret
    else:
        secret_value = _KEEP_SECRET
    cfg = await svc.update_config(
        enabled=body.enabled,
        target_url=body.target_url,
        signing_secret=secret_value,
        timeout_seconds=body.timeout_seconds,
        max_retries=body.max_retries,
        retry_backoff_seconds=body.retry_backoff_seconds,
    )
    return {"success": True, "config": cfg}


@router.get("/deliveries")
async def get_webhook_deliveries(
    request: Request,
    limit: int = Query(default=100, ge=1, le=500),
    offset: int = Query(default=0, ge=0),
):
    svc = request.app.state.webhook_service
    log = await svc.get_delivery_log(limit=limit, offset=offset)
    return {"deliveries": log}
