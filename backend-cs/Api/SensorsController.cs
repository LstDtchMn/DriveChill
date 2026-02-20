using Microsoft.AspNetCore.Mvc;
using DriveChill.Models;
using DriveChill.Services;

namespace DriveChill.Api;

[ApiController]
[Route("api/sensors")]
public sealed class SensorsController : ControllerBase
{
    private readonly SensorService _sensors;
    private readonly DbService     _db;

    public SensorsController(SensorService sensors, DbService db)
    {
        _sensors = sensors;
        _db      = db;
    }

    /// <summary>GET /api/sensors — current snapshot.</summary>
    [HttpGet]
    public IActionResult GetSensors()
    {
        var snap = _sensors.Latest;
        return Ok(snap);
    }

    /// <summary>GET /api/sensors/history?sensor_id=&amp;hours=1</summary>
    [HttpGet("history")]
    public async Task<IActionResult> GetHistory(
        [FromQuery(Name = "sensor_id")] string sensorId,
        [FromQuery] double hours = 1,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sensorId))
            return BadRequest(new { error = "sensor_id is required" });

        var since = DateTimeOffset.UtcNow.AddHours(-hours);
        var rows  = await _db.GetHistoryAsync(sensorId, since, ct);
        return Ok(rows);
    }

    /// <summary>GET /api/sensors/export?hours=24 — CSV download.</summary>
    [HttpGet("export")]
    public async Task<IActionResult> Export(
        [FromQuery] double hours = 24,
        CancellationToken ct = default)
    {
        var since = DateTimeOffset.UtcNow.AddHours(-hours);
        var csv   = await _db.ExportCsvAsync(since, ct);
        return File(System.Text.Encoding.UTF8.GetBytes(csv),
            "text/csv", $"drivechill-export-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv");
    }
}
