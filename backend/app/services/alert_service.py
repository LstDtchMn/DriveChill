from __future__ import annotations

import asyncio
import logging
from datetime import datetime, timezone
from typing import TYPE_CHECKING

import aiosqlite
from pydantic import BaseModel

from app.models.sensors import SensorReading, SensorType

logger = logging.getLogger(__name__)


def _log_task_exception(task: asyncio.Task) -> None:
    """Done-callback for fire-and-forget tasks: log exceptions instead of losing them."""
    if task.cancelled():
        return
    exc = task.exception()
    if exc is not None:
        logger.warning("Background alert delivery failed: %s", exc)

if TYPE_CHECKING:
    from app.services.push_notification_service import PushNotificationService
    from app.services.email_notification_service import EmailNotificationService

logger = logging.getLogger(__name__)

TEMP_SENSOR_TYPES = {SensorType.CPU_TEMP, SensorType.GPU_TEMP, SensorType.HDD_TEMP, SensorType.CASE_TEMP}


class AlertRule(BaseModel):
    id: str
    sensor_id: str
    threshold: float  # Temperature threshold in °C
    name: str = ""
    direction: str = "above"
    cooldown_seconds: int = 300
    enabled: bool = True


class AlertEvent(BaseModel):
    rule_id: str
    sensor_id: str
    sensor_name: str
    threshold: float
    actual_value: float
    timestamp: datetime
    message: str


class AlertService:
    """Monitors sensor readings and triggers alerts when thresholds are exceeded."""

    def __init__(
        self,
        db: aiosqlite.Connection | None = None,
        push_notification_service: PushNotificationService | None = None,
        email_svc: EmailNotificationService | None = None,
    ) -> None:
        self._db = db
        self._push_svc = push_notification_service
        self._email_svc = email_svc
        self._rules: list[AlertRule] = []
        self._events: list[AlertEvent] = []
        self._active_alerts: set[str] = set()  # rule IDs currently triggered
        self._last_triggered: dict[str, datetime] = {}  # per-rule cooldown tracking
        self._max_events = 500

    async def load_rules(self) -> None:
        """Load alert rules from the database on startup."""
        if not self._db:
            return
        cursor = await self._db.execute(
            "SELECT id, sensor_id, threshold, direction, enabled, "
            "cooldown_seconds, name FROM alert_rules"
        )
        rows = await cursor.fetchall()
        self._rules = [
            AlertRule(
                id=row[0], sensor_id=row[1], threshold=row[2],
                direction=row[3], enabled=bool(row[4]),
                cooldown_seconds=row[5], name=row[6] or "",
            )
            for row in rows
        ]
        logger.info("Loaded %d alert rule(s) from database", len(self._rules))

    async def _persist_rule(self, rule: AlertRule) -> None:
        if not self._db:
            return
        await self._db.execute(
            "INSERT OR REPLACE INTO alert_rules "
            "(id, sensor_id, threshold, direction, enabled, cooldown_seconds, name) "
            "VALUES (?, ?, ?, ?, ?, ?, ?)",
            (rule.id, rule.sensor_id, rule.threshold, rule.direction,
             int(rule.enabled), rule.cooldown_seconds, rule.name),
        )
        await self._db.commit()

    async def _delete_rule(self, rule_id: str) -> None:
        if not self._db:
            return
        await self._db.execute("DELETE FROM alert_rules WHERE id = ?", (rule_id,))
        await self._db.commit()

    @property
    def rules(self) -> list[AlertRule]:
        return list(self._rules)

    @property
    def events(self) -> list[AlertEvent]:
        return list(self._events)

    @property
    def active_alerts(self) -> list[str]:
        return list(self._active_alerts)

    async def add_rule(self, rule: AlertRule) -> None:
        self._rules = [r for r in self._rules if r.id != rule.id]
        self._rules.append(rule)
        await self._persist_rule(rule)

    async def remove_rule(self, rule_id: str) -> None:
        self._rules = [r for r in self._rules if r.id != rule_id]
        self._active_alerts.discard(rule_id)
        self._last_triggered.pop(rule_id, None)
        await self._delete_rule(rule_id)

    def set_rules(self, rules: list[AlertRule]) -> None:
        self._rules = list(rules)

    def check(self, readings: list[SensorReading]) -> list[AlertEvent]:
        """Check readings against rules. Returns list of newly triggered alerts.

        Cooldown logic: after an alert fires, the same rule will not fire
        again until ``cooldown_seconds`` have elapsed AND the condition has
        cleared (temp dropped below threshold) and re-triggered.
        """
        from datetime import timedelta

        now = datetime.now(timezone.utc)
        sensor_map = {r.id: r for r in readings if r.sensor_type in TEMP_SENSOR_TYPES}
        new_events: list[AlertEvent] = []

        for rule in self._rules:
            if not rule.enabled:
                continue

            reading = sensor_map.get(rule.sensor_id)
            if not reading:
                continue

            # Evaluate threshold based on direction
            if rule.direction == "below":
                triggered = reading.value <= rule.threshold
            else:  # "above" (default)
                triggered = reading.value >= rule.threshold

            if triggered:
                if rule.id not in self._active_alerts:
                    self._active_alerts.add(rule.id)

                    # Cooldown: suppress event if fired too recently
                    last = self._last_triggered.get(rule.id)
                    if last and (now - last) < timedelta(seconds=rule.cooldown_seconds):
                        continue

                    self._last_triggered[rule.id] = now
                    direction_word = "dropped to" if rule.direction == "below" else "reached"
                    event = AlertEvent(
                        rule_id=rule.id,
                        sensor_id=reading.id,
                        sensor_name=reading.name,
                        threshold=rule.threshold,
                        actual_value=reading.value,
                        timestamp=now,
                        message=f"{reading.name} {direction_word} {reading.value}°C (threshold: {rule.threshold}°C)",
                    )
                    self._events.append(event)
                    new_events.append(event)
                    # Trim old events
                    if len(self._events) > self._max_events:
                        self._events = self._events[-self._max_events:]
                    # Fire-and-forget: push notification
                    if self._push_svc:
                        t = asyncio.create_task(
                            self._push_svc.send_alert(
                                reading.name, reading.value, rule.threshold
                            )
                        )
                        t.add_done_callback(_log_task_exception)
                    # Fire-and-forget: email notification
                    if self._email_svc:
                        t = asyncio.create_task(
                            self._email_svc.send_alert(
                                reading.name, reading.value, rule.threshold
                            )
                        )
                        t.add_done_callback(_log_task_exception)
            else:
                # Clear alert when condition no longer met
                self._active_alerts.discard(rule.id)

        return new_events

    def clear_events(self) -> None:
        self._events.clear()
        self._active_alerts.clear()
