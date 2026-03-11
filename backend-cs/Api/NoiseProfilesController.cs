using DriveChill.Models;
using DriveChill.Services;
using Microsoft.AspNetCore.Mvc;

namespace DriveChill.Api;

[ApiController]
[Route("api/noise-profiles")]
public sealed class NoiseProfilesController : ControllerBase
{
    private static readonly HashSet<string> ValidModes = ["quick", "precise"];
    private readonly DbService _db;

    public NoiseProfilesController(DbService db) => _db = db;

    [HttpGet("")]
    public async Task<IActionResult> List(CancellationToken ct = default)
        => Ok(new { profiles = await _db.ListNoiseProfilesAsync(ct) });

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id, CancellationToken ct = default)
    {
        var profile = await _db.GetNoiseProfileAsync(id, ct);
        return profile is not null ? Ok(profile) : NotFound(new { detail = "Noise profile not found" });
    }

    [HttpPost("")]
    public async Task<IActionResult> Create([FromBody] NoiseProfileRequest body, CancellationToken ct = default)
    {
        var error = Validate(body);
        if (error is not null)
            return UnprocessableEntity(new { detail = error });

        var now = DateTimeOffset.UtcNow.ToString("o");
        var profile = new NoiseProfile
        {
            Id = $"np_{Guid.NewGuid().ToString("N")[..12]}",
            FanId = body.FanId,
            Mode = body.Mode,
            Data = body.Data,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await _db.CreateNoiseProfileAsync(profile, ct);
        return Ok(profile);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct = default)
    {
        var deleted = await _db.DeleteNoiseProfileAsync(id, ct);
        return deleted ? Ok(new { success = true }) : NotFound(new { detail = "Noise profile not found" });
    }

    private static string? Validate(NoiseProfileRequest body)
    {
        if (string.IsNullOrWhiteSpace(body.FanId))
            return "fan_id is required";
        if (!ValidModes.Contains(body.Mode))
            return "mode must be 'quick' or 'precise'";
        if (body.Data.Count == 0)
            return "data must not be empty";
        if (body.Data.Any(p => p.Rpm < 0))
            return "rpm must be non-negative";
        if (body.Data.Any(p => p.Db < 0))
            return "db must be non-negative";
        return null;
    }
}

public sealed class NoiseProfileRequest
{
    public string FanId { get; set; } = "";
    public string Mode { get; set; } = "quick";
    public List<NoiseDataPoint> Data { get; set; } = [];
}
