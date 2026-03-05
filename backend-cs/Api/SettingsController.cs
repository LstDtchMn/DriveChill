using Microsoft.AspNetCore.Mvc;
using DriveChill.Services;

namespace DriveChill.Api;

[ApiController]
[Route("api/settings")]
public sealed class SettingsController : ControllerBase
{
    private readonly SettingsStore _store;
    private readonly AppSettings   _appSettings;

    public SettingsController(SettingsStore store, AppSettings appSettings)
    {
        _store       = store;
        _appSettings = appSettings;
    }

    /// <summary>GET /api/settings — returns same field names as Python backend.</summary>
    [HttpGet]
    public IActionResult GetSettings() => Ok(new
    {
        sensor_poll_interval      = _store.PollIntervalMs / 1000.0,
        history_retention_hours   = _store.RetentionDays * 24,
        temp_unit                 = _store.TempUnit,
        hardware_backend          = "lhm",
        backend_name              = _appSettings.AppName,
        fan_ramp_rate_pct_per_sec = _store.FanRampRatePctPerSec,
    });

    /// <summary>PUT /api/settings — accepts same field names as Python backend.</summary>
    [HttpPut]
    public IActionResult UpdateSettings([FromBody] SettingsUpdateRequest req)
    {
        if (req.SensorPollInterval.HasValue)
        {
            var intervalSec = Math.Clamp(req.SensorPollInterval.Value, 0.5, 30.0);
            _store.PollIntervalMs = (int)(intervalSec * 1000);
        }
        if (req.HistoryRetentionHours.HasValue)
        {
            var retentionHours = Math.Clamp(req.HistoryRetentionHours.Value, 1, 8760);
            _store.RetentionDays = Math.Max(1, retentionHours / 24);
        }
        if (req.TempUnit is "C" or "F")
            _store.TempUnit = req.TempUnit;
        if (req.FanRampRatePctPerSec.HasValue)
            _store.FanRampRatePctPerSec = Math.Clamp(req.FanRampRatePctPerSec.Value, 0.1, 100.0);

        return Ok(new
        {
            success = true,
            settings = new
            {
                sensor_poll_interval      = _store.PollIntervalMs / 1000.0,
                history_retention_hours   = _store.RetentionDays * 24,
                temp_unit                 = _store.TempUnit,
                fan_ramp_rate_pct_per_sec = _store.FanRampRatePctPerSec,
            },
        });
    }

    /// <summary>GET /api/settings/info — app version and build info.</summary>
    [HttpGet("info")]
    public IActionResult GetInfo() => Ok(new
    {
        app_name    = _appSettings.AppName,
        version     = _appSettings.AppVersion,
        data_dir    = _appSettings.DataDir,
        platform    = "windows",
        runtime     = "dotnet",
    });
}

/// <summary>Request body for PUT /api/settings — uses same field names as Python backend.</summary>
public sealed class SettingsUpdateRequest
{
    public double? SensorPollInterval    { get; set; }
    public int?    HistoryRetentionHours { get; set; }
    public string  TempUnit              { get; set; } = "C";
    public double? FanRampRatePctPerSec  { get; set; }
}
