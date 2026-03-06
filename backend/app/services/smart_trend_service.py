"""SMART trend alerting: detects drive degradation trends and feeds the alert pipeline."""
from __future__ import annotations

import logging
from dataclasses import dataclass, field
from typing import TYPE_CHECKING, cast

from app.models.sensors import SensorReading, SensorType

if TYPE_CHECKING:
    from app.services.alert_service import AlertService

logger = logging.getLogger(__name__)


@dataclass
class SmartTrendConfig:
    """Thresholds for SMART trend detection."""
    wear_warning_pct: float = 80.0
    wear_critical_pct: float = 90.0
    power_on_hours_warning: int = 35_000  # ~4 years
    power_on_hours_critical: int = 50_000  # ~5.7 years
    reallocated_sector_delta_threshold: int = 1  # any increase fires


@dataclass
class _DriveSnapshot:
    """Last-seen values for a single drive."""
    reallocated_sectors: int | None = None
    wear_percent_used: float | None = None
    power_on_hours: int | None = None
    # Track which conditions are currently active to prevent duplicate alerts
    active_conditions: set[str] = field(default_factory=set)


class SmartTrendService:
    """Detects SMART degradation trends and pushes synthetic alerts.

    Called by DriveMonitorService after each health poll. Keeps in-memory
    state of the last-seen values per drive so it can detect deltas.
    """

    def __init__(self, config: SmartTrendConfig | None = None) -> None:
        self._config = config or SmartTrendConfig()
        self._snapshots: dict[str, _DriveSnapshot] = {}
        self._alert_service: AlertService | None = None

    def set_alert_service(self, svc: AlertService) -> None:
        self._alert_service = svc

    def check_drive(
        self,
        drive_id: str,
        drive_name: str,
        reallocated_sectors: int | None = None,
        wear_percent_used: float | None = None,
        power_on_hours: int | None = None,
    ) -> list[dict]:
        """Check a drive's SMART values for trend alerts.

        Returns a list of alert dicts (each has 'condition', 'severity', 'message').
        Also fires synthetic alerts into the alert pipeline if wired.
        """
        prev = self._snapshots.get(drive_id, _DriveSnapshot())
        alerts: list[dict] = []
        cfg = self._config

        # 1. Reallocated sector increase
        if reallocated_sectors is not None and prev.reallocated_sectors is not None:
            prev_realloc = cast(int, prev.reallocated_sectors)
            delta = reallocated_sectors - prev_realloc
            if delta >= cfg.reallocated_sector_delta_threshold:
                cond = "reallocated_increase"
                if cond not in prev.active_conditions:
                    alerts.append({
                        "condition": cond,
                        "severity": "critical",
                        "value": float(reallocated_sectors),
                        "threshold": float(prev_realloc + cfg.reallocated_sector_delta_threshold),
                        "message": (
                            f"{drive_name}: reallocated sectors increased by {delta} "
                            f"({prev.reallocated_sectors} → {reallocated_sectors})"
                        ),
                    })
                    prev.active_conditions.add(cond)
            else:
                prev.active_conditions.discard("reallocated_increase")

        # 2. Wear threshold crossing
        if wear_percent_used is not None:
            if wear_percent_used >= cfg.wear_critical_pct:
                cond = "wear_critical"
                if cond not in prev.active_conditions:
                    alerts.append({
                        "condition": cond,
                        "severity": "critical",
                        "value": wear_percent_used,
                        "threshold": cfg.wear_critical_pct,
                        "message": f"{drive_name}: wear level critical ({wear_percent_used:.1f}% used)",
                    })
                    prev.active_conditions.add(cond)
                prev.active_conditions.discard("wear_warning")
            elif wear_percent_used >= cfg.wear_warning_pct:
                cond = "wear_warning"
                if cond not in prev.active_conditions:
                    alerts.append({
                        "condition": cond,
                        "severity": "warning",
                        "value": wear_percent_used,
                        "threshold": cfg.wear_warning_pct,
                        "message": f"{drive_name}: wear level warning ({wear_percent_used:.1f}% used)",
                    })
                    prev.active_conditions.add(cond)
                prev.active_conditions.discard("wear_critical")
            else:
                prev.active_conditions.discard("wear_warning")
                prev.active_conditions.discard("wear_critical")

        # 3. Power-on-hours threshold
        if power_on_hours is not None:
            if power_on_hours >= cfg.power_on_hours_critical:
                cond = "poh_critical"
                if cond not in prev.active_conditions:
                    alerts.append({
                        "condition": cond,
                        "severity": "critical",
                        "value": float(power_on_hours),
                        "threshold": float(cfg.power_on_hours_critical),
                        "message": f"{drive_name}: power-on hours critical ({power_on_hours:,}h)",
                    })
                    prev.active_conditions.add(cond)
                prev.active_conditions.discard("poh_warning")
            elif power_on_hours >= cfg.power_on_hours_warning:
                cond = "poh_warning"
                if cond not in prev.active_conditions:
                    alerts.append({
                        "condition": cond,
                        "severity": "warning",
                        "value": float(power_on_hours),
                        "threshold": float(cfg.power_on_hours_warning),
                        "message": f"{drive_name}: power-on hours warning ({power_on_hours:,}h)",
                    })
                    prev.active_conditions.add(cond)
                prev.active_conditions.discard("poh_critical")
            else:
                prev.active_conditions.discard("poh_warning")
                prev.active_conditions.discard("poh_critical")

        # Update snapshot
        self._snapshots[drive_id] = _DriveSnapshot(
            reallocated_sectors=reallocated_sectors,
            wear_percent_used=wear_percent_used,
            power_on_hours=power_on_hours,
            active_conditions=prev.active_conditions,
        )

        # Feed alerts into the alert pipeline
        if alerts and self._alert_service:
            from datetime import datetime, timezone
            from app.services.alert_service import AlertEvent

            now = datetime.now(timezone.utc)
            for a in alerts:
                event = AlertEvent(
                    rule_id=f"smart_trend_{drive_id}_{a['condition']}",
                    sensor_id=f"hdd_temp_{drive_id}",
                    sensor_name=drive_name,
                    threshold=a["threshold"],
                    actual_value=a["value"],
                    timestamp=now,
                    message=a["message"],
                )
                self._alert_service.inject_event(event)
                logger.warning("SMART trend alert: %s", a["message"])

        return alerts
