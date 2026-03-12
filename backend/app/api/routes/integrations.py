"""Integration health endpoint — surfaces the status of all notification integrations."""

from __future__ import annotations

from fastapi import APIRouter, Request

router = APIRouter(prefix="/api/integrations", tags=["integrations"])


@router.get("/health")
async def get_integration_health(request: Request):
    """Return aggregated health status for webhooks, email, push, and notification channels."""

    # ── Webhooks ──────────────────────────────────────────────────────
    webhook_svc = getattr(request.app.state, "webhook_service", None)
    if webhook_svc is not None:
        wh_cfg = await webhook_svc.get_config()
        wh_deliveries = await webhook_svc.get_delivery_log(limit=50)
        recent_failures = sum(1 for d in wh_deliveries if not d.get("success", True))
        last_delivery = wh_deliveries[0] if wh_deliveries else None
        webhooks_status = {
            "enabled": wh_cfg.get("enabled", False),
            "target_url": wh_cfg.get("target_url", ""),
            "last_delivery_at": last_delivery.get("timestamp") if last_delivery else None,
            "recent_failures": recent_failures,
            "last_error": getattr(webhook_svc, "last_error", None),
            "success_count": getattr(webhook_svc, "success_count", 0),
            "failure_count": getattr(webhook_svc, "failure_count", 0),
        }
    else:
        webhooks_status = {
            "enabled": False,
            "target_url": None,
            "last_delivery_at": None,
            "recent_failures": 0,
            "last_error": None,
            "success_count": 0,
            "failure_count": 0,
        }

    # ── Email ─────────────────────────────────────────────────────────
    email_svc = getattr(request.app.state, "email_notification_service", None)
    if email_svc is not None:
        email_status = {
            "configured": getattr(email_svc, "is_configured", False),
            "last_sent_at": _fmt_dt(getattr(email_svc, "last_sent_at", None)),
            "last_error": getattr(email_svc, "last_error", None),
            "success_count": getattr(email_svc, "success_count", 0),
            "failure_count": getattr(email_svc, "failure_count", 0),
        }
    else:
        email_status = {
            "configured": False,
            "last_sent_at": None,
            "last_error": None,
            "success_count": 0,
            "failure_count": 0,
        }

    # ── Push ──────────────────────────────────────────────────────────
    push_svc = getattr(request.app.state, "push_notification_service", None)
    if push_svc is not None:
        sub_count = 0
        try:
            if hasattr(push_svc, "get_subscription_count"):
                sub_count = await push_svc.get_subscription_count()
            elif hasattr(push_svc, "_repo"):
                subs = await push_svc._repo.list_all()
                sub_count = len(subs)
        except Exception:
            pass
        push_status = {
            "configured": getattr(push_svc, "is_configured", False),
            "subscription_count": sub_count,
            "last_sent_at": _fmt_dt(getattr(push_svc, "last_sent_at", None)),
            "last_error": getattr(push_svc, "last_error", None),
            "success_count": getattr(push_svc, "success_count", 0),
            "failure_count": getattr(push_svc, "failure_count", 0),
        }
    else:
        push_status = {
            "configured": False,
            "subscription_count": 0,
            "last_sent_at": None,
            "last_error": None,
            "success_count": 0,
            "failure_count": 0,
        }

    # ── MQTT / Notification Channels ──────────────────────────────────
    channel_svc = getattr(request.app.state, "notification_channel_service", None)
    if channel_svc is not None:
        mqtt_channels = await channel_svc.get_mqtt_status()
        mqtt_status = {
            "channels": mqtt_channels,
            "last_sent_at": _fmt_dt(getattr(channel_svc, "last_sent_at", None)),
            "last_error": getattr(channel_svc, "last_error", None),
            "success_count": getattr(channel_svc, "success_count", 0),
            "failure_count": getattr(channel_svc, "failure_count", 0),
        }
    else:
        mqtt_status = {
            "channels": [],
            "last_sent_at": None,
            "last_error": None,
            "success_count": 0,
            "failure_count": 0,
        }

    return {
        "mqtt": mqtt_status,
        "webhooks": webhooks_status,
        "email": email_status,
        "push": push_status,
    }


def _fmt_dt(dt) -> str | None:
    """Format a datetime to ISO 8601 string, or return None."""
    if dt is None:
        return None
    try:
        return dt.isoformat()
    except Exception:
        return str(dt)
