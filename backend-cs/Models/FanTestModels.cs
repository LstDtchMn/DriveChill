using System.Text.Json.Serialization;

namespace DriveChill.Models;

/// <summary>Options supplied to POST /api/fans/{fanId}/test.</summary>
public sealed class FanTestOptions
{
    /// <summary>Number of speed steps in the sweep (min 2, max 20). Default 10 → 0%, 10%, …, 100%.</summary>
    [JsonPropertyName("steps")]
    public int Steps { get; set; } = 10;

    /// <summary>Milliseconds to wait at each step before sampling RPM. Default 2500.</summary>
    [JsonPropertyName("settle_ms")]
    public int SettleMs { get; set; } = 2500;

    /// <summary>
    /// Minimum RPM to consider a fan spinning (stall detection).
    /// Default 50 — accounts for ±20 RPM noise in mock backend and real sensor jitter.
    /// </summary>
    [JsonPropertyName("min_rpm_threshold")]
    public double MinRpmThreshold { get; set; } = 50.0;
}

/// <summary>Data recorded at a single sweep step.</summary>
public sealed class FanTestStep
{
    [JsonPropertyName("speed_pct")]
    public double SpeedPct { get; init; }

    /// <summary>Observed RPM after the settle delay. Null if reading unavailable.</summary>
    [JsonPropertyName("rpm")]
    public double? Rpm { get; init; }

    /// <summary>True if RPM exceeded min_rpm_threshold (fan is spinning at this speed).</summary>
    [JsonPropertyName("spinning")]
    public bool Spinning { get; init; }
}

/// <summary>Full benchmark result — returned by GET /api/fans/{fanId}/test.</summary>
public sealed class FanTestResult
{
    [JsonPropertyName("fan_id")]
    public string FanId { get; init; } = "";

    /// <summary>"running" | "completed" | "cancelled" | "failed"</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = "running";

    [JsonPropertyName("started_at")]
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("completed_at")]
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Ordered list of completed steps (grows as the test progresses).</summary>
    [JsonPropertyName("steps")]
    public List<FanTestStep> Steps { get; init; } = [];

    /// <summary>Lowest speed_pct where spinning == true. Null until found.</summary>
    [JsonPropertyName("min_operational_pct")]
    public double? MinOperationalPct { get; set; }

    /// <summary>RPM recorded at 100% speed. Null until the final step completes.</summary>
    [JsonPropertyName("max_rpm")]
    public double? MaxRpm { get; set; }

    [JsonPropertyName("options")]
    public FanTestOptions Options { get; init; } = new();

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

/// <summary>
/// Slim progress snapshot embedded in every WsMessage tick while a test is running.
/// Only included when fan_test is non-null (active tests).
/// </summary>
public sealed class FanTestProgress
{
    [JsonPropertyName("fan_id")]
    public string FanId { get; init; } = "";

    [JsonPropertyName("status")]
    public string Status { get; init; } = "running";

    [JsonPropertyName("steps_done")]
    public int StepsDone { get; init; }

    [JsonPropertyName("steps_total")]
    public int StepsTotal { get; init; }

    /// <summary>Speed percent commanded at the current step.</summary>
    [JsonPropertyName("current_pct")]
    public double CurrentPct { get; init; }

    /// <summary>Most recently sampled RPM. Null while settling at a new step.</summary>
    [JsonPropertyName("current_rpm")]
    public double? CurrentRpm { get; init; }

    /// <summary>Completed steps so far — frontend uses this for the live results table.</summary>
    [JsonPropertyName("steps")]
    public IReadOnlyList<FanTestStep> Steps { get; init; } = [];

    [JsonPropertyName("min_operational_pct")]
    public double? MinOperationalPct { get; init; }
}
