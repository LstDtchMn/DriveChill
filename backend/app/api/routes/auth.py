"""Authentication API routes: login, logout, session check, initial setup."""

from __future__ import annotations

from fastapi import APIRouter, Cookie, Depends, HTTPException, Request, Response
from pydantic import BaseModel, Field

from app.api.dependencies.auth import require_auth, require_csrf

router = APIRouter(prefix="/api/auth", tags=["auth"])


class LoginRequest(BaseModel):
    username: str = Field(min_length=1, max_length=128)
    password: str = Field(min_length=1, max_length=256)


class SetupRequest(BaseModel):
    username: str = Field(min_length=1, max_length=128)
    password: str = Field(min_length=8, max_length=256)


class CreateApiKeyRequest(BaseModel):
    name: str = Field(min_length=1, max_length=120)
    scopes: list[str] | None = Field(default=None, max_length=32)


def _is_secure_request(request: Request) -> bool:
    """Determine if the client connection is over TLS.

    Checks both the direct URL scheme and the X-Forwarded-Proto header
    set by reverse proxies that terminate TLS upstream.
    """
    if request.url.scheme == "https":
        return True
    forwarded_proto = request.headers.get("x-forwarded-proto", "")
    return forwarded_proto.lower() == "https"


def _set_session_cookies(
    response: Response, session_token: str, csrf_token: str, *, secure: bool,
) -> None:
    response.set_cookie(
        key="drivechill_session",
        value=session_token,
        httponly=True,
        samesite="strict",
        secure=secure,
        path="/",
    )
    response.set_cookie(
        key="drivechill_csrf",
        value=csrf_token,
        httponly=False,  # JS must read this for CSRF header
        samesite="strict",
        secure=secure,
        path="/",
    )


@router.post("/login")
async def login(body: LoginRequest, request: Request, response: Response):
    """Authenticate and create a session."""
    auth_service = request.app.state.auth_service
    ip = request.client.host if request.client else "unknown"

    if not auth_service.check_rate_limit(ip):
        raise HTTPException(status_code=429, detail="Too many requests. Try again later.")

    result = await auth_service.login(
        body.username, body.password, ip,
        user_agent=request.headers.get("user-agent", ""),
    )
    if result is None:
        raise HTTPException(status_code=401, detail="Invalid credentials or account locked")

    session_token, csrf_token = result
    _set_session_cookies(
        response, session_token, csrf_token,
        secure=_is_secure_request(request),
    )
    return {"success": True, "username": body.username}


@router.post("/logout", dependencies=[Depends(require_csrf)])
async def logout(
    request: Request, response: Response,
    drivechill_session: str | None = Cookie(None),
):
    """Destroy the current session."""
    if drivechill_session:
        auth_service = request.app.state.auth_service
        ip = request.client.host if request.client else "unknown"
        await auth_service.logout(drivechill_session, ip)

    response.delete_cookie("drivechill_session", path="/")
    response.delete_cookie("drivechill_csrf", path="/")
    return {"success": True}


@router.get("/session")
async def check_session(
    request: Request,
    drivechill_session: str | None = Cookie(None),
):
    """Check if the current session is valid. Frontend calls this on page load."""
    from app.api.dependencies.auth import _auth_enabled

    if not _auth_enabled():
        return {"auth_required": False, "authenticated": True}

    if not drivechill_session:
        return {"auth_required": True, "authenticated": False}

    auth_service = request.app.state.auth_service
    session = await auth_service.validate_session(drivechill_session)
    if session is None:
        return {"auth_required": True, "authenticated": False}

    return {
        "auth_required": True,
        "authenticated": True,
        "username": session["username"],
    }


@router.post("/setup")
async def initial_setup(body: SetupRequest, request: Request, response: Response):
    """Create the initial admin user. Only works when no users exist."""
    auth_service = request.app.state.auth_service

    if await auth_service.user_exists():
        raise HTTPException(status_code=409, detail="Setup already completed. User exists.")

    ip = request.client.host if request.client else "unknown"
    user_id = await auth_service.create_user(body.username, body.password)
    await auth_service._log_auth_event(
        "user_created", ip, body.username, "success",
        f"Initial setup, user_id={user_id}",
    )

    # Auto-login after setup
    result = await auth_service.login(
        body.username, body.password, ip,
        user_agent=request.headers.get("user-agent", ""),
    )
    if result:
        session_token, csrf_token = result
        _set_session_cookies(
            response, session_token, csrf_token,
            secure=_is_secure_request(request),
        )

    return {"success": True, "username": body.username}


@router.get("/status")
async def auth_status():
    """Return whether auth is enabled (for frontend to know whether to show login)."""
    from app.api.dependencies.auth import _auth_enabled
    return {"auth_enabled": _auth_enabled()}


@router.get("/api-keys", dependencies=[Depends(require_auth)])
async def list_api_keys(request: Request):
    """List API keys (metadata only, never includes plaintext key)."""
    auth_service = request.app.state.auth_service
    keys = await auth_service.list_api_keys()
    return {"api_keys": keys}


@router.post("/api-keys", dependencies=[Depends(require_auth), Depends(require_csrf)])
async def create_api_key(body: CreateApiKeyRequest, request: Request):
    """Create an API key and return plaintext key once."""
    auth_service = request.app.state.auth_service
    try:
        metadata, plaintext = await auth_service.create_api_key(
            body.name,
            scopes=body.scopes,
        )
    except ValueError as exc:
        raise HTTPException(status_code=422, detail=str(exc)) from exc
    ip = request.client.host if request.client else "unknown"
    await auth_service._log_auth_event(
        "api_key_created", ip, None, "success",
        f"key_id={metadata['id']} name={metadata['name']}",
    )
    return {"api_key": metadata, "plaintext_key": plaintext}


@router.delete("/api-keys/{key_id}", dependencies=[Depends(require_auth), Depends(require_csrf)])
async def revoke_api_key(key_id: str, request: Request):
    """Revoke an API key."""
    auth_service = request.app.state.auth_service
    revoked = await auth_service.revoke_api_key(key_id)
    if not revoked:
        raise HTTPException(status_code=404, detail="API key not found")
    ip = request.client.host if request.client else "unknown"
    await auth_service._log_auth_event(
        "api_key_revoked", ip, None, "success",
        f"key_id={key_id}",
    )
    return {"success": True}
