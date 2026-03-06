"""Tests for alert-triggered profile switching (Phase 7)."""
from __future__ import annotations

import asyncio

import pytest

from app.services.alert_service import AlertAction, AlertRule, AlertService
from app.models.sensors import SensorReading, SensorType


def _reading(sensor_id: str, value: float, sensor_type: SensorType = SensorType.CPU_TEMP) -> SensorReading:
    return SensorReading(id=sensor_id, name=sensor_id, sensor_type=sensor_type, value=value, unit="°C")


def _rule(rule_id: str, sensor_id: str, threshold: float, action: AlertAction | None = None) -> AlertRule:
    return AlertRule(
        id=rule_id, sensor_id=sensor_id, threshold=threshold,
        name=f"rule {rule_id}", direction="above", cooldown_seconds=0,
        enabled=True, action=action,
    )


def _check_and_drain(svc: AlertService, readings: list[SensorReading]) -> list:
    """Run check() inside an event loop so _spawn() works, then drain tasks."""
    async def _inner():
        events = svc.check(readings)
        # Drain spawned tasks
        for task in list(svc._pending_tasks):
            await task
        return events
    return asyncio.run(_inner())


class TestAlertProfileSwitching:
    """Alert-triggered profile switching."""

    def setup_method(self):
        self.svc = AlertService()
        self.activated_profiles: list[str] = []

        async def mock_activate(profile_id: str) -> None:
            self.activated_profiles.append(profile_id)

        self.svc.set_activate_profile_fn(mock_activate)
        self.svc.set_pre_alert_profile("default_profile")

    def test_action_fires_profile_switch(self):
        """When an alert with a switch_profile action fires, it activates the target profile."""
        action = AlertAction(type="switch_profile", profile_id="perf_profile", revert_after_clear=True)
        rule = _rule("r1", "cpu0", 80.0, action=action)
        self.svc.set_rules([rule])

        events = _check_and_drain(self.svc, [_reading("cpu0", 85.0)])

        assert len(events) == 1
        assert "perf_profile" in self.activated_profiles

    def test_action_clears_and_reverts(self):
        """When the alert clears and revert_after_clear=True, reverts to pre-alert profile."""
        action = AlertAction(type="switch_profile", profile_id="perf_profile", revert_after_clear=True)
        rule = _rule("r1", "cpu0", 80.0, action=action)
        self.svc.set_rules([rule])

        # Fire
        _check_and_drain(self.svc, [_reading("cpu0", 85.0)])
        assert "perf_profile" in self.activated_profiles

        # Clear
        self.activated_profiles.clear()
        _check_and_drain(self.svc, [_reading("cpu0", 70.0)])
        assert "default_profile" in self.activated_profiles

    def test_most_recently_fired_wins(self):
        """When multiple action rules fire, the most recently fired profile wins."""
        action1 = AlertAction(type="switch_profile", profile_id="profile_A", revert_after_clear=True)
        action2 = AlertAction(type="switch_profile", profile_id="profile_B", revert_after_clear=True)
        rule1 = _rule("r1", "cpu0", 80.0, action=action1)
        rule2 = _rule("r2", "cpu0", 90.0, action=action2)
        self.svc.set_rules([rule1, rule2])

        # Fire rule1 at 85°C
        _check_and_drain(self.svc, [_reading("cpu0", 85.0)])
        assert self.activated_profiles[-1] == "profile_A"

        # Fire rule2 at 95°C (both now active, r2 fires second = most recent)
        _check_and_drain(self.svc, [_reading("cpu0", 95.0)])
        assert self.activated_profiles[-1] == "profile_B"

    def test_clear_one_action_switches_to_remaining(self):
        """Clearing one action rule switches to the remaining active action rule."""
        action1 = AlertAction(type="switch_profile", profile_id="profile_A", revert_after_clear=True)
        action2 = AlertAction(type="switch_profile", profile_id="profile_B", revert_after_clear=True)
        rule1 = _rule("r1", "cpu0", 80.0, action=action1)
        rule2 = _rule("r2", "cpu0", 90.0, action=action2)
        self.svc.set_rules([rule1, rule2])

        # Fire both
        _check_and_drain(self.svc, [_reading("cpu0", 95.0)])

        # Clear r2 (drops below 90 but still above 80)
        self.activated_profiles.clear()
        _check_and_drain(self.svc, [_reading("cpu0", 85.0)])
        # Should switch back to profile_A since r1 is still active
        assert "profile_A" in self.activated_profiles

    def test_clear_all_reverts_to_pre_alert(self):
        """Clearing all action rules reverts to the pre-alert profile."""
        action1 = AlertAction(type="switch_profile", profile_id="profile_A", revert_after_clear=True)
        action2 = AlertAction(type="switch_profile", profile_id="profile_B", revert_after_clear=True)
        rule1 = _rule("r1", "cpu0", 80.0, action=action1)
        rule2 = _rule("r2", "cpu0", 90.0, action=action2)
        self.svc.set_rules([rule1, rule2])

        # Fire both
        _check_and_drain(self.svc, [_reading("cpu0", 95.0)])

        # Clear both
        self.activated_profiles.clear()
        _check_and_drain(self.svc, [_reading("cpu0", 70.0)])
        assert "default_profile" in self.activated_profiles

    def test_no_action_rule_no_switch(self):
        """Rules without actions don't trigger profile switches."""
        rule = _rule("r1", "cpu0", 80.0, action=None)
        self.svc.set_rules([rule])

        _check_and_drain(self.svc, [_reading("cpu0", 85.0)])
        assert len(self.activated_profiles) == 0

    def test_action_without_profile_id_ignored(self):
        """Actions with empty profile_id are ignored."""
        action = AlertAction(type="switch_profile", profile_id="", revert_after_clear=True)
        rule = _rule("r1", "cpu0", 80.0, action=action)
        self.svc.set_rules([rule])

        _check_and_drain(self.svc, [_reading("cpu0", 85.0)])
        assert len(self.activated_profiles) == 0

    def test_no_revert_when_pre_alert_not_set(self):
        """No revert if pre-alert profile was never set."""
        svc = AlertService()
        activated: list[str] = []

        async def mock_activate(pid: str) -> None:
            activated.append(pid)

        svc.set_activate_profile_fn(mock_activate)
        # Do NOT call set_pre_alert_profile

        action = AlertAction(type="switch_profile", profile_id="perf", revert_after_clear=True)
        rule = _rule("r1", "cpu0", 80.0, action=action)
        svc.set_rules([rule])

        _check_and_drain(svc, [_reading("cpu0", 85.0)])

        activated.clear()
        _check_and_drain(svc, [_reading("cpu0", 70.0)])
        # Should not revert since no pre-alert profile was set
        assert len(activated) == 0


    def test_revert_after_clear_false_does_not_revert(self):
        """When revert_after_clear=False, clearing the alert does NOT revert to the pre-alert profile."""
        action = AlertAction(type="switch_profile", profile_id="perf_profile", revert_after_clear=False)
        rule = _rule("r1", "cpu0", 80.0, action=action)
        self.svc.set_rules([rule])

        # Fire
        _check_and_drain(self.svc, [_reading("cpu0", 85.0)])
        assert "perf_profile" in self.activated_profiles

        # Clear — should NOT revert
        self.activated_profiles.clear()
        _check_and_drain(self.svc, [_reading("cpu0", 70.0)])
        assert "default_profile" not in self.activated_profiles

    def test_mixed_revert_suppress_wins(self):
        """If any fired rule had revert_after_clear=False, no revert occurs after all clear."""
        action1 = AlertAction(type="switch_profile", profile_id="profile_A", revert_after_clear=True)
        action2 = AlertAction(type="switch_profile", profile_id="profile_B", revert_after_clear=False)
        rule1 = _rule("r1", "cpu0", 80.0, action=action1)
        rule2 = _rule("r2", "cpu0", 90.0, action=action2)
        self.svc.set_rules([rule1, rule2])

        # Fire both
        _check_and_drain(self.svc, [_reading("cpu0", 95.0)])

        # Clear both — rule2 had revert_after_clear=False, so no revert
        self.activated_profiles.clear()
        _check_and_drain(self.svc, [_reading("cpu0", 70.0)])
        assert "default_profile" not in self.activated_profiles


class TestAlertActionModel:
    """AlertAction model serialization."""

    def test_action_round_trips(self):
        action = AlertAction(type="switch_profile", profile_id="p1", revert_after_clear=False)
        rule = AlertRule(
            id="r1", sensor_id="cpu0", threshold=80,
            name="test", action=action,
        )
        d = rule.model_dump()
        assert d["action"]["type"] == "switch_profile"
        assert d["action"]["profile_id"] == "p1"
        assert d["action"]["revert_after_clear"] is False

        restored = AlertRule(**d)
        assert restored.action is not None
        assert restored.action.profile_id == "p1"

    def test_rule_without_action_serializes_null(self):
        rule = AlertRule(id="r1", sensor_id="cpu0", threshold=80, name="test")
        d = rule.model_dump()
        assert d["action"] is None
