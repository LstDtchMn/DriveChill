using System.Text.Json.Serialization;

namespace DriveChill.Models;

/// <summary>A threshold rule that fires when a sensor exceeds a value.</summary>
public sealed class AlertRule
{
    [JsonPropertyName("rule_id")]
    public string RuleId { get; set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("sensor_id")]
    public string SensorId { get; set; } = "";

    [JsonPropertyName("sensor_name")]
    public string SensorName { get; set; } = "";

    [JsonPropertyName("threshold")]
    public double Threshold { get; set; }

    /// <summary>"above" or "below"</summary>
    [JsonPropertyName("condition")]
    public string Condition { get; set; } = "above";

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A fired alert event with the actual sensor value that triggered it.</summary>
public sealed class AlertEvent
{
    [JsonPropertyName("rule_id")]
    public string RuleId { get; set; } = "";

    [JsonPropertyName("sensor_id")]
    public string SensorId { get; set; } = "";

    [JsonPropertyName("sensor_name")]
    public string SensorName { get; set; } = "";

    [JsonPropertyName("actual_value")]
    public double ActualValue { get; set; }

    [JsonPropertyName("threshold")]
    public double Threshold { get; set; }

    [JsonPropertyName("condition")]
    public string Condition { get; set; } = "above";

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("fired_at")]
    public DateTimeOffset FiredAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("cleared")]
    public bool Cleared { get; set; }
}

/// <summary>Request body for POST /api/alerts/rules.</summary>
public sealed class CreateAlertRuleRequest
{
    [JsonPropertyName("sensor_id")]
    public string SensorId { get; set; } = "";

    [JsonPropertyName("sensor_name")]
    public string SensorName { get; set; } = "";

    [JsonPropertyName("threshold")]
    public double Threshold { get; set; }

    [JsonPropertyName("condition")]
    public string Condition { get; set; } = "above";

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}
