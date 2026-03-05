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
            return BadRequest(new { detail ="sensor_id is required" });

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

    // -----------------------------------------------------------------------
    // Sensor labels
    // -----------------------------------------------------------------------

    /// <summary>GET /api/sensors/labels — all custom sensor labels.</summary>
    [HttpGet("labels")]
    public async Task<IActionResult> GetLabels(CancellationToken ct = default)
    {
        var labels = await _db.GetAllLabelsAsync(ct);
        return Ok(new { labels });
    }

    /// <summary>PUT /api/sensors/{sensorId}/label — set or update a sensor label.</summary>
    [HttpPut("{sensorId}/label")]
    public async Task<IActionResult> SetLabel(string sensorId, [FromBody] SetLabelRequest req,
        CancellationToken ct = default)
    {
        if (sensorId.Length > 200)
            return UnprocessableEntity(new { detail ="sensor_id too long" });
        if (string.IsNullOrWhiteSpace(req.Label) || req.Label.Length > 100)
            return BadRequest(new { detail ="label must be 1-100 characters" });

        await _db.SetLabelAsync(sensorId, req.Label.Trim(), ct);
        return Ok(new { success = true, sensor_id = sensorId, label = req.Label.Trim() });
    }

    /// <summary>DELETE /api/sensors/{sensorId}/label — delete a sensor label.</summary>
    [HttpDelete("{sensorId}/label")]
    public async Task<IActionResult> DeleteLabel(string sensorId, CancellationToken ct = default)
    {
        var deleted = await _db.DeleteLabelAsync(sensorId, ct);
        return deleted
            ? Ok(new { success = true, sensor_id = sensorId })
            : NotFound(new { detail ="No label found for this sensor" });
    }
}

public sealed class SetLabelRequest
{
    public string Label { get; set; } = "";
}
