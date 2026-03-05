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

    /// <summary>POST /api/fans/speed — set manual speed percent (fan_id in body).</summary>
    [HttpPost("speed")]
    public IActionResult SetSpeed([FromBody] SetSpeedRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.FanId))
            return BadRequest(new { detail = "fan_id is required" });
        if (req.Speed < 0 || req.Speed > 100)
            return BadRequest(new { detail = "speed must be 0–100" });

        var ok = _fans.SetSpeed(req.FanId, req.Speed);
        return ok ? Ok(new { ok = true }) : NotFound(new { detail = $"Fan '{req.FanId}' not found" });
    }

    /// <summary>POST /api/fans/{fanId}/auto — return fan to motherboard control.</summary>
    [HttpPost("{fanId}/auto")]
    public IActionResult SetAuto(string fanId)
    {
        var ok = _fans.SetAuto(fanId);
        return ok ? Ok(new { ok = true }) : NotFound(new { detail = $"Fan '{fanId}' not found" });
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
            return BadRequest(new { detail = "fan_id is required" });
        if (curve.Points.Count < 2)
            return BadRequest(new { detail = "at least 2 curve points required" });

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

    // -----------------------------------------------------------------------
    // Safe mode — release / resume / status
    // -----------------------------------------------------------------------

    /// <summary>POST /api/fans/release — release all fans to BIOS/auto control.</summary>
    [HttpPost("release")]
    public IActionResult ReleaseFanControl()
    {
        _fans.ReleaseFanControl();
        return Ok(new { success = true, message = "Fan control released to BIOS/auto mode" });
    }

    /// <summary>POST /api/fans/resume — resume software fan control.</summary>
    [HttpPost("resume")]
    public IActionResult ResumeFanControl()
    {
        if (!_fans.Resume(out var profile))
            return Conflict(new { detail = "No active profile to resume. Activate a profile first." });
        return Ok(new { success = true, active_profile = profile });
    }

    /// <summary>GET /api/fans/status — safe mode status and applied speeds.</summary>
    [HttpGet("status")]
    public IActionResult GetFanStatus() => Ok(_fans.GetSafeModeStatus());
}
