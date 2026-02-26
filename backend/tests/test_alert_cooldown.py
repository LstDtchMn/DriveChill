"""Tests for alert cooldown & dedup logic."""

from __future__ import annotations

from datetime import datetime, timedelta

from app.models.sensors import SensorReading, SensorType
from app.services.alert_service import AlertRule, AlertService


def _make_reading(sensor_id: str, value: float) -> SensorReading:
    return SensorReading(
        id=sensor_id,
        name=sensor_id,
        sensor_type=SensorType.CPU_TEMP,
        value=value,
        unit="°C",
    )


def _make_rule(rule_id: str = "r1", sensor_id: str = "cpu_0",
               threshold: float = 80.0, cooldown: int = 300) -> AlertRule:
    return AlertRule(
        id=rule_id, sensor_id=sensor_id, threshold=threshold,
        cooldown_seconds=cooldown,
    )


class TestAlertCooldown:

    def test_first_crossing_fires(self) -> None:
        """Alert fires the first time threshold is exceeded."""
        svc = AlertService()
        import asyncio
        asyncio.run(svc.add_rule(_make_rule()))
        events = svc.check([_make_reading("cpu_0", 90)])
        assert len(events) == 1
        assert events[0].actual_value == 90

    def test_dedup_while_active(self) -> None:
        """No duplicate alert while condition persists continuously."""
        svc = AlertService()
        import asyncio
        asyncio.run(svc.add_rule(_make_rule()))
        events1 = svc.check([_make_reading("cpu_0", 90)])
        assert len(events1) == 1
        # Second check while still above threshold — no new event
        events2 = svc.check([_make_reading("cpu_0", 92)])
        assert len(events2) == 0

    def test_cooldown_suppresses_rapid_refire(self) -> None:
        """After alert clears and re-triggers within cooldown, event is suppressed."""
        svc = AlertService()
        import asyncio
        asyncio.run(svc.add_rule(_make_rule(cooldown=300)))

        # First trigger
        events1 = svc.check([_make_reading("cpu_0", 90)])
        assert len(events1) == 1

        # Condition clears
        svc.check([_make_reading("cpu_0", 70)])

        # Re-trigger within cooldown window — should be suppressed
        events2 = svc.check([_make_reading("cpu_0", 90)])
        assert len(events2) == 0

    def test_fires_after_cooldown_expires(self) -> None:
        """Alert fires again after cooldown period has elapsed."""
        svc = AlertService()
        import asyncio
        asyncio.run(svc.add_rule(_make_rule(cooldown=60)))

        # First trigger
        events1 = svc.check([_make_reading("cpu_0", 90)])
        assert len(events1) == 1

        # Simulate cooldown expiry by backdating _last_triggered
        svc._last_triggered["r1"] = datetime.now() - timedelta(seconds=120)

        # Condition clears then re-triggers
        svc.check([_make_reading("cpu_0", 70)])
        events2 = svc.check([_make_reading("cpu_0", 90)])
        assert len(events2) == 1

    def test_cooldown_resets_on_rule_removal(self) -> None:
        """Removing and re-adding a rule clears its cooldown state."""
        svc = AlertService()
        import asyncio
        asyncio.run(svc.add_rule(_make_rule(cooldown=9999)))

        svc.check([_make_reading("cpu_0", 90)])
        assert "r1" in svc._last_triggered

        asyncio.run(svc.remove_rule("r1"))
        assert "r1" not in svc._last_triggered

        # Re-add and trigger — should fire immediately (no cooldown memory)
        asyncio.run(svc.add_rule(_make_rule(cooldown=9999)))
        svc.check([_make_reading("cpu_0", 70)])  # clear first
        events = svc.check([_make_reading("cpu_0", 90)])
        assert len(events) == 1

    def test_per_rule_independent_cooldowns(self) -> None:
        """Cooldown for one rule does not affect another rule."""
        svc = AlertService()
        import asyncio
        asyncio.run(svc.add_rule(_make_rule("r1", "cpu_0", 80, cooldown=9999)))
        asyncio.run(svc.add_rule(_make_rule("r2", "cpu_0", 85, cooldown=0)))

        # Both fire initially
        events1 = svc.check([_make_reading("cpu_0", 90)])
        assert len(events1) == 2

        # Clear both
        svc.check([_make_reading("cpu_0", 70)])

        # Re-trigger: r1 suppressed by cooldown, r2 fires (cooldown=0)
        events2 = svc.check([_make_reading("cpu_0", 90)])
        assert len(events2) == 1
        assert events2[0].rule_id == "r2"

    def test_disabled_rule_does_not_fire(self) -> None:
        """Disabled rules are skipped entirely."""
        svc = AlertService()
        rule = _make_rule()
        rule.enabled = False
        import asyncio
        asyncio.run(svc.add_rule(rule))
        events = svc.check([_make_reading("cpu_0", 90)])
        assert len(events) == 0

    def test_below_threshold_no_alert(self) -> None:
        """No alert when reading is below threshold."""
        svc = AlertService()
        import asyncio
        asyncio.run(svc.add_rule(_make_rule(threshold=80)))
        events = svc.check([_make_reading("cpu_0", 75)])
        assert len(events) == 0

    def test_direction_below_fires_when_cold(self) -> None:
        """direction='below' fires when reading drops to/below threshold."""
        svc = AlertService()
        rule = _make_rule(threshold=30.0)
        rule.direction = "below"
        import asyncio
        asyncio.run(svc.add_rule(rule))
        events = svc.check([_make_reading("cpu_0", 25)])
        assert len(events) == 1
        assert "dropped to" in events[0].message

    def test_direction_below_no_fire_when_hot(self) -> None:
        """direction='below' does NOT fire when reading is above threshold."""
        svc = AlertService()
        rule = _make_rule(threshold=30.0)
        rule.direction = "below"
        import asyncio
        asyncio.run(svc.add_rule(rule))
        events = svc.check([_make_reading("cpu_0", 50)])
        assert len(events) == 0

    def test_direction_above_default(self) -> None:
        """direction='above' (default) fires when reading exceeds threshold."""
        svc = AlertService()
        import asyncio
        asyncio.run(svc.add_rule(_make_rule(threshold=80)))
        events = svc.check([_make_reading("cpu_0", 85)])
        assert len(events) == 1
        assert "reached" in events[0].message
