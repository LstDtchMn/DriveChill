"""SQLite repository for email notification settings (singleton row, id=1)."""

from __future__ import annotations

import json
from datetime import datetime, timezone

import aiosqlite

_ALLOWED_FIELDS = {
    "enabled",
    "smtp_host",
    "smtp_port",
    "smtp_username",
    "smtp_password",
    "sender_address",
    "recipient_list",
    "use_tls",
    "use_ssl",
}


class EmailNotificationRepo:
    def __init__(self, db: aiosqlite.Connection) -> None:
        self._db = db

    @staticmethod
    def _row_to_public_dict(row: tuple) -> dict:
        """Convert a DB row to a public dict — never exposes smtp_password."""
        # columns: id, enabled, smtp_host, smtp_port, smtp_username,
        #          smtp_password, sender_address, recipient_list,
        #          use_tls, use_ssl, updated_at
        raw_recipients = row[7]
        try:
            recipients: list[str] = json.loads(raw_recipients) if raw_recipients else []
        except (ValueError, TypeError):
            recipients = []

        return {
            "enabled": bool(row[1]),
            "smtp_host": row[2] or "",
            "smtp_port": int(row[3]),
            "smtp_username": row[4] or "",
            "has_password": bool(row[5]),
            "sender_address": row[6] or "",
            "recipient_list": recipients,
            "use_tls": bool(row[8]),
            "use_ssl": bool(row[9]),
            "updated_at": row[10],
        }

    async def get(self) -> dict:
        """Return email settings for the singleton row (id=1).

        Never includes smtp_password; includes has_password bool instead.
        """
        cursor = await self._db.execute(
            "SELECT id, enabled, smtp_host, smtp_port, smtp_username, "
            "smtp_password, sender_address, recipient_list, "
            "use_tls, use_ssl, updated_at "
            "FROM email_notification_settings WHERE id = 1"
        )
        row = await cursor.fetchone()
        if row is None:
            # Seed the singleton row if somehow missing
            await self._db.execute(
                "INSERT OR IGNORE INTO email_notification_settings (id) VALUES (1)"
            )
            await self._db.commit()
            cursor = await self._db.execute(
                "SELECT id, enabled, smtp_host, smtp_port, smtp_username, "
                "smtp_password, sender_address, recipient_list, "
                "use_tls, use_ssl, updated_at "
                "FROM email_notification_settings WHERE id = 1"
            )
            row = await cursor.fetchone()
        return self._row_to_public_dict(row)

    async def update(self, **fields: object) -> dict:
        """Update only the provided fields. Returns updated settings (sans password)."""
        updates: dict[str, object] = {}
        for key, value in fields.items():
            if key not in _ALLOWED_FIELDS:
                continue

            if key == "enabled":
                updates["enabled"] = int(bool(value))
            elif key == "smtp_port":
                updates["smtp_port"] = int(value)  # type: ignore[arg-type]
            elif key == "use_tls":
                updates["use_tls"] = int(bool(value))
            elif key == "use_ssl":
                updates["use_ssl"] = int(bool(value))
            elif key == "recipient_list":
                if isinstance(value, list):
                    updates["recipient_list"] = json.dumps(value)
                else:
                    updates["recipient_list"] = str(value)
            else:
                updates[key] = value

        if not updates:
            return await self.get()

        updates["updated_at"] = datetime.now(timezone.utc).isoformat()
        assignments = ", ".join(f"{k} = ?" for k in updates.keys())
        values = list(updates.values())
        await self._db.execute(
            f"UPDATE email_notification_settings SET {assignments} WHERE id = 1",
            tuple(values),
        )
        await self._db.commit()
        return await self.get()

    async def get_password(self) -> str:
        """Return the raw smtp_password value (for actual sending)."""
        cursor = await self._db.execute(
            "SELECT smtp_password FROM email_notification_settings WHERE id = 1"
        )
        row = await cursor.fetchone()
        if row is None:
            return ""
        return row[0] or ""
