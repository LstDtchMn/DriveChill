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

    /// <summary>PUT /api/settings</summary>
    [HttpPut]
    public IActionResult UpdateSettings([FromBody] StoredData data)
    {
        // Clamp values to sensible ranges
        data.PollIntervalMs = Math.Clamp(data.PollIntervalMs, 200, 30_000);
        data.RetentionDays  = Math.Clamp(data.RetentionDays, 1, 365);
        if (data.TempUnit != "C" && data.TempUnit != "F")
            data.TempUnit = "C";

        _store.SetAll(data);
        return Ok(data);
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
