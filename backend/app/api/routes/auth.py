"""Authentication API routes: login, logout, session check, initial setup."""

from __future__ import annotations

from fastapi import APIRouter, Cookie, Depends, HTTPException, Request, Response
from pydantic import BaseModel, Field

from app.api.dependencies.auth import require_admin, require_auth, require_csrf

router = APIRouter(prefix="/api/auth", tags=["auth"])


class LoginRequest(BaseModel):
    username: str = Field(min_length=1, max_length=128)
    password: str = Field(min_length=1, max_length=256)


class SetupRequest(BaseModel):
    username: str = Field(min_length=1, max_length=128)
    password: str = Field(min_length=8, max_length=256)


class CreateUserRequest(BaseModel):
    username: str = Field(min_length=1, max_length=128)
    password: str = Field(min_length=8, max_length=256)
    role: str = Field(default="admin", pattern="^(admin|viewer)$")


class SetRoleRequest(BaseModel):
    role: str = Field(pattern="^(admin|viewer)$")


class ChangePasswordRequest(BaseModel):
    password: str = Field(min_length=8, max_length=256)


class SelfPasswordChangeRequest(BaseModel):
    current_password: str = Field(..., max_length=256)
    new_password: str = Field(..., min_length=8, max_length=256)


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
    if not session:
        return {"auth_required": True, "authenticated": False}

    return {
        "auth_required": True,
        "authenticated": True,
        "username": session["username"],
        "role": session.get("role", "admin"),
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
    """Create an API key and return plaintext key once.

    The key's effective role is capped to the caller's own role so that a
    viewer cannot mint an admin-privilege key.
    """
    auth_service = request.app.state.auth_service

    # Determine the caller's role and username from the current auth context.
    auth_info = getattr(request.state, "auth_info", None) or {}
    if auth_info.get("auth_type") == "session":
        session = auth_info.get("session", {})
        caller_role = session.get("role", "admin")
        caller_username = session.get("username")
    elif auth_info.get("auth_type") == "api_key":
        key_meta = auth_info.get("api_key", {})
        caller_role = key_meta.get("role", "admin")
        caller_username = key_meta.get("name")
    else:
        caller_role = "admin"
        caller_username = None

    try:
        metadata, plaintext = await auth_service.create_api_key(
            body.name,
            scopes=body.scopes,
            created_by_username=caller_username,
            requesting_role=caller_role,
        )
    except ValueError as exc:
        raise HTTPException(status_code=422, detail=str(exc)) from exc
    ip = request.client.host if request.client else "unknown"
    await auth_service._log_auth_event(
        "api_key_created", ip, caller_username, "success",
        f"key_id={metadata['id']} name={metadata['name']} role={metadata['role']}",
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


# ── Self-service password change ────────────────────────────────────────────

@router.post("/me/password", dependencies=[Depends(require_auth), Depends(require_csrf)])
async def change_my_password(body: SelfPasswordChangeRequest, request: Request, response: Response):
    """Change the current user's own password. Rotates session token on success."""
    auth_service = request.app.state.auth_service
    session_token = request.cookies.get("drivechill_session")
    session = await auth_service.validate_session(session_token)
    if not session:
        raise HTTPException(status_code=401, detail="Invalid session")

    user = await auth_service.get_user(session["username"])
    if not user:
        raise HTTPException(status_code=404, detail="User not found")

    if not auth_service.verify_password(body.current_password, user["password_hash"]):
        raise HTTPException(status_code=403, detail="Current password is incorrect")

    pw_hash = auth_service.hash_password(body.new_password)
    await auth_service.change_user_password(user["id"], pw_hash)

    # Invalidate ALL sessions for this user (including the current one)
    await auth_service.delete_all_sessions_for_user(user["id"])

    # Issue a fresh session so the client stays logged in
    ip = request.client.host if request.client else "unknown"
    new_session_token, new_csrf_token = await auth_service.create_session(
        user["id"], ip, request.headers.get("user-agent", ""),
    )

    await auth_service._log_auth_event(
        "self_password_changed", ip, user["username"], "success", "",
    )

    _set_session_cookies(
        response, new_session_token, new_csrf_token,
        secure=_is_secure_request(request),
    )
    return {"success": True}


# ── User management (admin only) ────────────────────────────────────────────

@router.get("/users", dependencies=[Depends(require_auth), Depends(require_admin)])
async def list_users(request: Request):
    """List all users. Requires admin role."""
    auth_service = request.app.state.auth_service
    users = await auth_service.list_users()
    return {"users": users}


@router.post("/users", dependencies=[Depends(require_auth), Depends(require_admin), Depends(require_csrf)])
async def create_user(body: CreateUserRequest, request: Request):
    """Create a new user. Requires admin role."""
    auth_service = request.app.state.auth_service
    ip = request.client.host if request.client else "unknown"
    try:
        user_id = await auth_service.create_user(body.username, body.password, role=body.role)
    except Exception as exc:
        raise HTTPException(status_code=409, detail=str(exc)) from exc
    await auth_service._log_auth_event(
        "user_created", ip, body.username, "success",
        f"user_id={user_id} role={body.role}",
    )
    return {"success": True, "user_id": user_id, "username": body.username, "role": body.role}


@router.put("/users/{user_id}/role", dependencies=[Depends(require_auth), Depends(require_admin), Depends(require_csrf)])
async def set_user_role(user_id: int, body: SetRoleRequest, request: Request):
    """Change a user's role. Requires admin role."""
    auth_service = request.app.state.auth_service
    try:
        updated = await auth_service.set_user_role(user_id, body.role)
    except ValueError as exc:
        raise HTTPException(status_code=422, detail=str(exc)) from exc
    if not updated:
        raise HTTPException(status_code=404, detail="User not found")
    ip = request.client.host if request.client else "unknown"
    await auth_service._log_auth_event(
        "user_role_changed", ip, None, "success",
        f"user_id={user_id} role={body.role}",
    )
    return {"success": True}


@router.put("/users/{user_id}/password", dependencies=[Depends(require_auth), Depends(require_admin), Depends(require_csrf)])
async def change_user_password(user_id: int, body: ChangePasswordRequest, request: Request):
    """Change a user's password. Requires admin role."""
    auth_service = request.app.state.auth_service
    user = await auth_service.get_user_by_id(user_id)
    if not user:
        raise HTTPException(status_code=404, detail="User not found")
    pw_hash = auth_service.hash_password(body.password)
    await auth_service._db.execute(
        "UPDATE users SET password_hash = ?, updated_at = datetime('now') WHERE id = ?",
        (pw_hash, user_id),
    )
    await auth_service._db.commit()
    # Invalidate all existing sessions — a stolen session cannot persist after
    # an admin resets the password (GAP-2).
    await auth_service._db.execute(
        "DELETE FROM sessions WHERE user_id = ?", (user_id,)
    )
    await auth_service._db.commit()
    ip = request.client.host if request.client else "unknown"
    await auth_service._log_auth_event(
        "password_changed", ip, user["username"], "success", f"user_id={user_id}",
    )
    return {"success": True}


@router.delete("/users/{user_id}", dependencies=[Depends(require_auth), Depends(require_admin), Depends(require_csrf)])
async def delete_user(user_id: int, request: Request):
    """Delete a user. Requires admin role. Cannot delete the last admin."""
    auth_service = request.app.state.auth_service
    user = await auth_service.get_user_by_id(user_id)
    if not user:
        raise HTTPException(status_code=404, detail="User not found")
    try:
        deleted = await auth_service.delete_user(user_id)
    except ValueError as exc:
        raise HTTPException(status_code=409, detail=str(exc)) from exc
    if not deleted:
        raise HTTPException(status_code=404, detail="User not found")
    ip = request.client.host if request.client else "unknown"
    await auth_service._log_auth_event(
        "user_deleted", ip, user["username"], "success", f"user_id={user_id}",
    )
    return {"success": True}
