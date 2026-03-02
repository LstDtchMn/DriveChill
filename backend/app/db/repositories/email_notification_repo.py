"""SQLite repository for email notification settings (singleton row, id=1)."""

from __future__ import annotations

import json
import logging
from datetime import datetime, timezone

import aiosqlite

from app.utils.credential_encryption import decrypt, encrypt, is_encrypted

logger = logging.getLogger(__name__)


class EmailNotificationRepo:
    def __init__(self, db: aiosqlite.Connection, secret_key: str = "") -> None:
        self._db = db
        self._secret_key = secret_key

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
        """Update email settings. Uses a static UPDATE to avoid dynamic SQL.

        Reads the current row first, merges the provided fields, then writes
        all columns explicitly — eliminates any risk of column-name injection.
        New smtp_password values are encrypted before storage when a secret_key
        is configured.
        Returns updated settings (sans password).
        """
        cursor = await self._db.execute(
            "SELECT enabled, smtp_host, smtp_port, smtp_username, smtp_password, "
            "sender_address, recipient_list, use_tls, use_ssl "
            "FROM email_notification_settings WHERE id = 1"
        )
        row = await cursor.fetchone()
        if row is None:
            return await self.get()

        enabled, smtp_host, smtp_port, smtp_username, smtp_password, \
            sender_address, recipient_list, use_tls, use_ssl = row

        if "enabled" in fields:
            enabled = int(bool(fields["enabled"]))
        if "smtp_host" in fields:
            smtp_host = fields["smtp_host"]
        if "smtp_port" in fields:
            smtp_port = int(fields["smtp_port"])  # type: ignore[arg-type]
        if "smtp_username" in fields:
            smtp_username = fields["smtp_username"]
        if "smtp_password" in fields:
            raw = str(fields["smtp_password"])
            # Encrypt the new password before storing it
            smtp_password = encrypt(raw, self._secret_key) if raw else raw
        if "sender_address" in fields:
            sender_address = fields["sender_address"]
        if "recipient_list" in fields:
            rl = fields["recipient_list"]
            recipient_list = json.dumps(rl) if isinstance(rl, list) else str(rl)
        if "use_tls" in fields:
            use_tls = int(bool(fields["use_tls"]))
        if "use_ssl" in fields:
            use_ssl = int(bool(fields["use_ssl"]))

        updated_at = datetime.now(timezone.utc).isoformat()
        await self._db.execute(
            "UPDATE email_notification_settings SET "
            "enabled=?, smtp_host=?, smtp_port=?, smtp_username=?, smtp_password=?, "
            "sender_address=?, recipient_list=?, use_tls=?, use_ssl=?, updated_at=? "
            "WHERE id = 1",
            (enabled, smtp_host, smtp_port, smtp_username, smtp_password,
             sender_address, recipient_list, use_tls, use_ssl, updated_at),
        )
        await self._db.commit()
        return await self.get()

    async def get_password(self) -> str:
        """Return the decrypted smtp_password (for actual sending).

        Auto-migrates legacy plaintext rows: on first read after
        DRIVECHILL_SECRET_KEY is configured, re-encrypts in place.
        """
        cursor = await self._db.execute(
            "SELECT smtp_password FROM email_notification_settings WHERE id = 1"
        )
        row = await cursor.fetchone()
        if row is None:
            return ""

        stored = row[0] or ""
        if not stored:
            return ""

        if is_encrypted(stored):
            return decrypt(stored, self._secret_key)

        # Legacy plaintext row — migrate to encrypted form if key is available
        if self._secret_key:
            encrypted = encrypt(stored, self._secret_key)
            await self._db.execute(
                "UPDATE email_notification_settings SET smtp_password=? WHERE id = 1",
                (encrypted,),
            )
            await self._db.commit()
            logger.info("Migrated SMTP password from plaintext to encrypted storage")

        return stored
