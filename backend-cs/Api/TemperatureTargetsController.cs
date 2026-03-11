using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using DriveChill.Models;
using DriveChill.Services;

namespace DriveChill.Api;

[ApiController]
[Route("api/temperature-targets")]
public sealed partial class TemperatureTargetsController : ControllerBase
{
    private readonly TemperatureTargetService _svc;
    private readonly SensorService _sensors;
    private readonly FanService _fans;

    [GeneratedRegex(@"^(hdd_temp_|cpu_temp_|gpu_temp_|vs_)")]
    private static partial Regex SensorIdPattern();

    public TemperatureTargetsController(
        TemperatureTargetService svc,
        SensorService sensors,
        FanService fans)
    {
        _svc = svc;
        _sensors = sensors;
        _fans = fans;
    }

    /// <summary>GET /api/temperature-targets</summary>
    [HttpGet("")]
    public IActionResult List()
        => Ok(new { targets = _svc.Targets });

    /// <summary>POST /api/temperature-targets</summary>
    [HttpPost("")]
    public async Task<IActionResult> Create([FromBody] TemperatureTargetCreateRequest req)
    {
        var err = ValidateSensorId(req.SensorId, isNew: true);
        if (err is not null) return err;
        err = ValidateFanIds(req.FanIds);
        if (err is not null) return err;

        var target = new TemperatureTarget
        {
            Id = GenerateId(),
            Name = req.Name,
            DriveId = req.DriveId,
            SensorId = req.SensorId,
            FanIds = req.FanIds,
            TargetTempC = req.TargetTempC,
            ToleranceC = req.ToleranceC,
            MinFanSpeed = req.MinFanSpeed,
            PidMode = req.PidMode,
            PidKp = req.PidKp,
            PidKi = req.PidKi,
            PidKd = req.PidKd,
        };
        var created = await _svc.AddAsync(target);
        return StatusCode(201, created);
    }

    /// <summary>GET /api/temperature-targets/{targetId}</summary>
    [HttpGet("{targetId}")]
    public IActionResult Get(string targetId)
    {
        var target = _svc.Targets.FirstOrDefault(t => t.Id == targetId);
        return target is not null ? Ok(target) : NotFound(new { detail = "Not found" });
    }

    /// <summary>PUT /api/temperature-targets/{targetId}</summary>
    [HttpPut("{targetId}")]
    public async Task<IActionResult> Update(string targetId,
        [FromBody] TemperatureTargetUpdateRequest req)
    {
        var existing = _svc.Targets.FirstOrDefault(t => t.Id == targetId);
        if (existing is null)
            return NotFound(new { detail = "Not found" });

        var sensorChanged = req.SensorId != existing.SensorId;
        var err = ValidateSensorId(req.SensorId, isNew: sensorChanged);
        if (err is not null) return err;
        err = ValidateFanIds(req.FanIds);
        if (err is not null) return err;

        var updated = await _svc.UpdateAsync(
            targetId, req.Name, req.DriveId, req.SensorId, req.FanIds,
            req.TargetTempC, req.ToleranceC, req.MinFanSpeed,
            req.PidMode, req.PidKp, req.PidKi, req.PidKd);
        return updated is not null ? Ok(updated) : NotFound(new { detail = "Not found" });
    }

    /// <summary>DELETE /api/temperature-targets/{targetId}</summary>
    [HttpDelete("{targetId}")]
    public async Task<IActionResult> Delete(string targetId)
    {
        var deleted = await _svc.RemoveAsync(targetId);
        return deleted ? Ok(new { success = true }) : NotFound(new { detail = "Not found" });
    }

    /// <summary>PATCH /api/temperature-targets/{targetId}/enabled</summary>
    [HttpPatch("{targetId}/enabled")]
    public async Task<IActionResult> Toggle(string targetId,
        [FromBody] TemperatureTargetToggleRequest req)
    {
        var updated = await _svc.SetEnabledAsync(targetId, req.Enabled);
        return updated is not null ? Ok(updated) : NotFound(new { detail = "Not found" });
    }

    // -----------------------------------------------------------------------
    // Validation helpers
    // -----------------------------------------------------------------------

    private IActionResult? ValidateSensorId(string sensorId, bool isNew)
    {
        if (!SensorIdPattern().IsMatch(sensorId))
            return UnprocessableEntity(new { detail = "sensor_id must start with hdd_temp_, cpu_temp_, gpu_temp_, or vs_" });

        if (isNew)
        {
            var known = _sensors.Latest.Readings
                .Select(r => r.Id)
                .ToHashSet();
            if (known.Count > 0 && !known.Contains(sensorId))
                return UnprocessableEntity(new
                {
                    detail = $"sensor not found: {sensorId} — drive may be offline or not yet detected"
                });
        }
        return null;
    }

    private IActionResult? ValidateFanIds(string[] fanIds)
    {
        if (fanIds.Length == 0)
            return UnprocessableEntity(new { detail = "fan_ids must not be empty" });

        var known = _fans.GetAll(new SensorSnapshot
            {
                Timestamp = DateTimeOffset.UtcNow,
                Readings = _sensors.Latest.Readings,
            })
            .Select(f => f.FanId)
            .ToHashSet();

        if (known.Count > 0)
        {
            foreach (var fid in fanIds)
            {
                if (!known.Contains(fid))
                    return UnprocessableEntity(new { detail = $"fan not found: {fid}" });
            }
        }
        return null;
    }

    private static string GenerateId()
    {
        var bytes = new byte[6];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexStringLower(bytes);
    }
}
