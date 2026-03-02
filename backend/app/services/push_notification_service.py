"""Web Push notification delivery service (VAPID)."""

from __future__ import annotations

import asyncio
import json
import logging
from datetime import datetime, timezone
from functools import partial

from app.db.repositories.push_subscription_repo import PushSubscriptionRepo

logger = logging.getLogger(__name__)


class PushNotificationService:
    """Delivers Web Push notifications to subscribed browsers."""

    def __init__(
        self,
        repo: PushSubscriptionRepo,
        vapid_private_key: str,
        vapid_claims: dict,
    ) -> None:
        self._repo = repo
        self._private_key = vapid_private_key
        self._claims = vapid_claims

    async def send_alert(
        self,
        sensor_name: str,
        value: float,
        threshold: float,
        unit: str = "\u00b0C",
    ) -> int:
        """Send alert push to all subscriptions. Returns count of successful deliveries."""
        try:
            from pywebpush import webpush, WebPushException
        except ImportError:
            logger.warning(
                "pywebpush is not installed; skipping Web Push delivery. "
                "Install it with: pip install pywebpush"
            )
            return 0

        if not self._private_key:
            logger.debug("VAPID private key not configured; skipping push notifications")
            return 0

        payload = {
            "type": "alert",
            "title": "DriveChill Alert",
            "body": f"{sensor_name} reached {value:.1f}{unit} (threshold: {threshold:.1f}{unit})",
            "timestamp": datetime.now(timezone.utc).isoformat(),
        }

        subscriptions = await self._repo.list_all()
        successes = 0

        loop = asyncio.get_running_loop()
        for sub in subscriptions:
            try:
                # webpush() is a blocking HTTP call; offload to a thread so we
                # don't stall the event loop (and therefore fan control) while
                # waiting for push service responses.
                await loop.run_in_executor(
                    None,
                    partial(
                        webpush,
                        subscription_info={
                            "endpoint": sub["endpoint"],
                            "keys": {"p256dh": sub["p256dh"], "auth": sub["auth"]},
                        },
                        data=json.dumps(payload),
                        vapid_private_key=self._private_key,
                        vapid_claims=self._claims,
                    ),
                )
                await self._repo.update_last_used(sub["id"])
                successes += 1
            except WebPushException as exc:
                status = getattr(exc, "response", None)
                status_code = status.status_code if status is not None else None
                if status_code == 410:
                    logger.info(
                        "Push subscription %s has expired (410 Gone); removing", sub["id"]
                    )
                    await self._repo.delete(sub["id"])
                else:
                    logger.warning(
                        "Web Push delivery failed for subscription %s (status=%s): %s",
                        sub["id"], status_code, exc,
                    )
            except Exception:
                logger.exception(
                    "Unexpected error delivering push notification to subscription %s", sub["id"]
                )

        return successes

    async def send_test(self, subscription_id: str) -> bool:
        """Send a test push to one subscription. Returns True on success."""
        try:
            from pywebpush import webpush, WebPushException
        except ImportError:
            logger.warning(
                "pywebpush is not installed; skipping Web Push delivery. "
                "Install it with: pip install pywebpush"
            )
            return False

        if not self._private_key:
            logger.debug("VAPID private key not configured; skipping push notifications")
            return False

        sub = await self._repo.get(subscription_id)
        if sub is None:
            logger.warning("Test push requested for unknown subscription %s", subscription_id)
            return False

        payload = {
            "type": "test",
            "title": "DriveChill Test",
            "body": "Web Push notifications are working correctly.",
            "timestamp": datetime.now(timezone.utc).isoformat(),
        }

        try:
            loop = asyncio.get_running_loop()
            await loop.run_in_executor(
                None,
                partial(
                    webpush,
                    subscription_info={
                        "endpoint": sub["endpoint"],
                        "keys": {"p256dh": sub["p256dh"], "auth": sub["auth"]},
                    },
                    data=json.dumps(payload),
                    vapid_private_key=self._private_key,
                    vapid_claims=self._claims,
                ),
            )
            await self._repo.update_last_used(sub["id"])
            return True
        except WebPushException as exc:
            status = getattr(exc, "response", None)
            status_code = status.status_code if status is not None else None
            if status_code == 410:
                logger.info(
                    "Push subscription %s has expired (410 Gone); removing", sub["id"]
                )
                await self._repo.delete(sub["id"])
            else:
                logger.warning(
                    "Web Push test delivery failed for subscription %s (status=%s): %s",
                    sub["id"], status_code, exc,
                )
            return False
        except Exception:
            logger.exception(
                "Unexpected error delivering test push to subscription %s", sub["id"]
            )
            return False
