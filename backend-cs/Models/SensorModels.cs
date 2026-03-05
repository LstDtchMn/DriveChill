using System.Text.Json.Serialization;
// FanTestProgress lives in the same namespace — no extra using needed.

namespace DriveChill.Models;

/// <summary>
/// Stable string constants for the sensor_type field.
/// Must match the TypeScript SensorType union in frontend/src/lib/types.ts.
/// </summary>
public static class SensorTypeValues
{
    public const string CpuTemp   = "cpu_temp";
    public const string GpuTemp   = "gpu_temp";
    public const string HddTemp   = "hdd_temp";
    public const string CaseTemp  = "case_temp";
    public const string CpuLoad   = "cpu_load";
    public const string GpuLoad   = "gpu_load";
    public const string FanRpm    = "fan_rpm";
    public const string FanPercent = "fan_percent";
}

/// <summary>
/// Single sensor reading — serialised with snake_case to match the TypeScript contract.
/// </summary>
public sealed class SensorReading
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("sensor_type")]
    public string SensorType { get; init; } = "";

    [JsonPropertyName("value")]
    public double Value { get; init; }

    [JsonPropertyName("min_value")]
    public double? MinValue { get; init; }

    [JsonPropertyName("max_value")]
    public double? MaxValue { get; init; }

    [JsonPropertyName("unit")]
    public string Unit { get; init; } = "";

    // Additive drive metadata — only set for hdd_temp sensors from drive monitoring
    [JsonPropertyName("drive_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DriveId { get; init; }

    [JsonPropertyName("entity_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EntityName { get; init; }

    [JsonPropertyName("source_kind")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceKind { get; init; }
}

/// <summary>
/// Full snapshot broadcast over WebSocket and returned by GET /api/sensors.
/// </summary>
public sealed class SensorSnapshot
{
    [JsonPropertyName("readings")]
    public IReadOnlyList<SensorReading> Readings { get; init; } = [];

    [JsonPropertyName("backend")]
    public string Backend { get; init; } = "";

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// WebSocket message — flat structure matching the TypeScript WSMessage interface:
/// { type, timestamp, readings, applied_speeds, alerts, active_alerts }
/// </summary>
public sealed class WsMessage
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("readings")]
    public IReadOnlyList<SensorReading> Readings { get; init; } = [];

    /// <summary>fanId → speed percent currently applied by the curve engine.</summary>
    [JsonPropertyName("applied_speeds")]
    public IReadOnlyDictionary<string, double> AppliedSpeeds { get; init; } =
        new Dictionary<string, double>();

    [JsonPropertyName("alerts")]
    public IReadOnlyList<AlertEvent> Alerts { get; init; } = [];

    /// <summary>Rule IDs that are currently firing.</summary>
    [JsonPropertyName("active_alerts")]
    public IReadOnlyList<string> ActiveAlerts { get; init; } = [];

    /// <summary>
    /// Live progress for all actively-running fan benchmark tests.
    /// Omitted (not serialised) when no tests are running — zero overhead for normal traffic.
    /// </summary>
    [JsonPropertyName("fan_test")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<FanTestProgress>? FanTest { get; init; }
}
