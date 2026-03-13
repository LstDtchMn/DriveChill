"""Tests for notification route handlers (email + push subscriptions)."""
from __future__ import annotations

import asyncio
from unittest.mock import AsyncMock, MagicMock

import pytest


def _req(**state_attrs):
    req = MagicMock()
    for k, v in state_attrs.items():
        setattr(req.app.state, k, v)
    return req


def _run(coro):
    return asyncio.run(coro)


# ---------------------------------------------------------------------------
# Email settings
# ---------------------------------------------------------------------------


class TestGetEmailSettings:
    def test_returns_settings(self):
        from app.api.routes.notifications import get_email_settings

        repo = AsyncMock()
        repo.get = AsyncMock(return_value={
            "enabled": True,
            "smtp_host": "smtp.example.com",
            "smtp_port": 587,
            "smtp_username": "user",
            "has_password": True,
            "sender_address": "noreply@example.com",
            "recipient_list": ["admin@example.com"],
            "use_tls": True,
            "use_ssl": False,
            "updated_at": "2026-03-12T10:00:00+00:00",
        })
        req = _req(email_notification_repo=repo)
        result = _run(get_email_settings(req))
        assert "settings" in result
        assert result["settings"]["smtp_host"] == "smtp.example.com"


class TestUpdateEmailSettings:
    def test_partial_update(self):
        from app.api.routes.notifications import (
            UpdateEmailSettingsRequest,
            update_email_settings,
        )

        repo = AsyncMock()
        repo.update = AsyncMock(return_value={
            "enabled": True,
            "smtp_host": "new-host.example.com",
            "smtp_port": 587,
        })
        req = _req(email_notification_repo=repo)
        body = UpdateEmailSettingsRequest(smtp_host="new-host.example.com")
        result = _run(update_email_settings(body, req))
        assert result["success"] is True
        # Only smtp_host should be in the update kwargs
        call_kwargs = repo.update.call_args.kwargs
        assert "smtp_host" in call_kwargs
        assert "smtp_port" not in call_kwargs  # not supplied, not sent

    def test_password_preserved_when_not_sent(self):
        from app.api.routes.notifications import (
            UpdateEmailSettingsRequest,
            update_email_settings,
        )

        repo = AsyncMock()
        repo.update = AsyncMock(return_value={"enabled": False})
        req = _req(email_notification_repo=repo)
        body = UpdateEmailSettingsRequest(enabled=False)
        _run(update_email_settings(body, req))
        call_kwargs = repo.update.call_args.kwargs
        assert "smtp_password" not in call_kwargs


class TestTestEmail:
    def test_success(self):
        from app.api.routes.notifications import test_email

        svc = AsyncMock()
        svc.send_test = AsyncMock(return_value=True)
        req = _req(email_notification_service=svc)
        result = _run(test_email(req))
        assert result["success"] is True
        assert result["error"] is None

    def test_failure_returns_error(self):
        from app.api.routes.notifications import test_email

        svc = AsyncMock()
        svc.send_test = AsyncMock(side_effect=ConnectionError("SMTP timeout"))
        req = _req(email_notification_service=svc)
        result = _run(test_email(req))
        assert result["success"] is False
        assert "SMTP timeout" in result["error"]


# ---------------------------------------------------------------------------
# Push subscriptions
# ---------------------------------------------------------------------------


class TestListPushSubscriptions:
    def test_returns_redacted_subscriptions(self):
        from app.api.routes.notifications import list_push_subscriptions

        repo = AsyncMock()
        repo.list_all = AsyncMock(return_value=[{
            "id": "sub1",
            "endpoint": "https://push.example.com/sub1",
            "p256dh": "SECRET_KEY",
            "auth": "SECRET_AUTH",
            "user_agent": "Chrome/120",
            "created_at": "2026-03-12T10:00:00Z",
            "last_used_at": None,
        }])
        req = _req(push_subscription_repo=repo)
        result = _run(list_push_subscriptions(req))
        assert len(result["subscriptions"]) == 1
        sub = result["subscriptions"][0]
        assert "p256dh" not in sub
        assert "auth" not in sub
        assert sub["endpoint"] == "https://push.example.com/sub1"


