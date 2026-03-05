"""Authentication service: login, API keys, session management, and audit logging."""

from __future__ import annotations

import json
import logging
import hashlib
import secrets
import time
from collections import defaultdict
from datetime import datetime, timedelta, timezone

import aiosqlite
import bcrypt

logger = logging.getLogger(__name__)

# Rate limiter: in-memory, per-IP
_rate_buckets: dict[str, list[float]] = defaultdict(list)
_RATE_LIMIT = 10
_RATE_WINDOW = 60.0  # seconds

# Brute-force constants
_MAX_FAILED_ATTEMPTS = 5
_LOCKOUT_DURATION = timedelta(minutes=15)

_API_KEY_SCOPE_DOMAINS = (
    "alerts",
    "analytics",
    "auth",
    "drives",
    "fans",
    "machines",
    "notifications",
    "profiles",
    "quiet_hours",
    "sensors",
    "settings",
    "temperature_targets",
    "webhooks",
)
_DEFAULT_API_KEY_SCOPES = ("read:sensors",)
_ALLOWED_API_KEY_SCOPES = frozenset(
    {"*", "read:*", "write:*"} |
    {f"read:{d}" for d in _API_KEY_SCOPE_DOMAINS} |
    {f"write:{d}" for d in _API_KEY_SCOPE_DOMAINS}
)


def _normalize_api_key_scopes(scopes: list[str] | None) -> list[str]:
    raw = scopes if scopes is not None else list(_DEFAULT_API_KEY_SCOPES)
    if not raw:
        raise ValueError("At least one scope is required")
    out: list[str] = []
    seen: set[str] = set()
    for item in raw:
        scope = str(item).strip().lower()
        if not scope:
            raise ValueError("Scopes cannot contain empty values")
        if scope not in _ALLOWED_API_KEY_SCOPES:
            raise ValueError(f"Unsupported API key scope: {scope}")
        if scope in seen:
            continue
        seen.add(scope)
        out.append(scope)
    return out


def _parse_scopes_json(raw: str | None) -> list[str]:
    if not raw:
        return list(_DEFAULT_API_KEY_SCOPES)
    try:
        parsed = json.loads(raw)
    except json.JSONDecodeError:
        return list(_DEFAULT_API_KEY_SCOPES)
    if not isinstance(parsed, list):
        return list(_DEFAULT_API_KEY_SCOPES)
    try:
        return _normalize_api_key_scopes([str(x) for x in parsed])
    except ValueError:
        return list(_DEFAULT_API_KEY_SCOPES)


