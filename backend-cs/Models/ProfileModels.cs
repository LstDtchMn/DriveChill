using System.Text.Json.Serialization;

namespace DriveChill.Models;

/// <summary>A named profile bundling a set of fan curves.</summary>
public sealed class Profile
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("is_active")]
    public bool IsActive { get; set; }

    [JsonPropertyName("curves")]
    public List<FanCurve> Curves { get; set; } = [];

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Request body for POST /api/profiles.</summary>
public sealed class CreateProfileRequest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("curves")]
    public List<FanCurve> Curves { get; set; } = [];
}

/// <summary>Built-in preset curve templates exposed at GET /api/profiles/preset-curves.</summary>
public static class PresetCurves
{
    public static readonly IReadOnlyDictionary<string, List<FanCurvePoint>> All =
        new Dictionary<string, List<FanCurvePoint>>
        {
            ["silent"] =
            [
                new() { Temp = 30, Speed = 20 },
                new() { Temp = 50, Speed = 35 },
                new() { Temp = 70, Speed = 55 },
                new() { Temp = 85, Speed = 80 },
                new() { Temp = 95, Speed = 100 },
            ],
            ["balanced"] =
            [
                new() { Temp = 30, Speed = 30 },
                new() { Temp = 50, Speed = 45 },
                new() { Temp = 70, Speed = 65 },
                new() { Temp = 85, Speed = 85 },
                new() { Temp = 95, Speed = 100 },
            ],
            ["performance"] =
            [
                new() { Temp = 30, Speed = 50 },
                new() { Temp = 50, Speed = 65 },
                new() { Temp = 70, Speed = 80 },
                new() { Temp = 85, Speed = 95 },
                new() { Temp = 95, Speed = 100 },
            ],
        };
}
