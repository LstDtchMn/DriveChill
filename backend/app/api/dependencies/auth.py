"""FastAPI dependencies for session authentication and CSRF validation."""

from __future__ import annotations

from http.cookies import SimpleCookie

from fastapi import Cookie, Header, HTTPException, Request, WebSocket

from app.config import settings


def _auth_enabled() -> bool:
    """Auth is required when NOT binding to localhost."""
    return settings.auth_required


def _is_internal_request(request: Request) -> bool:
    """Check if request carries the per-process internal token (tray -> backend)."""
    token = request.headers.get("x-drivechill-internal")
    return bool(token and token == settings.internal_token)


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

    if not drivechill_session:
        raise HTTPException(status_code=401, detail="Authentication required")

    auth_service = request.app.state.auth_service
    session = await auth_service.validate_session(drivechill_session)
    if session is None:
        raise HTTPException(status_code=401, detail="Session expired or invalid")

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

    # Tray sends a per-process internal token to bypass CSRF
    if _is_internal_request(request):
        return

    if not drivechill_session:
        raise HTTPException(status_code=401, detail="Authentication required")

    auth_service = request.app.state.auth_service
    session = await auth_service.validate_session(drivechill_session)
    if session is None:
        raise HTTPException(status_code=401, detail="Session expired or invalid")

    if not x_csrf_token or x_csrf_token != session["csrf_token"]:
        raise HTTPException(status_code=403, detail="CSRF token invalid or missing")
