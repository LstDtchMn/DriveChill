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

    /// <summary>GET /api/settings</summary>
    [HttpGet]
    public IActionResult GetSettings() => Ok(_store.GetAll());

    /// <summary>PUT /api/settings — only accepts safe runtime settings, not security-sensitive data.</summary>
    [HttpPut]
    public IActionResult UpdateSettings([FromBody] SettingsUpdateRequest req)
    {
        var pollMs = Math.Clamp(req.PollIntervalMs, 200, 30_000);
        var retention = Math.Clamp(req.RetentionDays, 1, 365);
        var unit = (req.TempUnit == "C" || req.TempUnit == "F") ? req.TempUnit : "C";

        _store.PollIntervalMs = pollMs;
        _store.RetentionDays  = retention;
        _store.TempUnit       = unit;

        return Ok(new { poll_interval_ms = pollMs, retention_days = retention, temp_unit = unit });
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

/// <summary>Request body for PUT /api/settings — only safe runtime settings.</summary>
public sealed class SettingsUpdateRequest
{
    public int    PollIntervalMs { get; set; } = 1000;
    public int    RetentionDays  { get; set; } = 30;
    public string TempUnit       { get; set; } = "C";
}
