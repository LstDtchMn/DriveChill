using DriveChill.Models;
using DriveChill.Services;
using Microsoft.AspNetCore.Mvc;

namespace DriveChill.Api;

[ApiController]
[Route("api/virtual-sensors")]
public sealed class VirtualSensorsController : ControllerBase
{
    private static readonly HashSet<string> ValidTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "max", "min", "avg", "weighted", "delta", "moving_avg"
    };

    private readonly DbService _db;
    private readonly VirtualSensorService _vsSvc;

    public VirtualSensorsController(DbService db, VirtualSensorService vsSvc)
    {
        _db = db;
        _vsSvc = vsSvc;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var sensors = await _db.GetVirtualSensorsAsync(ct);
        return Ok(new { virtual_sensors = sensors.Select(ToDto) });
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] VirtualSensorRequest body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Name))
            return BadRequest(new { detail = "Name is required" });
        if (!ValidTypes.Contains(body.Type))
            return BadRequest(new { detail = $"Invalid type '{body.Type}'. Must be one of: {string.Join(", ", ValidTypes.Order())}" });
        if (body.SourceIds == null || body.SourceIds.Count == 0)
            return BadRequest(new { detail = "At least one source sensor ID is required" });

        var vs = new VirtualSensor
        {
            Id = $"vs_{Guid.NewGuid():N}"[..15],
            Name = body.Name,
            Type = body.Type.ToLowerInvariant(),
            SourceIds = body.SourceIds,
            Weights = body.Weights,
            WindowSeconds = body.WindowSeconds,
            Offset = body.Offset,
            Enabled = body.Enabled,
        };
        await _db.CreateVirtualSensorAsync(vs, ct);
        await ReloadVirtualSensorsAsync(ct);
        return Ok(new { success = true, id = vs.Id });
    }

    [HttpPut("{sensorId}")]
    public async Task<IActionResult> Update(string sensorId, [FromBody] VirtualSensorRequest body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Name))
            return BadRequest(new { detail = "Name is required" });
        if (!ValidTypes.Contains(body.Type))
            return BadRequest(new { detail = $"Invalid type '{body.Type}'" });
        if (body.SourceIds == null || body.SourceIds.Count == 0)
            return BadRequest(new { detail = "At least one source sensor ID is required" });

        var vs = new VirtualSensor
        {
            Id = sensorId,
            Name = body.Name,
            Type = body.Type.ToLowerInvariant(),
            SourceIds = body.SourceIds,
            Weights = body.Weights,
            WindowSeconds = body.WindowSeconds,
            Offset = body.Offset,
            Enabled = body.Enabled,
        };
        var updated = await _db.UpdateVirtualSensorAsync(vs, ct);
        if (!updated)
            return NotFound(new { detail = "Virtual sensor not found" });
        await ReloadVirtualSensorsAsync(ct);
        return Ok(new { success = true });
    }

    [HttpDelete("{sensorId}")]
    public async Task<IActionResult> Delete(string sensorId, CancellationToken ct)
    {
        var deleted = await _db.DeleteVirtualSensorAsync(sensorId, ct);
        if (!deleted)
            return NotFound(new { detail = "Virtual sensor not found" });
        await ReloadVirtualSensorsAsync(ct);
        return Ok(new { success = true });
    }

    private async Task ReloadVirtualSensorsAsync(CancellationToken ct)
    {
        var all = await _db.GetVirtualSensorsAsync(ct);
        _vsSvc.Load(all);
    }

    private static object ToDto(VirtualSensor vs) => new
    {
        id = vs.Id,
        name = vs.Name,
        type = vs.Type,
        source_ids = vs.SourceIds,
        weights = vs.Weights,
        window_seconds = vs.WindowSeconds,
        offset = vs.Offset,
        enabled = vs.Enabled,
        created_at = vs.CreatedAt,
        updated_at = vs.UpdatedAt,
    };
}
