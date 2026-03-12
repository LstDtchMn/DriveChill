"""FastAPI dependencies for session authentication and CSRF validation."""

from __future__ import annotations

import hmac
from http.cookies import SimpleCookie

from fastapi import Cookie, Header, HTTPException, Request, WebSocket

from app.config import settings

_READ_METHODS = frozenset({"GET", "HEAD", "OPTIONS"})
_API_KEY_SCOPE_PREFIX_RULES: tuple[tuple[str, str], ...] = (
    ("/api/auth/api-keys", "auth"),
    ("/api/alerts", "alerts"),
    ("/api/analytics", "analytics"),
    ("/api/drives", "drives"),
    ("/api/fans", "fans"),
    ("/api/machines", "machines"),
    ("/api/notifications", "notifications"),
    ("/api/profiles", "profiles"),
    ("/api/quiet-hours", "quiet_hours"),
    ("/api/sensors", "sensors"),
    ("/api/settings", "settings"),
    ("/api/temperature-targets", "temperature_targets"),
    ("/api/virtual-sensors", "virtual_sensors"),
    ("/api/notification-channels", "notifications"),
    ("/api/webhooks", "webhooks"),
    ("/api/profile-schedules", "profiles"),
    ("/api/noise-profiles", "settings"),
    ("/api/report-schedules", "settings"),
    ("/api/scheduler", "settings"),
    ("/api/annotations", "analytics"),
    ("/api/integrations", "settings"),
    ("/api/update", "settings"),
)


def _auth_enabled() -> bool:
    """Auth is required when NOT binding to localhost."""
    return settings.auth_required


def _is_internal_request(request: Request) -> bool:
    """Check if request carries the per-process internal token (tray -> backend)."""
    token = request.headers.get("x-drivechill-internal")
    return bool(token and hmac.compare_digest(token, settings.internal_token))


def _extract_api_key(request: Request) -> str | None:
    """Extract API key from Authorization Bearer or X-API-Key."""
    auth_header = request.headers.get("authorization", "")
    if auth_header.lower().startswith("bearer "):
        token = auth_header[7:].strip()
        if token:
            return token
    x_api_key = request.headers.get("x-api-key", "").strip()
    return x_api_key or None


def _required_api_key_scope(request: Request) -> str | None:
    """Map an API request to a required scope string."""
    path = request.url.path.rstrip("/") or "/"
    method = request.method.upper()
    for prefix, domain in _API_KEY_SCOPE_PREFIX_RULES:
        if path == prefix or path.startswith(f"{prefix}/"):
            action = "read" if method in _READ_METHODS else "write"
            return f"{action}:{domain}"
    return None


def _api_key_has_scope(scopes: list[str], required: str) -> bool:
    scope_set = {str(s).strip().lower() for s in scopes}
    if "*" in scope_set:
        return True

    action, _, domain = required.partition(":")
    if not action or not domain:
        return False

    if required in scope_set or f"{action}:*" in scope_set:
        return True
    if action == "read" and (f"write:{domain}" in scope_set or "write:*" in scope_set):
        return True
    return False


async def require_auth(
    request: Request,
    drivechill_session: str | None = Cookie(None),
) -> dict | None:
    """Enforce session auth on protected HTTP routes.

    Returns the session dict when auth is enabled and valid, or None when
    auth is disabled (localhost binding) or for internal tray requests.
    """
    if not _auth_enabled():
        return None

    # Tray sends a per-process internal token to bypass session auth
    if _is_internal_request(request):
        return None

    # API key auth (machine-to-machine path)
    api_key = _extract_api_key(request)
    if api_key:
        auth_service = request.app.state.auth_service
        key_meta = await auth_service.validate_api_key(api_key)
        if key_meta is None:
            ip = request.client.host if request.client else "unknown"
            await auth_service._log_auth_event(
                "api_key_auth_failure", ip, None, "failed", "Invalid API key",
            )
            raise HTTPException(status_code=401, detail="Invalid API key")
        required_scope = _required_api_key_scope(request)
        if required_scope is None:
            raise HTTPException(status_code=403, detail="API key not allowed for this endpoint")
        if not _api_key_has_scope(key_meta.get("scopes", []), required_scope):
            raise HTTPException(
                status_code=403,
                detail=f"API key missing required scope: {required_scope}",
            )
        auth_info = {"auth_type": "api_key", "api_key": key_meta}
        request.state.auth_info = auth_info
        return auth_info

    if not drivechill_session:
        raise HTTPException(status_code=401, detail="Authentication required")

    auth_service = request.app.state.auth_service
    session = await auth_service.validate_session(drivechill_session)
    if session is None:
        raise HTTPException(status_code=401, detail="Session expired or invalid")

    request.state.auth_info = {"auth_type": "session", "session": session}
    return session


