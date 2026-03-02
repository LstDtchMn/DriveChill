using Microsoft.AspNetCore.Mvc;
using DriveChill.Services;

namespace DriveChill.Api;

[ApiController]
[Route("api/analytics")]
public sealed class AnalyticsController : ControllerBase
{
    private readonly DbService _db;

    public AnalyticsController(DbService db)
    {
        _db = db;
    }

    [HttpGet("history")]
    public async Task<IActionResult> GetHistory(
        [FromQuery] double hours = 1.0,
        [FromQuery(Name = "sensor_id")] string? sensorId = null,
        [FromQuery(Name = "bucket_seconds")] int bucketSeconds = 60,
        CancellationToken ct = default)
    {
        hours = Math.Clamp(hours, 0.1, 8760.0);
        bucketSeconds = Math.Clamp(bucketSeconds, 10, 86400);
        var buckets = await _db.GetAnalyticsHistoryAsync(hours, sensorId, bucketSeconds, ct);
        return Ok(new { buckets });
    }

    [HttpGet("stats")]
    public async Task<IActionResult> GetStats(
        [FromQuery] double hours = 24.0,
        [FromQuery(Name = "sensor_id")] string? sensorId = null,
        CancellationToken ct = default)
    {
        hours = Math.Clamp(hours, 0.1, 8760.0);
        var stats = await _db.GetAnalyticsStatsAsync(hours, sensorId, ct);
        return Ok(new { stats });
    }

    [HttpGet("anomalies")]
    public async Task<IActionResult> GetAnomalies(
        [FromQuery] double hours = 24.0,
        [FromQuery(Name = "z_score_threshold")] double zScoreThreshold = 3.0,
        CancellationToken ct = default)
    {
        hours = Math.Clamp(hours, 0.1, 720.0);
        zScoreThreshold = Math.Clamp(zScoreThreshold, 1.0, 10.0);
        var anomalies = await _db.GetAnalyticsAnomaliesAsync(hours, zScoreThreshold, ct);
        return Ok(new { anomalies });
    }

    [HttpGet("report")]
    public async Task<IActionResult> GetReport(
        [FromQuery] double hours = 24.0,
        CancellationToken ct = default)
    {
        hours = Math.Clamp(hours, 0.1, 720.0);
        var stats = await _db.GetAnalyticsStatsAsync(hours, null, ct);
        var anomalies = await _db.GetAnalyticsAnomaliesAsync(hours, 3.0, ct);

        var topAnomalous = anomalies
            .GroupBy(a => a.SensorId)
            .Select(g => new { sensor_id = g.Key, sensor_name = g.First().SensorName, count = g.Count() })
            .OrderByDescending(x => x.count)
            .Take(3)
            .ToList();

        return Ok(new
        {
            generated_at          = DateTimeOffset.UtcNow.ToString("o"),
            window_hours          = hours,
            stats,
            anomalies,
            top_anomalous_sensors = topAnomalous,
        });
    }
}
