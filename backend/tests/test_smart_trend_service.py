"""Tests for SMART trend alerting (Phase 8)."""
from __future__ import annotations

from app.services.smart_trend_service import SmartTrendConfig, SmartTrendService


class TestReallocatedSectors:
    """Reallocated sector increase detection."""

    def test_first_snapshot_no_alert(self):
        svc = SmartTrendService()
        alerts = svc.check_drive("d1", "Drive 1", reallocated_sectors=5)
        assert len(alerts) == 0

    def test_increase_fires_alert(self):
        svc = SmartTrendService()
        svc.check_drive("d1", "Drive 1", reallocated_sectors=5)
        alerts = svc.check_drive("d1", "Drive 1", reallocated_sectors=8)
        assert len(alerts) == 1
        assert alerts[0]["condition"] == "reallocated_increase"
        assert alerts[0]["severity"] == "critical"
        assert "increased by 3" in alerts[0]["message"]

    def test_no_increase_no_alert(self):
        svc = SmartTrendService()
        svc.check_drive("d1", "Drive 1", reallocated_sectors=5)
        alerts = svc.check_drive("d1", "Drive 1", reallocated_sectors=5)
        assert len(alerts) == 0

    def test_no_duplicate_while_active(self):
        svc = SmartTrendService()
        svc.check_drive("d1", "Drive 1", reallocated_sectors=5)
        svc.check_drive("d1", "Drive 1", reallocated_sectors=8)
        # Second check with same elevated value — should not fire again
        alerts = svc.check_drive("d1", "Drive 1", reallocated_sectors=8)
        assert len(alerts) == 0

    def test_clears_after_no_increase(self):
        svc = SmartTrendService()
        svc.check_drive("d1", "Drive 1", reallocated_sectors=5)
        svc.check_drive("d1", "Drive 1", reallocated_sectors=8)
        # Same value — condition clears
        svc.check_drive("d1", "Drive 1", reallocated_sectors=8)
        # Now increase again — should fire
        alerts = svc.check_drive("d1", "Drive 1", reallocated_sectors=10)
        assert len(alerts) == 1


class TestWearThreshold:
    """Wear level threshold detection."""

    def test_wear_warning(self):
        svc = SmartTrendService(SmartTrendConfig(wear_warning_pct=80, wear_critical_pct=90))
        alerts = svc.check_drive("d1", "SSD", wear_percent_used=82.0)
        assert len(alerts) == 1
        assert alerts[0]["condition"] == "wear_warning"
        assert alerts[0]["severity"] == "warning"

    def test_wear_critical(self):
        svc = SmartTrendService(SmartTrendConfig(wear_warning_pct=80, wear_critical_pct=90))
        alerts = svc.check_drive("d1", "SSD", wear_percent_used=92.0)
        assert len(alerts) == 1
        assert alerts[0]["condition"] == "wear_critical"
        assert alerts[0]["severity"] == "critical"

    def test_no_duplicate_wear_warning(self):
        svc = SmartTrendService(SmartTrendConfig(wear_warning_pct=80, wear_critical_pct=90))
        svc.check_drive("d1", "SSD", wear_percent_used=82.0)
        alerts = svc.check_drive("d1", "SSD", wear_percent_used=83.0)
        assert len(alerts) == 0

    def test_wear_clears_when_below(self):
        svc = SmartTrendService(SmartTrendConfig(wear_warning_pct=80, wear_critical_pct=90))
        svc.check_drive("d1", "SSD", wear_percent_used=82.0)
        svc.check_drive("d1", "SSD", wear_percent_used=70.0)
        # Re-entering warning should fire again
        alerts = svc.check_drive("d1", "SSD", wear_percent_used=82.0)
        assert len(alerts) == 1

    def test_wear_escalates_to_critical(self):
        svc = SmartTrendService(SmartTrendConfig(wear_warning_pct=80, wear_critical_pct=90))
        svc.check_drive("d1", "SSD", wear_percent_used=82.0)
        alerts = svc.check_drive("d1", "SSD", wear_percent_used=92.0)
        assert len(alerts) == 1
        assert alerts[0]["condition"] == "wear_critical"


class TestPowerOnHours:
    """Power-on-hours threshold detection."""

    def test_poh_warning(self):
        svc = SmartTrendService(SmartTrendConfig(power_on_hours_warning=35000, power_on_hours_critical=50000))
        alerts = svc.check_drive("d1", "HDD", power_on_hours=36000)
        assert len(alerts) == 1
        assert alerts[0]["condition"] == "poh_warning"

    def test_poh_critical(self):
        svc = SmartTrendService(SmartTrendConfig(power_on_hours_warning=35000, power_on_hours_critical=50000))
        alerts = svc.check_drive("d1", "HDD", power_on_hours=51000)
        assert len(alerts) == 1
        assert alerts[0]["condition"] == "poh_critical"

    def test_no_duplicate_poh(self):
        svc = SmartTrendService(SmartTrendConfig(power_on_hours_warning=35000, power_on_hours_critical=50000))
        svc.check_drive("d1", "HDD", power_on_hours=36000)
        alerts = svc.check_drive("d1", "HDD", power_on_hours=36100)
        assert len(alerts) == 0

    def test_below_threshold_no_alert(self):
        svc = SmartTrendService(SmartTrendConfig(power_on_hours_warning=35000, power_on_hours_critical=50000))
        alerts = svc.check_drive("d1", "HDD", power_on_hours=10000)
        assert len(alerts) == 0


class TestMultipleDrives:
    """Isolation between drives."""

    def test_drives_tracked_independently(self):
        svc = SmartTrendService()
        svc.check_drive("d1", "Drive 1", reallocated_sectors=5)
        svc.check_drive("d2", "Drive 2", reallocated_sectors=0)

        alerts1 = svc.check_drive("d1", "Drive 1", reallocated_sectors=8)
        alerts2 = svc.check_drive("d2", "Drive 2", reallocated_sectors=0)

        assert len(alerts1) == 1
        assert len(alerts2) == 0
