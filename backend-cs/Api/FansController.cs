using Microsoft.AspNetCore.Mvc;
using DriveChill.Models;
using DriveChill.Services;

namespace DriveChill.Api;

[ApiController]
[Route("api/fans")]
public sealed class FansController : ControllerBase
{
    private readonly FanService    _fans;
    private readonly SensorService _sensors;

    public FansController(FanService fans, SensorService sensors)
    {
        _fans    = fans;
        _sensors = sensors;
    }

    /// <summary>GET /api/fans — list all fans with current speed and active curve.</summary>
    [HttpGet]
    public IActionResult GetFans()
    {
        var status = _fans.GetAll(_sensors.Latest);
        return Ok(status);
    }

    /// <summary>POST /api/fans/{fanId}/speed — set manual speed percent.</summary>
    [HttpPost("{fanId}/speed")]
    public IActionResult SetSpeed(string fanId, [FromBody] SetSpeedRequest req)
    {
        if (req.Speed < 0 || req.Speed > 100)
            return BadRequest(new { error = "speed must be 0–100" });

        var ok = _fans.SetSpeed(fanId, req.Speed);
        return ok ? Ok(new { ok = true }) : NotFound(new { error = $"Fan '{fanId}' not found" });
    }

    /// <summary>POST /api/fans/{fanId}/auto — return fan to motherboard control.</summary>
    [HttpPost("{fanId}/auto")]
    public IActionResult SetAuto(string fanId)
    {
        var ok = _fans.SetAuto(fanId);
        return ok ? Ok(new { ok = true }) : NotFound(new { error = $"Fan '{fanId}' not found" });
    }

    // -----------------------------------------------------------------------
    // Curves
    // -----------------------------------------------------------------------

    /// <summary>GET /api/fans/curves — all active fan curves.</summary>
    [HttpGet("curves")]
    public IActionResult GetCurves() => Ok(_fans.GetCurves());

    /// <summary>PUT /api/fans/curves — upsert a fan curve.</summary>
    [HttpPut("curves")]
    public IActionResult SetCurve([FromBody] FanCurve curve)
    {
        if (string.IsNullOrWhiteSpace(curve.FanId))
            return BadRequest(new { error = "fan_id is required" });
        if (curve.Points.Count < 2)
            return BadRequest(new { error = "at least 2 curve points required" });

        _fans.SetCurve(curve);
        return Ok(new { ok = true });
    }

    /// <summary>DELETE /api/fans/curves/{fanId} — remove the curve for a fan.</summary>
    [HttpDelete("curves/{fanId}")]
    public IActionResult DeleteCurve(string fanId)
    {
        _fans.DeleteCurve(fanId);
        return Ok(new { ok = true });
    }
}