async def require_ws_auth(websocket: WebSocket) -> dict | None:
    """Validate session auth for WebSocket connections.

    Reads the session cookie from the WebSocket handshake headers.
    Returns the session dict, or None when auth is disabled.
    Closes the WebSocket with 1008 (Policy Violation) on failure.
    """
    if not _auth_enabled():
        return None

    # Parse cookies from the raw Cookie header (WebSocket doesn't use
    # FastAPI's Cookie() dependency injection).
    cookie_header = websocket.headers.get("cookie", "")
    cookies = SimpleCookie(cookie_header)
    session_morsel = cookies.get("drivechill_session")
    token = session_morsel.value if session_morsel else None

    if not token:
        await websocket.close(code=1008, reason="Authentication required")
        return None

    auth_service = websocket.app.state.auth_service
    session = await auth_service.validate_session(token)
    if session is None:
        await websocket.close(code=1008, reason="Session expired or invalid")
        return None

    return session


async def require_csrf(
    request: Request,
    drivechill_session: str | None = Cookie(None),
    x_csrf_token: str | None = Header(None),
) -> None:
    """Validate CSRF token on state-changing requests (POST/PUT/DELETE).

    Only enforced when auth is enabled. Verifies the X-CSRF-Token header
    matches the token stored in the server-side session.
    """
    if not _auth_enabled():
        return

    existing = getattr(request.state, "auth_info", None)
    if existing and existing.get("auth_type") == "api_key":
        # Viewer-role API keys cannot perform write operations.
        key_meta = existing.get("api_key", {})
        path = request.url.path.rstrip("/")
        if key_meta.get("role", "admin") != "admin" and path != "/api/auth/logout":
            raise HTTPException(status_code=403, detail="Write access requires admin role")
        return

    # Tray sends a per-process internal token to bypass CSRF
    if _is_internal_request(request):
        return

    # API key authenticated requests are stateless and CSRF-exempt.
    api_key = _extract_api_key(request)
    if api_key:
        auth_service = request.app.state.auth_service
        key_meta = await auth_service.validate_api_key(api_key)
        if key_meta is None:
            raise HTTPException(status_code=401, detail="Invalid API key")
        required_scope = _required_api_key_scope(request)
        if required_scope is None:
            raise HTTPException(status_code=403, detail="API key not allowed for this endpoint")
        if not _api_key_has_scope(key_meta.get("scopes", []), required_scope):
            raise HTTPException(
                status_code=403,
                detail=f"API key missing required scope: {required_scope}",
            )
        # Viewer-role API keys cannot perform write operations.
        path = request.url.path.rstrip("/")
        if key_meta.get("role", "admin") != "admin" and path != "/api/auth/logout":
            raise HTTPException(status_code=403, detail="Write access requires admin role")
        request.state.auth_info = {"auth_type": "api_key", "api_key": key_meta}
        return

    if not drivechill_session:
        raise HTTPException(status_code=401, detail="Authentication required")

    # Re-use session already validated by require_auth (avoids double DB query).
    session = None
    if existing and existing.get("auth_type") == "session":
        session = existing.get("session")
    if session is None:
        auth_service = request.app.state.auth_service
        session = await auth_service.validate_session(drivechill_session)
        if session is None:
            raise HTTPException(status_code=401, detail="Session expired or invalid")

    csrf_token = session["csrf_token"]
    if not x_csrf_token or not hmac.compare_digest(str(x_csrf_token), str(csrf_token)):
        raise HTTPException(status_code=403, detail="CSRF token invalid or missing")

    # Viewer-role sessions cannot perform write operations.
    # Logout is exempt so viewers can always end their session.
    assert session is not None
    path = request.url.path.rstrip("/")
    if path not in ("/api/auth/logout", "/api/auth/me/password"):
        role = session.get("role", "admin")
        if role != "admin":
            raise HTTPException(status_code=403, detail="Write access requires admin role")


async def require_admin(
    request: Request,
    drivechill_session: str | None = Cookie(None),
) -> None:
    """Enforce that the authenticated user has the 'admin' role.

    Must be used alongside require_auth (or after it). API-key auth is not
    allowed for admin user-management endpoints.
    """
    if not _auth_enabled():
        return

    if _is_internal_request(request):
        return

    # API keys cannot be used for user-management endpoints
    api_key = _extract_api_key(request)
    if api_key:
        raise HTTPException(status_code=403, detail="User management requires session auth")

    if not drivechill_session:
        raise HTTPException(status_code=401, detail="Authentication required")

    existing = getattr(request.state, "auth_info", None)
    session = None
    if existing and existing.get("auth_type") == "session":
        session = existing.get("session")
    if session is None:
        auth_service = request.app.state.auth_service
        session = await auth_service.validate_session(drivechill_session)
        if session is None:
            raise HTTPException(status_code=401, detail="Session expired or invalid")

    assert session is not None
    role = session.get("role", "admin")
    if role != "admin":
        raise HTTPException(status_code=403, detail="Admin role required")


