from __future__ import annotations

import asyncio
import json
import logging
from datetime import datetime, timezone
from typing import TYPE_CHECKING, Any, Callable, Awaitable

import aiosqlite
from typing import Literal
from pydantic import BaseModel, Field

from app.models.sensors import SensorReading, SensorType
from app.services import prom_metrics

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
    from app.services.notification_channel_service import NotificationChannelService

TEMP_SENSOR_TYPES = {SensorType.CPU_TEMP, SensorType.GPU_TEMP, SensorType.HDD_TEMP, SensorType.CASE_TEMP}


class AlertAction(BaseModel):
    """Optional action payload attached to an alert rule."""
    type: str = "switch_profile"  # currently only "switch_profile"
    profile_id: str = ""
    revert_after_clear: bool = True


class AlertRule(BaseModel):
    id: str = Field(max_length=128)
    sensor_id: str = Field(max_length=128)
    threshold: float  # Temperature threshold in °C
    name: str = Field(default="", max_length=256)
    direction: Literal["above", "below"] = "above"
    cooldown_seconds: int = Field(default=300, ge=0, le=86400)
    enabled: bool = True
    action: AlertAction | None = None


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
        channel_svc: NotificationChannelService | None = None,
    ) -> None:
        self._db = db
        self._push_svc = push_notification_service
        self._email_svc = email_svc
        self._channel_svc = channel_svc
        self._rules: list[AlertRule] = []
        self._events: list[AlertEvent] = []
        self._active_alerts: set[str] = set()  # rule IDs currently triggered
        self._last_triggered: dict[str, datetime] = {}  # per-rule cooldown tracking
        self._max_events = 500
        self._pending_tasks: set[asyncio.Task] = set()  # keeps fire-and-forget tasks alive

        # Profile switching state
        self._activate_profile_fn: Callable[[str], Awaitable[None]] | None = None
        self._pre_alert_profile_id: str | None = None  # profile before first alert switch
        self._action_fired_order: list[str] = []  # rule IDs in firing order (oldest first)
        self._suppress_revert: bool = False  # True if any fired rule had revert_after_clear=False

    def set_activate_profile_fn(self, fn: Callable[[str], Awaitable[None]]) -> None:
        """Set the callback used to activate a profile by ID."""
        self._activate_profile_fn = fn

    async def load_rules(self) -> None:
        """Load alert rules from the database on startup."""
        if not self._db:
            return
        cursor = await self._db.execute(
            "SELECT id, sensor_id, threshold, direction, enabled, "
            "cooldown_seconds, name, action_json FROM alert_rules"
        )
        rows = await cursor.fetchall()
        self._rules = []
        for row in rows:
            action = None
            if row[7]:
                try:
                    action = AlertAction(**json.loads(row[7]))
                except Exception:
                    logger.warning("Invalid action_json for rule %s, ignoring", row[0])
            self._rules.append(AlertRule(
                id=row[0], sensor_id=row[1], threshold=row[2],
                direction=row[3], enabled=bool(row[4]),
                cooldown_seconds=row[5], name=row[6] or "",
                action=action,
            ))
        logger.info("Loaded %d alert rule(s) from database", len(self._rules))

    async def _persist_rule(self, rule: AlertRule) -> None:
        if not self._db:
            return
        action_json = json.dumps(rule.action.model_dump()) if rule.action else None
        await self._db.execute(
            "INSERT OR REPLACE INTO alert_rules "
            "(id, sensor_id, threshold, direction, enabled, cooldown_seconds, name, action_json) "
            "VALUES (?, ?, ?, ?, ?, ?, ?, ?)",
            (rule.id, rule.sensor_id, rule.threshold, rule.direction,
             int(rule.enabled), rule.cooldown_seconds, rule.name, action_json),
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
        await self._persist_rule(rule)
        # Only mutate in-memory state after the DB write succeeds.
        self._rules = [r for r in self._rules if r.id != rule.id]
        # Clear stale tracking so a replaced rule can fire immediately.
        self._active_alerts.discard(rule.id)
        self._last_triggered.pop(rule.id, None)
        if rule.id in self._action_fired_order:
            self._action_fired_order.remove(rule.id)
        self._rules.append(rule)

    async def remove_rule(self, rule_id: str) -> bool:
        """Remove a rule by ID. Returns True if the rule existed, False otherwise."""
        # Look up the rule before removing so we can compute its suppress preference.
        deleted_rule = next((r for r in self._rules if r.id == rule_id), None)
        found = deleted_rule is not None
        # Write to DB first so that if it fails, in-memory state stays consistent.
        await self._delete_rule(rule_id)
        if found and deleted_rule is not None:
            self._rules = [r for r in self._rules if r.id != rule_id]
            self._active_alerts.discard(rule_id)
            self._last_triggered.pop(rule_id, None)
            if rule_id in self._action_fired_order:
                # Treat the deletion as a single-rule batch clear so _suppress_revert
                # is recomputed correctly from remaining active rules.
                self._handle_batch_action_clear([deleted_rule])
        return found

    def set_rules(self, rules: list[AlertRule]) -> None:
        self._rules = list(rules)

    def set_pre_alert_profile(self, profile_id: str) -> None:
        """Record the current profile ID so we can revert after alert-driven switches clear."""
        self._pre_alert_profile_id = profile_id

    def _spawn(self, coro) -> None:
        """Create a fire-and-forget task anchored in _pending_tasks to prevent GC."""
        t = asyncio.create_task(coro)
        self._pending_tasks.add(t)
        t.add_done_callback(self._pending_tasks.discard)
        t.add_done_callback(_log_task_exception)

    def _handle_action_fire(self, rule: AlertRule) -> None:
        """Called when an action rule fires. Most recently fired wins."""
        action = rule.action
        if action is None or action.type != "switch_profile" or not action.profile_id:
            return
        assert action is not None  # help Pylance narrow past the compound or-guard

        # Track firing order
        if rule.id in self._action_fired_order:
            self._action_fired_order.remove(rule.id)
        self._action_fired_order.append(rule.id)
        # Recompute suppress from the full active set (not monotonic accumulation).
        self._suppress_revert = any(
            self._rule_suppresses_revert(r)
            for r in self._rules
            if r.id in self._action_fired_order
        )

        if self._activate_profile_fn:
            logger.info("Alert rule %s firing profile switch to %s", rule.id, action.profile_id)
            self._spawn(self._activate_profile_fn(action.profile_id))

    @staticmethod
    def _rule_suppresses_revert(rule: AlertRule) -> bool:
        action = rule.action
        return action is not None and not action.revert_after_clear

    def _handle_batch_action_clear(self, cleared_rules: list[AlertRule]) -> None:
        """Process a batch of simultaneously-clearing action rules in one step.

        Computes suppress from the whole clearing batch plus any remaining active rules,
        then re-evaluates once.  Processing rules one-by-one would let a historical
        no-revert rule's flag leak into later separate waves.
        """
        batch_suppress = any(self._rule_suppresses_revert(r) for r in cleared_rules)
        for rule in cleared_rules:
            if rule.id in self._action_fired_order:
                self._action_fired_order.remove(rule.id)
        remaining_suppress = any(
            self._rule_suppresses_revert(r)
            for r in self._rules
            if r.id in self._action_fired_order
        )
        self._suppress_revert = remaining_suppress or batch_suppress
        self._handle_action_reeval()

    def _handle_action_reeval(self) -> None:
        """Re-evaluate which profile should be active after an action rule clears."""
        # Find the most recently fired action rule that's still active
        for rid in reversed(self._action_fired_order):
            rule = next((r for r in self._rules if r.id == rid), None)
            if rule and rule.action and rule.action.type == "switch_profile" and rule.action.profile_id:
                if self._activate_profile_fn:
                    logger.info("Re-evaluating: switching to profile %s (rule %s still active)",
                                rule.action.profile_id, rid)
                    self._spawn(self._activate_profile_fn(rule.action.profile_id))
                return

        # No action rules active — check if we should revert
        if self._pre_alert_profile_id:
            if self._suppress_revert:
                # At least one fired rule had revert_after_clear=False — honour it.
                logger.info(
                    "All alert actions cleared but revert suppressed by revert_after_clear=False"
                )
                self._suppress_revert = False
                self._pre_alert_profile_id = None
            elif self._activate_profile_fn:
                logger.info("All alert actions cleared, reverting to profile %s",
                            self._pre_alert_profile_id)
                self._spawn(self._activate_profile_fn(self._pre_alert_profile_id))
                self._pre_alert_profile_id = None

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
        # Collect all simultaneously-clearing action rules for batch processing
        cleared_action_rules: list[AlertRule] = []

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
                    prom_metrics.alert_events_total.labels(rule.id, rule.direction).inc()
                    # Trim old events
                    if len(self._events) > self._max_events:
                        self._events = self._events[-self._max_events:]
                    # Fire-and-forget: push notification
                    if self._push_svc:
                        self._spawn(
                            self._push_svc.send_alert(
                                reading.name, reading.value, rule.threshold
                            )
                        )
                    # Fire-and-forget: email notification
                    if self._email_svc:
                        self._spawn(
                            self._email_svc.send_alert(
                                reading.name, reading.value, rule.threshold
                            )
                        )
                    # Fire-and-forget: notification channels (ntfy, Discord, Slack, etc.)
                    if self._channel_svc:
                        self._spawn(
                            self._channel_svc.send_alert_all(
                                reading.name, reading.value, rule.threshold
                            )
                        )
                    # Profile switching action
                    self._handle_action_fire(rule)
            else:
                # Clear alert when condition no longer met
                was_active = rule.id in self._active_alerts
                self._active_alerts.discard(rule.id)
                if was_active and rule.action and rule.action.type == "switch_profile":
                    cleared_action_rules.append(rule)

        # Process all simultaneously-clearing action rules as a single batch so that
        # _suppress_revert is computed from the whole clearing set, not rule-by-rule.
        if cleared_action_rules:
            self._handle_batch_action_clear(cleared_action_rules)

        return new_events

    def inject_event(self, event: AlertEvent) -> None:
        """Inject a synthetic event (e.g. from SmartTrendService) with full notification dispatch.

        Avoids direct access to private fields from external services.
        """
        self._events.append(event)
        if len(self._events) > self._max_events:
            self._events = self._events[-self._max_events :]  # type: ignore[index]
        prom_metrics.alert_events_total.labels(event.rule_id, "above").inc()
        if self._push_svc:
            self._spawn(
                self._push_svc.send_alert(event.sensor_name, event.actual_value, event.threshold)
            )
        if self._email_svc:
            self._spawn(
                self._email_svc.send_alert(event.sensor_name, event.actual_value, event.threshold)
            )
        if self._channel_svc:
            self._spawn(
                self._channel_svc.send_alert_all(event.sensor_name, event.actual_value, event.threshold)
            )

    def clear_events(self) -> None:
        self._events.clear()
        self._active_alerts.clear()
        self._suppress_revert = False
