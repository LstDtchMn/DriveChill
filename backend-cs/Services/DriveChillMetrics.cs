using Prometheus;

namespace DriveChill.Services;

/// <summary>
/// Central registry of all Prometheus metrics exposed at /metrics.
///
/// Metrics are auth-exempt — Prometheus scrapers use network-level isolation.
/// Gate with DRIVECHILL_PROMETHEUS_ENABLED=true.
///
/// Note: prometheus-net uses the global DefaultRegistry, so /metrics also emits
/// ASP.NET infrastructure metrics (http_request_duration_seconds, etc.).
/// The Python backend uses an isolated registry and emits only DriveChill metrics.
/// This asymmetry is intentional — ASP.NET infra metrics are useful for C# deployments
/// but not available in the Python backend.
/// </summary>
internal static class DriveChillMetrics
{
    /// <summary>Wall-clock duration of each hardware sensor poll, labelled by backend name.</summary>
    public static readonly Histogram SensorPollDuration = Metrics.CreateHistogram(
        "drivechill_sensor_poll_duration_seconds",
        "Duration of each hardware sensor poll in seconds.",
        new HistogramConfiguration { LabelNames = ["backend"] });

    /// <summary>Cumulative sensor readings collected, labelled by sensor_type.</summary>
    public static readonly Counter SensorReadingsTotal = Metrics.CreateCounter(
        "drivechill_sensor_readings_total",
        "Total sensor readings collected.",
        new CounterConfiguration { LabelNames = ["sensor_type"] });

    /// <summary>Cumulative alert events fired, labelled by rule_id and condition.</summary>
    public static readonly Counter AlertEventsTotal = Metrics.CreateCounter(
        "drivechill_alert_events_total",
        "Total alert events fired.",
        new CounterConfiguration { LabelNames = ["rule_id", "condition"] });

    /// <summary>Current fan speed as a percentage, labelled by fan_id.</summary>
    public static readonly Gauge FanSpeedPct = Metrics.CreateGauge(
        "drivechill_fan_speed_pct",
        "Current fan speed as a percentage (0-100).",
        new GaugeConfiguration { LabelNames = ["fan_id"] });

    /// <summary>Current drive temperature in Celsius, labelled by drive_id.</summary>
    public static readonly Gauge DriveTempCelsius = Metrics.CreateGauge(
        "drivechill_drive_temp_celsius",
        "Current drive temperature in Celsius.",
        new GaugeConfiguration { LabelNames = ["drive_id"] });

    /// <summary>Number of currently active WebSocket connections.</summary>
    public static readonly Gauge WebSocketConnectionsActive = Metrics.CreateGauge(
        "drivechill_websocket_connections_active",
        "Number of active WebSocket connections.");

    /// <summary>Cumulative webhook delivery attempts, labelled by success (true/false).</summary>
    public static readonly Counter WebhookDeliveriesTotal = Metrics.CreateCounter(
        "drivechill_webhook_deliveries_total",
        "Total webhook delivery attempts.",
        new CounterConfiguration { LabelNames = ["success"] });
}
