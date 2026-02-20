using Microsoft.AspNetCore.Mvc;
using DriveChill.Hardware;
using DriveChill.Services;

namespace DriveChill.Api;

[ApiController]
[Route("api/health")]
public sealed class HealthController : ControllerBase
{
    private readonly IHardwareBackend _hw;
    private readonly SensorService    _sensors;
    private readonly AppSettings      _appSettings;

    public HealthController(IHardwareBackend hw, SensorService sensors, AppSettings appSettings)
    {
        _hw          = hw;
        _sensors     = sensors;
        _appSettings = appSettings;
    }

    /// <summary>GET /api/health</summary>
    [HttpGet]
    public IActionResult GetHealth()
    {
        var snap = _sensors.Latest;
        return Ok(new
        {
            status       = "ok",
            backend      = _hw.GetBackendName(),
            sensor_count = snap.Readings.Count,
            last_updated = snap.Timestamp,
            version      = _appSettings.AppVersion,
        });
    }
}
