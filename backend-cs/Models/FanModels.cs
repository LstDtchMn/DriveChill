using System.Text.Json.Serialization;

namespace DriveChill.Models;

/// <summary>A single (temperature → speed) control point on a fan curve.</summary>
public sealed class FanCurvePoint
{
    [JsonPropertyName("temp")]
    public double Temp { get; set; }

    [JsonPropertyName("speed")]
    public double Speed { get; set; }
}

/// <summary>A named fan curve that maps a sensor to a fan via interpolated control points.</summary>
public sealed class FanCurve
{
    [JsonPropertyName("fan_id")]
    public string FanId { get; set; } = "";

    [JsonPropertyName("sensor_id")]
    public string SensorId { get; set; } = "";

    [JsonPropertyName("points")]
    public List<FanCurvePoint> Points { get; set; } = [];

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("sensor_ids")]
    public List<string> SensorIds { get; set; } = [];
}

/// <summary>Request body for POST /api/fans/speed.</summary>
public sealed class SetSpeedRequest
{
    [JsonPropertyName("fan_id")]
    public string FanId { get; set; } = "";

    [JsonPropertyName("speed")]
    public double Speed { get; set; }
}

/// <summary>Request body for PUT /api/fans/curves.</summary>
public sealed class UpdateCurveRequest
{
    [JsonPropertyName("curve")]
    public FanCurve Curve { get; set; } = new();

    [JsonPropertyName("allow_dangerous")]
    public bool AllowDangerous { get; set; }
}

/// <summary>Request body for POST /api/fans/curves/validate.</summary>
public sealed class ValidateCurveRequest
{
    [JsonPropertyName("points")]
    public List<FanCurvePoint> Points { get; set; } = [];
}

/// <summary>A dangerous-speed warning returned by the curve safety check.</summary>
public sealed class DangerWarning
{
    [JsonPropertyName("temp")]
    public double Temp { get; set; }

    [JsonPropertyName("speed")]
    public double Speed { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}

/// <summary>Per-fan settings (min speed floor, zero-RPM capability).</summary>
public sealed class FanSettingsModel
{
    [JsonPropertyName("fan_id")]
    public string FanId { get; set; } = "";

    [JsonPropertyName("min_speed_pct")]
    public double MinSpeedPct { get; set; }

    [JsonPropertyName("zero_rpm_capable")]
    public bool ZeroRpmCapable { get; set; }
}

/// <summary>Request body for PUT /api/fans/{fanId}/settings.</summary>
public sealed class UpdateFanSettingsRequest
{
    [JsonPropertyName("min_speed_pct")]
    public double MinSpeedPct { get; set; }

    [JsonPropertyName("zero_rpm_capable")]
    public bool ZeroRpmCapable { get; set; }
}

/// <summary>Fan status returned by GET /api/fans.</summary>
public sealed class FanStatus
{
    [JsonPropertyName("fan_id")]
    public string FanId { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("speed_percent")]
    public double SpeedPercent { get; set; }

    [JsonPropertyName("rpm")]
    public double? Rpm { get; set; }

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "auto"; // "auto" | "manual" | "curve"

    [JsonPropertyName("curve")]
    public FanCurve? Curve { get; set; }
}
