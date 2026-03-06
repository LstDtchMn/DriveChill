"""Prometheus metric definitions for DriveChill Python backend.

Uses a custom registry (no default process/GC metrics) so the output matches
the C# backend's /metrics exactly — same metric names, same labels.

Metric objects are always created (cheap), but the /metrics endpoint that calls
generate_latest() is only registered when DRIVECHILL_PROMETHEUS_ENABLED=true.
"""

from prometheus_client import CollectorRegistry, Counter, Gauge, Histogram

# Isolated registry — excludes default Python process_* and gc_* metrics so
# output is clean and matches the C# backend's metric set.
REGISTRY = CollectorRegistry(auto_describe=False)

sensor_poll_duration = Histogram(
    "drivechill_sensor_poll_duration_seconds",
    "Duration of each hardware sensor poll in seconds.",
    labelnames=["backend"],
    registry=REGISTRY,
)

sensor_readings_total = Counter(
    "drivechill_sensor_readings_total",
    "Total sensor readings collected.",
    labelnames=["sensor_type"],
    registry=REGISTRY,
)

alert_events_total = Counter(
    "drivechill_alert_events_total",
    "Total alert events fired.",
    labelnames=["rule_id", "condition"],
    registry=REGISTRY,
)

fan_speed_pct = Gauge(
    "drivechill_fan_speed_pct",
    "Current fan speed as a percentage (0-100).",
    labelnames=["fan_id"],
    registry=REGISTRY,
)

drive_temp_celsius = Gauge(
    "drivechill_drive_temp_celsius",
    "Current drive temperature in Celsius.",
    labelnames=["drive_id"],
    registry=REGISTRY,
)

websocket_connections_active = Gauge(
    "drivechill_websocket_connections_active",
    "Number of active WebSocket connections.",
    registry=REGISTRY,
)

webhook_deliveries_total = Counter(
    "drivechill_webhook_deliveries_total",
    "Total webhook delivery attempts.",
    labelnames=["success"],
    registry=REGISTRY,
)