class AuthService:
    """Handles login, logout, session validation, brute-force protection, and audit logging."""

    def __init__(self, db: aiosqlite.Connection, session_ttl_seconds: int = 28800) -> None:
        self._db = db
        self._session_ttl = session_ttl_seconds  # default 8h

    # --- Password hashing ---

    @staticmethod
    def hash_password(password: str) -> str:
        """Hash a password with bcrypt, cost factor 12."""
        return bcrypt.hashpw(
            password.encode("utf-8"), bcrypt.gensalt(rounds=12)
        ).decode("ascii")

    @staticmethod
    def verify_password(password: str, password_hash: str) -> bool:
        """Verify a plaintext password against a bcrypt hash."""
        return bcrypt.checkpw(
            password.encode("utf-8"), password_hash.encode("ascii")
        )

    # --- Rate limiting (in-memory, per IP) ---

    @staticmethod
    def check_rate_limit(ip: str) -> bool:
        """Return True if the IP is within the rate limit, False if exceeded."""
        now = time.monotonic()
        bucket = _rate_buckets[ip]
        bucket[:] = [t for t in bucket if now - t < _RATE_WINDOW]
        if len(bucket) >= _RATE_LIMIT:
            return False
        bucket.append(now)

        # Prune stale IPs every 256 checks to prevent unbounded memory growth.
        if len(_rate_buckets) > 256:
            stale = [k for k, v in _rate_buckets.items() if not v or now - v[-1] > _RATE_WINDOW]
            for k in stale:
                del _rate_buckets[k]

        return True

    # --- User management ---

    async def get_user(self, username: str) -> dict | None:
        cursor = await self._db.execute(
            "SELECT id, username, password_hash, locked_until, failed_attempts, "
            "COALESCE(role, 'admin') "
            "FROM users WHERE username = ?",
            (username,),
        )
        row = await cursor.fetchone()
        if not row:
            return None
        return {
            "id": row[0], "username": row[1], "password_hash": row[2],
            "locked_until": row[3], "failed_attempts": row[4], "role": row[5],
        }

    async def get_user_by_id(self, user_id: int) -> dict | None:
        cursor = await self._db.execute(
            "SELECT id, username, COALESCE(role, 'admin') FROM users WHERE id = ?",
            (user_id,),
        )
        row = await cursor.fetchone()
        if not row:
            return None
        return {"id": row[0], "username": row[1], "role": row[2]}

    async def list_users(self) -> list[dict]:
        cursor = await self._db.execute(
            "SELECT id, username, COALESCE(role, 'admin'), created_at FROM users ORDER BY id"
        )
        rows = await cursor.fetchall()
        return [
            {"id": r[0], "username": r[1], "role": r[2], "created_at": r[3]}
            for r in rows
        ]

    async def user_exists(self) -> bool:
        cursor = await self._db.execute("SELECT COUNT(*) FROM users")
        row = await cursor.fetchone()
        return row[0] > 0

    async def create_user(self, username: str, password: str, role: str = "admin") -> int:
        if role not in ("admin", "viewer"):
            raise ValueError(f"Invalid role: {role}")
        pw_hash = self.hash_password(password)
        cursor = await self._db.execute(
            "INSERT INTO users (username, password_hash, role) VALUES (?, ?, ?)",
            (username, pw_hash, role),
        )
        await self._db.commit()
        return cursor.lastrowid

    async def set_user_role(self, user_id: int, role: str) -> bool:
        if role not in ("admin", "viewer"):
            raise ValueError(f"Invalid role: {role}")
        # Prevent demoting the last admin to viewer — same lockout risk as deletion.
        if role == "viewer":
            cur = await self._db.execute(
                "SELECT COALESCE(role, 'admin') FROM users WHERE id = ?", (user_id,)
            )
            target = await cur.fetchone()
            if target and target[0] == "admin":
                count_cur = await self._db.execute(
                    "SELECT COUNT(*) FROM users WHERE COALESCE(role, 'admin') = 'admin'"
                )
                count_row = await count_cur.fetchone()
                if count_row[0] <= 1:
                    raise ValueError("Cannot demote the last admin user")
        cursor = await self._db.execute(
            "UPDATE users SET role = ?, updated_at = datetime('now') WHERE id = ?",
            (role, user_id),
        )
        await self._db.commit()
        return cursor.rowcount > 0

    async def delete_user(self, user_id: int) -> bool:
        # Prevent deleting the last admin
        cursor = await self._db.execute(
            "SELECT COUNT(*) FROM users WHERE COALESCE(role, 'admin') = 'admin'"
        )
        row = await cursor.fetchone()
        admin_count = row[0]

        # Check if target is an admin and resolve username for session invalidation
        cursor2 = await self._db.execute(
            "SELECT COALESCE(role, 'admin'), username FROM users WHERE id = ?", (user_id,)
        )
        target = await cursor2.fetchone()
        if not target:
            return False
        if target[0] == "admin" and admin_count <= 1:
            raise ValueError("Cannot delete the last admin user")

        cursor3 = await self._db.execute("DELETE FROM users WHERE id = ?", (user_id,))
        await self._db.commit()
        if cursor3.rowcount > 0:
            # Invalidate all active sessions belonging to the deleted user
            # (defense-in-depth alongside ON DELETE CASCADE).
            await self._db.execute("DELETE FROM sessions WHERE user_id = ?", (user_id,))
            await self._db.commit()
            return True
        return False

    # --- API keys ---

    async def create_api_key(
        self,
        name: str,
        scopes: list[str] | None = None,
    ) -> tuple[dict, str]:
        """Create an API key and return (metadata, plaintext_key_once)."""
        key_id = secrets.token_hex(8)
        plaintext_key = f"dc_live_{secrets.token_urlsafe(32)}"
        key_hash = hashlib.sha256(plaintext_key.encode("utf-8")).hexdigest()
        key_prefix = plaintext_key[:8]
        now = datetime.now(timezone.utc).isoformat()
        normalized_scopes = _normalize_api_key_scopes(scopes)
        scopes_json = json.dumps(normalized_scopes, separators=(",", ":"), ensure_ascii=True)

        await self._db.execute(
            "INSERT INTO api_keys (id, name, key_prefix, key_hash, scopes_json, created_at) "
            "VALUES (?, ?, ?, ?, ?, ?)",
            (key_id, name.strip(), key_prefix, key_hash, scopes_json, now),
        )
        await self._db.commit()

        return {
            "id": key_id,
            "name": name.strip(),
            "key_prefix": key_prefix,
            "scopes": normalized_scopes,
            "created_at": now,
            "revoked_at": None,
            "last_used_at": None,
        }, plaintext_key

    async def list_api_keys(self) -> list[dict]:
        cursor = await self._db.execute(
            "SELECT id, name, key_prefix, scopes_json, created_at, revoked_at, last_used_at "
            "FROM api_keys ORDER BY created_at DESC"
        )
        rows = await cursor.fetchall()
        out: list[dict] = []
        for row in rows:
            scopes = _parse_scopes_json(row[3])
            out.append({
                "id": row[0],
                "name": row[1],
                "key_prefix": row[2],
                "scopes": scopes,
                "created_at": row[4],
                "revoked_at": row[5],
                "last_used_at": row[6],
            })
        return out

    async def revoke_api_key(self, key_id: str) -> bool:
        now = datetime.now(timezone.utc).isoformat()
        cursor = await self._db.execute(
            "UPDATE api_keys SET revoked_at = ? WHERE id = ? AND revoked_at IS NULL",
            (now, key_id),
        )
        await self._db.commit()
        return cursor.rowcount > 0

    async def validate_api_key(self, plaintext_key: str) -> dict | None:
        """Validate a plaintext API key and return metadata when valid."""
        if not plaintext_key:
            return None
        key_hash = hashlib.sha256(plaintext_key.encode("utf-8")).hexdigest()
        cursor = await self._db.execute(
            "SELECT id, name, key_prefix, scopes_json, created_at, revoked_at "
            "FROM api_keys WHERE key_hash = ? AND revoked_at IS NULL",
            (key_hash,),
        )
        row = await cursor.fetchone()
        if not row:
            return None
        scopes = _parse_scopes_json(row[3])

        now = datetime.now(timezone.utc).isoformat()
        await self._db.execute(
            "UPDATE api_keys SET last_used_at = ? WHERE id = ?",
            (now, row[0]),
        )
        await self._db.commit()

        return {
            "id": row[0],
            "name": row[1],
            "key_prefix": row[2],
            "scopes": scopes,
            "created_at": row[4],
            "revoked_at": row[5],
            "last_used_at": now,
        }

    # --- Brute-force protection ---

    def _is_locked(self, user: dict) -> bool:
        if user["locked_until"] is None:
            return False
        locked_until = datetime.fromisoformat(user["locked_until"])
        if locked_until.tzinfo is None:
            locked_until = locked_until.replace(tzinfo=timezone.utc)
        return datetime.now(timezone.utc) < locked_until

    async def _record_failed_attempt(self, user: dict, ip: str) -> None:
        new_count = user["failed_attempts"] + 1
        locked_until = None
        if new_count >= _MAX_FAILED_ATTEMPTS:
            locked_until = (datetime.now(timezone.utc) + _LOCKOUT_DURATION).isoformat()
        await self._db.execute(
            "UPDATE users SET failed_attempts = ?, locked_until = ?, "
            "updated_at = datetime('now') WHERE id = ?",
            (new_count, locked_until, user["id"]),
        )
        await self._db.commit()
        event_type = "lockout_triggered" if locked_until else "login_failure"
        outcome = "locked" if locked_until else "failed"
        await self._log_auth_event(
            event_type, ip, user["username"], outcome,
            f"Attempt {new_count}/{_MAX_FAILED_ATTEMPTS}",
        )

    async def _clear_failed_attempts(self, user_id: int) -> None:
        await self._db.execute(
            "UPDATE users SET failed_attempts = 0, locked_until = NULL, "
            "updated_at = datetime('now') WHERE id = ?",
            (user_id,),
        )
        await self._db.commit()

    # --- Login ---

    async def login(
        self, username: str, password: str, ip: str, user_agent: str = ""
    ) -> tuple[str, str] | None:
        """Attempt login. Returns (session_token, csrf_token) or None on failure."""
        user = await self.get_user(username)
        if not user:
            await self._log_auth_event(
                "login_failure", ip, username, "failed", "User not found",
            )
            return None

        if self._is_locked(user):
            await self._log_auth_event(
                "login_failure", ip, username, "locked", "Account locked",
            )
            return None

        if not self.verify_password(password, user["password_hash"]):
            await self._record_failed_attempt(user, ip)
            return None

        # Success
        await self._clear_failed_attempts(user["id"])
        session_token = secrets.token_hex(32)  # 32 bytes = 64 hex chars
        csrf_token = secrets.token_hex(32)
        now = datetime.now(timezone.utc)
        expires_at = now + timedelta(seconds=self._session_ttl)

        await self._db.execute(
            "INSERT INTO sessions (token, user_id, csrf_token, created_at, "
            "last_active, expires_at, ip_address, user_agent) "
            "VALUES (?, ?, ?, ?, ?, ?, ?, ?)",
            (session_token, user["id"], csrf_token, now.isoformat(),
             now.isoformat(), expires_at.isoformat(), ip, user_agent),
        )
        await self._db.commit()
        await self._log_auth_event("login_success", ip, username, "success", None)
        return session_token, csrf_token

    # --- Session validation ---

    async def validate_session(self, token: str) -> dict | None:
        """Return session dict if valid, or None. Updates last_active (sliding window)."""
        cursor = await self._db.execute(
            "SELECT s.token, s.user_id, s.csrf_token, s.expires_at, "
            "s.last_active, u.username, u.role "
            "FROM sessions s JOIN users u ON s.user_id = u.id "
            "WHERE s.token = ?",
            (token,),
        )
        row = await cursor.fetchone()
        if not row:
            return None

        expires_at = datetime.fromisoformat(row[3])
        if expires_at.tzinfo is None:
            expires_at = expires_at.replace(tzinfo=timezone.utc)
        now = datetime.now(timezone.utc)

        if now > expires_at:
            await self._db.execute("DELETE FROM sessions WHERE token = ?", (token,))
            await self._db.commit()
            await self._log_auth_event(
                "session_expired", None, row[5], "expired", None,
            )
            return None

        # Sliding window: extend expiry on each valid access
        new_expires = now + timedelta(seconds=self._session_ttl)
        await self._db.execute(
            "UPDATE sessions SET last_active = ?, expires_at = ? WHERE token = ?",
            (now.isoformat(), new_expires.isoformat(), token),
        )
        await self._db.commit()

        return {
            "token": row[0], "user_id": row[1], "csrf_token": row[2],
            "expires_at": new_expires.isoformat(), "username": row[5], "role": row[6],
        }

    # --- Logout ---

    async def logout(self, token: str, ip: str) -> None:
        cursor = await self._db.execute(
            "SELECT u.username FROM sessions s JOIN users u ON s.user_id = u.id "
            "WHERE s.token = ?",
            (token,),
        )
        row = await cursor.fetchone()
        username = row[0] if row else "unknown"
        await self._db.execute("DELETE FROM sessions WHERE token = ?", (token,))
        await self._db.commit()
        await self._log_auth_event("logout", ip, username, "success", None)

    # --- Session cleanup (call periodically) ---

    async def cleanup_expired_sessions(self) -> int:
        now = datetime.now(timezone.utc).isoformat()
        cursor = await self._db.execute(
            "DELETE FROM sessions WHERE expires_at < ?", (now,),
        )
        await self._db.commit()
        return cursor.rowcount

    # --- Auth audit log ---

    async def _log_auth_event(
        self, event_type: str, ip: str | None, username: str | None,
        outcome: str, detail: str | None,
    ) -> None:
        await self._db.execute(
            "INSERT INTO auth_log (timestamp, event_type, ip_address, "
            "username, outcome, detail) VALUES (?, ?, ?, ?, ?, ?)",
            (datetime.now(timezone.utc).isoformat(), event_type, ip,
             username, outcome, detail),
        )
        await self._db.commit()

    # --- Auth log retention (90 days) ---

    async def cleanup_old_auth_logs(self, batch_size: int = 500) -> int:
        """Delete auth log entries older than 90 days in batches.

        Batching prevents long write locks on SQLite when the table is large.
        """
        cutoff = (datetime.now(timezone.utc) - timedelta(days=90)).isoformat()
        total_deleted = 0
        while True:
            cursor = await self._db.execute(
                "DELETE FROM auth_log WHERE rowid IN "
                "(SELECT rowid FROM auth_log WHERE timestamp < ? LIMIT ?)",
                (cutoff, batch_size),
            )
            await self._db.commit()
            deleted = cursor.rowcount
            total_deleted += deleted
            if deleted < batch_size:
                break
        return total_deleted
