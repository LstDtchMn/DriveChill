from datetime import datetime

from pydantic import BaseModel

from app.models.sensors import SensorReading, SensorType


TEMP_SENSOR_TYPES = {SensorType.CPU_TEMP, SensorType.GPU_TEMP, SensorType.HDD_TEMP, SensorType.CASE_TEMP}


class AlertRule(BaseModel):
    id: str
    sensor_id: str
    threshold: float  # Temperature threshold in °C
    name: str = ""
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

    def __init__(self) -> None:
        self._rules: list[AlertRule] = []
        self._events: list[AlertEvent] = []
        self._active_alerts: set[str] = set()  # rule IDs currently triggered
        self._max_events = 500

    @property
    def rules(self) -> list[AlertRule]:
        return list(self._rules)

    @property
    def events(self) -> list[AlertEvent]:
        return list(self._events)

    @property
    def active_alerts(self) -> list[str]:
        return list(self._active_alerts)

    def add_rule(self, rule: AlertRule) -> None:
        self._rules = [r for r in self._rules if r.id != rule.id]
        self._rules.append(rule)

    def remove_rule(self, rule_id: str) -> None:
        self._rules = [r for r in self._rules if r.id != rule_id]
        self._active_alerts.discard(rule_id)

    def set_rules(self, rules: list[AlertRule]) -> None:
        self._rules = list(rules)

    def check(self, readings: list[SensorReading]) -> list[AlertEvent]:
        """Check readings against rules. Returns list of newly triggered alerts."""
        sensor_map = {r.id: r for r in readings if r.sensor_type in TEMP_SENSOR_TYPES}
        new_events: list[AlertEvent] = []

        for rule in self._rules:
            if not rule.enabled:
                continue

            reading = sensor_map.get(rule.sensor_id)
            if not reading:
                continue

            if reading.value >= rule.threshold:
                if rule.id not in self._active_alerts:
                    self._active_alerts.add(rule.id)
                    event = AlertEvent(
                        rule_id=rule.id,
                        sensor_id=reading.id,
                        sensor_name=reading.name,
                        threshold=rule.threshold,
                        actual_value=reading.value,
                        timestamp=datetime.now(),
                        message=f"{reading.name} reached {reading.value}°C (threshold: {rule.threshold}°C)",
                    )
                    self._events.append(event)
                    new_events.append(event)
                    # Trim old events
                    if len(self._events) > self._max_events:
                        self._events = self._events[-self._max_events:]
            else:
                # Clear alert when temp drops below threshold
                self._active_alerts.discard(rule.id)

        return new_events

    def clear_events(self) -> None:
        self._events.clear()
        self._active_alerts.clear()
