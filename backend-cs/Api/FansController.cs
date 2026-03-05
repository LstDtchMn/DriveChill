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
    private readonly DbService     _db;

    private const double DangerTempThreshold  = 75.0;
    private const double DangerSpeedThreshold = 20.0;

    public FansController(FanService fans, SensorService sensors, DbService db)
    {
        _fans    = fans;
        _sensors = sensors;
        _db      = db;
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

    /// <summary>PUT /api/fans/curves — upsert a fan curve (with dangerous-speed check).</summary>
    [HttpPut("curves")]
    public IActionResult SetCurve([FromBody] UpdateCurveRequest req)
    {
        var curve = req.Curve;
        if (string.IsNullOrWhiteSpace(curve.FanId))
            return BadRequest(new { detail = "fan_id is required" });
        if (curve.Points.Count < 2)
            return BadRequest(new { detail = "at least 2 curve points required" });

        var warnings = CheckDangerousCurve(curve.Points);
        if (warnings.Count > 0 && !req.AllowDangerous)
        {
            return Conflict(new
            {
                detail = new
                {
                    message  = "Curve has dangerous speed settings at high temperatures. " +
                               "Set allow_dangerous=true to override.",
                    warnings,
                },
            });
        }

        _fans.SetCurve(curve);

        var resp = new Dictionary<string, object> { ["success"] = true, ["curve"] = curve };
        if (warnings.Count > 0)
        {
            resp["dangerous_curve_warnings"] = warnings;
            resp["override_logged"] = true;
        }
        return Ok(resp);
    }

    /// <summary>POST /api/fans/curves/validate — pre-check for dangerous speeds without saving.</summary>
    [HttpPost("curves/validate")]
    public IActionResult ValidateCurve([FromBody] ValidateCurveRequest req)
    {
        var warnings = CheckDangerousCurve(req.Points);
        return Ok(new { safe = warnings.Count == 0, warnings });
    }

    /// <summary>DELETE /api/fans/curves/{curveId} — remove a fan curve by ID.</summary>
    [HttpDelete("curves/{curveId}")]
    public IActionResult DeleteCurve(string curveId)
    {
        _fans.DeleteCurve(curveId);
        return Ok(new { ok = true });
    }

    // -----------------------------------------------------------------------
    // Fan settings (min speed floor, zero-RPM capability)
    // -----------------------------------------------------------------------

    /// <summary>GET /api/fans/settings — per-fan settings for all fans.</summary>
    [HttpGet("settings")]
    public async Task<IActionResult> GetAllFanSettings(CancellationToken ct = default)
    {
        var all = await _db.GetAllFanSettingsAsync(ct);
        return Ok(new { fan_settings = all });
    }

    /// <summary>GET /api/fans/{fanId}/settings — per-fan settings for one fan.</summary>
    [HttpGet("{fanId}/settings")]
    public async Task<IActionResult> GetFanSettings(string fanId, CancellationToken ct = default)
    {
        var fs = await _db.GetFanSettingAsync(fanId, ct);
        return Ok(new
        {
            fan_id           = fanId,
            min_speed_pct    = fs?.MinSpeedPct    ?? 0.0,
            zero_rpm_capable = fs?.ZeroRpmCapable ?? false,
        });
    }

    /// <summary>PUT /api/fans/{fanId}/settings — update per-fan settings.</summary>
    [HttpPut("{fanId}/settings")]
    public async Task<IActionResult> UpdateFanSettings(string fanId,
        [FromBody] UpdateFanSettingsRequest req, CancellationToken ct = default)
    {
        if (req.MinSpeedPct < 0 || req.MinSpeedPct > 100)
            return BadRequest(new { detail = "min_speed_pct must be 0–100" });

        await _db.SetFanSettingAsync(fanId, req.MinSpeedPct, req.ZeroRpmCapable, ct);
        _fans.UpdateFanSettings(fanId, req.MinSpeedPct, req.ZeroRpmCapable);
        return Ok(new
        {
            success          = true,
            fan_id           = fanId,
            min_speed_pct    = req.MinSpeedPct,
            zero_rpm_capable = req.ZeroRpmCapable,
        });
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

    // -----------------------------------------------------------------------
    // Dangerous-curve detection (mirrors Python curve_engine.check_dangerous_curve)
    // -----------------------------------------------------------------------

    private static List<DangerWarning> CheckDangerousCurve(List<FanCurvePoint> points)
    {
        var warnings = new List<DangerWarning>();

        // Check explicit points
        foreach (var pt in points)
        {
            if (pt.Temp > DangerTempThreshold && pt.Speed < DangerSpeedThreshold)
            {
                warnings.Add(new DangerWarning
                {
                    Temp    = pt.Temp,
                    Speed   = pt.Speed,
                    Message = $"Fan speed {pt.Speed:F0}% at {pt.Temp:F0}°C is dangerously low. " +
                              $"Temperatures above {DangerTempThreshold:F0}°C with fans below " +
                              $"{DangerSpeedThreshold:F0}% risk thermal damage.",
                });
            }
        }

        // Check interpolated values at key high-temp checkpoints
        if (points.Count > 0)
        {
            var explicitTemps = points.Select(p => p.Temp).ToHashSet();
            foreach (var checkTemp in new[] { 80.0, 85.0, 90.0, 95.0, 100.0 })
            {
                if (explicitTemps.Contains(checkTemp)) continue;
                var speed = Interpolate(points, checkTemp);
                if (speed < DangerSpeedThreshold)
                {
                    // De-duplicate: skip if an existing warning is within 5°C at the same speed
                    var alreadyWarned = warnings.Any(w =>
                        Math.Abs(w.Temp - checkTemp) <= 5 && Math.Abs(w.Speed - speed) < 1);
                    if (!alreadyWarned)
                    {
                        warnings.Add(new DangerWarning
                        {
                            Temp    = checkTemp,
                            Speed   = Math.Round(speed, 1),
                            Message = $"Interpolated fan speed is {speed:F0}% at {checkTemp:F0}°C. " +
                                      "This could allow dangerous temperatures.",
                        });
                    }
                }
            }
        }

        return warnings;
    }

    private static double Interpolate(List<FanCurvePoint> points, double temp)
    {
        var sorted = points.OrderBy(p => p.Temp).ToList();
        if (temp <= sorted[0].Temp)  return sorted[0].Speed;
        if (temp >= sorted[^1].Temp) return sorted[^1].Speed;
        for (int i = 1; i < sorted.Count; i++)
        {
            if (temp <= sorted[i].Temp)
            {
                var lo = sorted[i - 1];
                var hi = sorted[i];
                return lo.Speed + (temp - lo.Temp) / (hi.Temp - lo.Temp) * (hi.Speed - lo.Speed);
            }
        }
        return sorted[^1].Speed;
    }
}
