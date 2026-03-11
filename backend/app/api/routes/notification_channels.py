"""API routes for notification channels (ntfy, Discord, Slack, generic webhook)."""
from __future__ import annotations

import secrets as stdlib_secrets

from fastapi import APIRouter, Depends, HTTPException, Request
from pydantic import BaseModel, Field

from app.api.dependencies.auth import require_auth, require_csrf
from app.services.notification_channel_service import VALID_CHANNEL_TYPES
from app.utils.url_security import validate_outbound_url_async

router = APIRouter(prefix="/api/notification-channels", tags=["notification-channels"])

# Config keys that contain URLs requiring full SSRF validation
_URL_CONFIG_KEYS = ("url", "webhook_url", "broker_url")


async def _validate_config_urls(config: dict) -> None:
    """Reject config URLs that are unsafe outbound targets (SSRF mitigation).

    Uses the shared url_security validator which blocks loopback, link-local,
    private RFC1918 ranges, and non-http/https schemes.

    For broker_url (MQTT), the mqtt:// or mqtts:// scheme is rewritten to
    http:// before validation so the hostname/IP check still applies.
    """
    import re

    for key in _URL_CONFIG_KEYS:
        val = config.get(key)
        if val and isinstance(val, str):
            check_val = val
            if key == "broker_url":
                # Rewrite mqtt(s)://  →  http:// so the SSRF validator
                # can parse and resolve the hostname.
                check_val = re.sub(r"^mqtts?://", "http://", val, count=1)
                check_val = re.sub(r"^ssl://", "http://", check_val, count=1)
            ok, reason = await validate_outbound_url_async(check_val)
            if not ok:
                raise HTTPException(
                    status_code=400,
                    detail=f"Config '{key}': {reason}",
                )


class CreateChannelBody(BaseModel):
    type: str
    name: str = Field(min_length=1, max_length=200)
    enabled: bool = True
    config: dict = {}


class UpdateChannelBody(BaseModel):
    name: str | None = Field(default=None, max_length=200)
    enabled: bool | None = None
    config: dict | None = None


@router.get("", dependencies=[Depends(require_auth)])
async def list_channels(request: Request):
    svc = request.app.state.notification_channel_service
    channels = await svc.list_channels()
    return {"channels": [ch.to_dict() for ch in channels]}


@router.get("/{channel_id}", dependencies=[Depends(require_auth)])
async def get_channel(channel_id: str, request: Request):
    svc = request.app.state.notification_channel_service
    ch = await svc.get_channel(channel_id)
    if ch is None:
        raise HTTPException(status_code=404, detail="Channel not found")
    return ch.to_dict()


@router.post("", dependencies=[Depends(require_csrf)])
async def create_channel(body: CreateChannelBody, request: Request):
    if body.type not in VALID_CHANNEL_TYPES:
        raise HTTPException(status_code=400, detail=f"Invalid type. Must be one of: {', '.join(sorted(VALID_CHANNEL_TYPES))}")
    await _validate_config_urls(body.config)
    svc = request.app.state.notification_channel_service
    channel_id = f"nc_{stdlib_secrets.token_hex(8)}"
    ch = await svc.create_channel(channel_id, body.type, body.name, body.enabled, body.config)
    return {"success": True, "channel": ch.to_dict()}


@router.put("/{channel_id}", dependencies=[Depends(require_csrf)])
async def update_channel(channel_id: str, body: UpdateChannelBody, request: Request):
    if body.config is not None:
        await _validate_config_urls(body.config)  # type: ignore[arg-type]
    svc = request.app.state.notification_channel_service
    ok = await svc.update_channel(channel_id, name=body.name, enabled=body.enabled, config=body.config)
    if not ok:
        raise HTTPException(status_code=404, detail="Channel not found")
    ch = await svc.get_channel(channel_id)
    return {"success": True, "channel": ch.to_dict() if ch else None}


@router.delete("/{channel_id}", dependencies=[Depends(require_csrf)])
async def delete_channel(channel_id: str, request: Request):
    svc = request.app.state.notification_channel_service
    ok = await svc.delete_channel(channel_id)
    if not ok:
        raise HTTPException(status_code=404, detail="Channel not found")
    return {"success": True}


@router.post("/{channel_id}/test", dependencies=[Depends(require_csrf)])
async def test_channel(channel_id: str, request: Request):
    svc = request.app.state.notification_channel_service
    success, error = await svc.send_test(channel_id)
    return {"success": success, "error": error}
