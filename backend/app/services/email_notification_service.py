"""Email notification service for DriveChill alert delivery."""

from __future__ import annotations

import logging
from datetime import datetime, timezone
from email.message import EmailMessage

try:
    import aiosmtplib
except ImportError:
    aiosmtplib = None  # type: ignore

from app.db.repositories.email_notification_repo import EmailNotificationRepo

logger = logging.getLogger(__name__)


class EmailNotificationService:
    def __init__(self, repo: EmailNotificationRepo) -> None:
        self._repo = repo

    async def send_alert(
        self,
        sensor_name: str,
        value: float,
        threshold: float,
        unit: str = "\u00b0C",
    ) -> int:
        """Send an alert email to all configured recipients.

        Returns the number of messages successfully sent (0 or 1 — this
        implementation sends one SMTP transaction addressed to all recipients).
        """
        timestamp = datetime.now(timezone.utc).strftime("%Y-%m-%d %H:%M:%S UTC")
        subject = f"DriveChill Alert: {sensor_name}"
        body = (
            "DriveChill has detected a temperature alert:\n"
            "\n"
            f"Sensor:    {sensor_name}\n"
            f"Value:     {value:.1f}{unit}\n"
            f"Threshold: {threshold:.1f}{unit}\n"
            f"Time:      {timestamp}\n"
            "\n"
            "This alert was sent by DriveChill."
        )
        return await self._send(subject, body)

    async def send_test(self) -> bool:
        """Send a test email to all configured recipients.

        Returns True when at least one message was sent successfully.
        """
        subject = "DriveChill Test Notification"
        body = (
            "This is a test notification from DriveChill. "
            "Your email settings are configured correctly."
        )
        sent = await self._send(subject, body)
        return sent > 0

    async def send_html_report(self, subject: str, html_body: str) -> int:
        """Public API for sending HTML reports (e.g. scheduled analytics)."""
        return await self._send_html(subject, html_body)

    async def _send_html(self, subject: str, html_body: str) -> int:
        """Send an HTML email to all configured recipients.

        Returns 1 on success, 0 on failure or when sending is skipped.
        """
        if aiosmtplib is None:
            logger.warning(
                "aiosmtplib is not installed; email notifications are disabled."
            )
            return 0

        cfg = await self._repo.get()

        if not cfg["enabled"]:
            logger.debug("Email notifications are disabled; skipping send.")
            return 0

        if not cfg["smtp_host"]:
            logger.warning("Email send skipped: smtp_host is not configured.")
            return 0

        recipients: list[str] = cfg["recipient_list"]
        if not recipients:
            logger.warning("Email send skipped: recipient_list is empty.")
            return 0

        password = await self._repo.get_password()

        try:
            async with aiosmtplib.SMTP(
                hostname=cfg["smtp_host"],
                port=cfg["smtp_port"],
                use_tls=cfg["use_ssl"],
                start_tls=cfg["use_tls"] and not cfg["use_ssl"],
            ) as smtp:
                if cfg["smtp_username"] and password:
                    await smtp.login(cfg["smtp_username"], password)

                msg = EmailMessage()
                msg["From"] = cfg["sender_address"]
                msg["To"] = ", ".join(recipients)
                msg["Subject"] = subject
                msg.set_content("This report requires an HTML-capable email client.")
                msg.add_alternative(html_body, subtype="html")
                await smtp.send_message(msg)

            logger.info(
                "HTML email sent: subject=%r recipients=%d", subject, len(recipients)
            )
            return 1

        except Exception:
            logger.exception(
                "Failed to send HTML email: subject=%r smtp_host=%r",
                subject,
                cfg["smtp_host"],
            )
            return 0

    async def _send(self, subject: str, body: str) -> int:
        """Internal: build and send a single SMTP message to all recipients.

        Returns 1 on success, 0 on failure or when sending is skipped.
        """
        if aiosmtplib is None:
            logger.warning(
                "aiosmtplib is not installed; email notifications are disabled. "
                "Install it with: pip install aiosmtplib"
            )
            return 0

        cfg = await self._repo.get()

        if not cfg["enabled"]:
            logger.debug("Email notifications are disabled; skipping send.")
            return 0

        if not cfg["smtp_host"]:
            logger.warning("Email send skipped: smtp_host is not configured.")
            return 0

        recipients: list[str] = cfg["recipient_list"]
        if not recipients:
            logger.warning("Email send skipped: recipient_list is empty.")
            return 0

        password = await self._repo.get_password()

        try:
            async with aiosmtplib.SMTP(
                hostname=cfg["smtp_host"],
                port=cfg["smtp_port"],
                use_tls=cfg["use_ssl"],
                start_tls=cfg["use_tls"] and not cfg["use_ssl"],
            ) as smtp:
                if cfg["smtp_username"] and password:
                    await smtp.login(cfg["smtp_username"], password)

                msg = EmailMessage()
                msg["From"] = cfg["sender_address"]
                msg["To"] = ", ".join(recipients)
                msg["Subject"] = subject
                msg.set_content(body)
                await smtp.send_message(msg)

            logger.info(
                "Email sent: subject=%r recipients=%d", subject, len(recipients)
            )
            return 1

        except Exception:
            logger.exception(
                "Failed to send email: subject=%r smtp_host=%r",
                subject,
                cfg["smtp_host"],
            )
            return 0