class TestCreatePushSubscription:
    def test_creates_new_subscription(self):
        from app.api.routes.notifications import (
            CreatePushSubscriptionRequest,
            create_push_subscription,
        )

        repo = AsyncMock()
        repo.get_by_endpoint = AsyncMock(return_value=None)
        repo.create = AsyncMock(return_value={
            "id": "new-sub",
            "endpoint": "https://push.example.com/new",
            "p256dh": "KEY",
            "auth": "AUTH",
            "user_agent": "Firefox/120",
            "created_at": "2026-03-12T10:00:00Z",
            "last_used_at": None,
        })
        req = _req(push_subscription_repo=repo)
        body = CreatePushSubscriptionRequest(
            endpoint="https://push.example.com/new",
            p256dh="KEY",
            auth="AUTH",
            user_agent="Firefox/120",
        )
        result = _run(create_push_subscription(body, req))
        assert result["success"] is True
        # Redacted — no p256dh/auth
        assert "p256dh" not in result["subscription"]

    def test_returns_existing_on_duplicate_endpoint(self):
        from app.api.routes.notifications import (
            CreatePushSubscriptionRequest,
            create_push_subscription,
        )

        existing = {
            "id": "existing-sub",
            "endpoint": "https://push.example.com/dup",
            "p256dh": "KEY",
            "auth": "AUTH",
            "user_agent": "Chrome",
            "created_at": "2026-03-12T10:00:00Z",
            "last_used_at": None,
        }
        repo = AsyncMock()
        repo.get_by_endpoint = AsyncMock(return_value=existing)
        req = _req(push_subscription_repo=repo)
        body = CreatePushSubscriptionRequest(
            endpoint="https://push.example.com/dup",
            p256dh="KEY2",
            auth="AUTH2",
        )
        result = _run(create_push_subscription(body, req))
        assert result["success"] is True
        assert result["subscription"]["id"] == "existing-sub"
        # create should NOT have been called
        repo.create.assert_not_called()


class TestDeletePushSubscription:
    def test_deletes_existing(self):
        from app.api.routes.notifications import delete_push_subscription

        repo = AsyncMock()
        repo.delete = AsyncMock(return_value=True)
        req = _req(push_subscription_repo=repo)
        result = _run(delete_push_subscription("sub1", req))
        assert result["success"] is True

    def test_returns_404_for_unknown(self):
        from app.api.routes.notifications import delete_push_subscription
        from fastapi import HTTPException

        repo = AsyncMock()
        repo.delete = AsyncMock(return_value=False)
        req = _req(push_subscription_repo=repo)
        with pytest.raises(HTTPException) as exc_info:
            _run(delete_push_subscription("nonexistent", req))
        assert exc_info.value.status_code == 404


class TestTestPushSubscription:
    def test_success(self):
        from app.api.routes.notifications import TestPushRequest, test_push_subscription

        svc = AsyncMock()
        svc.send_test = AsyncMock(return_value=True)
        req = _req(push_notification_service=svc)
        body = TestPushRequest(subscription_id="sub1")
        result = _run(test_push_subscription(body, req))
        assert result["success"] is True

    def test_failure_returns_502(self):
        from app.api.routes.notifications import TestPushRequest, test_push_subscription
        from fastapi import HTTPException

        svc = AsyncMock()
        svc.send_test = AsyncMock(return_value=False)
        req = _req(push_notification_service=svc)
        body = TestPushRequest(subscription_id="sub1")
        with pytest.raises(HTTPException) as exc_info:
            _run(test_push_subscription(body, req))
        assert exc_info.value.status_code == 502
