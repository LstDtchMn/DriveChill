"""HTTP-layer tests for require_auth and require_csrf dependencies.

Tests API key extraction, scope checking, and session/CSRF validation
at the FastAPI dependency level.
"""

from __future__ import annotations

import pytest

from app.api.dependencies.auth import (
    _api_key_has_scope,
    _extract_api_key,
    _required_api_key_scope,
)


# -----------------------------------------------------------------------
# _extract_api_key
# -----------------------------------------------------------------------


class _FakeHeaders(dict):
    """Mimics Starlette's Headers (case-insensitive get)."""
    def get(self, key: str, default: str = "") -> str:
        for k, v in self.items():
            if k.lower() == key.lower():
                return v
        return default


class _FakeRequest:
    def __init__(self, headers: dict, path: str = "/api/sensors", method: str = "GET"):
        self.headers = _FakeHeaders(headers)
        self.method = method

        class _Url:
            def __init__(self, p: str):
                self.path = p
        self.url = _Url(path)


class TestExtractApiKey:
    def test_bearer_header(self):
        req = _FakeRequest({"authorization": "Bearer dc_live_abc123"})
        assert _extract_api_key(req) == "dc_live_abc123"

    def test_bearer_header_case_insensitive(self):
        req = _FakeRequest({"Authorization": "bearer DC_LIVE_XYZ"})
        assert _extract_api_key(req) == "DC_LIVE_XYZ"

    def test_x_api_key_header(self):
        req = _FakeRequest({"x-api-key": "dc_live_key456"})
        assert _extract_api_key(req) == "dc_live_key456"

    def test_bearer_takes_precedence(self):
        req = _FakeRequest({
            "authorization": "Bearer primary_key",
            "x-api-key": "fallback_key",
        })
        assert _extract_api_key(req) == "primary_key"

    def test_no_api_key_returns_none(self):
        req = _FakeRequest({})
        assert _extract_api_key(req) is None

    def test_empty_bearer_falls_through(self):
        req = _FakeRequest({"authorization": "Bearer "})
        assert _extract_api_key(req) is None

    def test_non_bearer_auth_ignored(self):
        req = _FakeRequest({"authorization": "Basic dXNlcjpwYXNz"})
        assert _extract_api_key(req) is None


# -----------------------------------------------------------------------
# _required_api_key_scope
# -----------------------------------------------------------------------


class TestRequiredScope:
    def test_sensors_get(self):
        req = _FakeRequest({}, path="/api/sensors", method="GET")
        assert _required_api_key_scope(req) == "read:sensors"

    def test_sensors_post(self):
        req = _FakeRequest({}, path="/api/sensors/labels", method="POST")
        assert _required_api_key_scope(req) == "write:sensors"

    def test_fans_put(self):
        req = _FakeRequest({}, path="/api/fans/curves", method="PUT")
        assert _required_api_key_scope(req) == "write:fans"

    def test_alerts_delete(self):
        req = _FakeRequest({}, path="/api/alerts/r1", method="DELETE")
        assert _required_api_key_scope(req) == "write:alerts"

    def test_auth_api_keys(self):
        req = _FakeRequest({}, path="/api/auth/api-keys", method="GET")
        assert _required_api_key_scope(req) == "read:auth"

    def test_machines(self):
        req = _FakeRequest({}, path="/api/machines", method="POST")
        assert _required_api_key_scope(req) == "write:machines"

    def test_quiet_hours(self):
        req = _FakeRequest({}, path="/api/quiet-hours", method="GET")
        assert _required_api_key_scope(req) == "read:quiet_hours"

    def test_webhooks(self):
        req = _FakeRequest({}, path="/api/webhooks", method="PUT")
        assert _required_api_key_scope(req) == "write:webhooks"

    def test_settings(self):
        req = _FakeRequest({}, path="/api/settings", method="GET")
        assert _required_api_key_scope(req) == "read:settings"

    def test_profiles(self):
        req = _FakeRequest({}, path="/api/profiles/p1/activate", method="PUT")
        assert _required_api_key_scope(req) == "write:profiles"

    def test_unknown_path_returns_none(self):
        req = _FakeRequest({}, path="/api/health", method="GET")
        assert _required_api_key_scope(req) is None

    def test_trailing_slash_stripped(self):
        req = _FakeRequest({}, path="/api/sensors/", method="GET")
        assert _required_api_key_scope(req) == "read:sensors"


# -----------------------------------------------------------------------
# _api_key_has_scope
# -----------------------------------------------------------------------


class TestApiKeyHasScope:
    def test_wildcard_grants_everything(self):
        assert _api_key_has_scope(["*"], "read:sensors") is True
        assert _api_key_has_scope(["*"], "write:fans") is True

    def test_exact_match(self):
        assert _api_key_has_scope(["read:sensors"], "read:sensors") is True
        assert _api_key_has_scope(["write:fans"], "write:fans") is True

    def test_action_wildcard(self):
        assert _api_key_has_scope(["read:*"], "read:sensors") is True
        assert _api_key_has_scope(["read:*"], "read:alerts") is True

    def test_action_wildcard_does_not_grant_write(self):
        assert _api_key_has_scope(["read:*"], "write:sensors") is False

    def test_write_implies_read(self):
        assert _api_key_has_scope(["write:sensors"], "read:sensors") is True

    def test_write_wildcard_implies_all_reads(self):
        assert _api_key_has_scope(["write:*"], "read:sensors") is True
        assert _api_key_has_scope(["write:*"], "read:fans") is True

    def test_read_does_not_imply_write(self):
        assert _api_key_has_scope(["read:sensors"], "write:sensors") is False

    def test_wrong_domain_denied(self):
        assert _api_key_has_scope(["read:sensors"], "read:fans") is False
        assert _api_key_has_scope(["write:fans"], "write:sensors") is False

    def test_empty_scopes_deny(self):
        assert _api_key_has_scope([], "read:sensors") is False

    def test_case_insensitive(self):
        assert _api_key_has_scope(["READ:Sensors"], "read:sensors") is True

    def test_empty_required_denied(self):
        # Wildcard grants everything including empty (edge case);
        # non-wildcard scopes correctly reject empty required scope.
        assert _api_key_has_scope(["read:sensors"], "") is False
        assert _api_key_has_scope(["write:fans"], "") is False

    def test_multiple_scopes(self):
        scopes = ["read:sensors", "write:fans"]
        assert _api_key_has_scope(scopes, "read:sensors") is True
        assert _api_key_has_scope(scopes, "write:fans") is True
        assert _api_key_has_scope(scopes, "read:fans") is True  # write:fans implies read:fans
        assert _api_key_has_scope(scopes, "write:sensors") is False
