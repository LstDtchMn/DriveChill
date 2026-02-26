"""Authentication service: login, logout, session management, brute-force protection, rate limiting."""

from __future__ import annotations

import logging
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
            "SELECT id, username, password_hash, locked_until, failed_attempts "
            "FROM users WHERE username = ?",
            (username,),
        )
        row = await cursor.fetchone()
        if not row:
            return None
        return {
            "id": row[0], "username": row[1], "password_hash": row[2],
            "locked_until": row[3], "failed_attempts": row[4],
        }

    async def user_exists(self) -> bool:
        cursor = await self._db.execute("SELECT COUNT(*) FROM users")
        row = await cursor.fetchone()
        return row[0] > 0

    async def create_user(self, username: str, password: str) -> int:
        pw_hash = self.hash_password(password)
        cursor = await self._db.execute(
            "INSERT INTO users (username, password_hash) VALUES (?, ?)",
            (username, pw_hash),
        )
        await self._db.commit()
        return cursor.lastrowid

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
            "s.last_active, u.username "
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
            "expires_at": new_expires.isoformat(), "username": row[5],
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
