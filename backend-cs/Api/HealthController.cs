using Microsoft.AspNetCore.Mvc;
using DriveChill.Hardware;
using DriveChill.Services;
using System.Text;
using System.Text.RegularExpressions;

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
            api_version  = "v1",
            capabilities = new[]
            {
                "api_keys",
                "webhooks",
                "fan_settings",
                "composite_curves"
            },
            backend      = _hw.GetBackendName(),
            sensor_count = snap.Readings.Count,
            last_updated = snap.Timestamp,
            version      = _appSettings.AppVersion,
        });
    }

    [HttpGet("/metrics")]
    public IActionResult GetMetrics()
    {
        if (!_appSettings.PrometheusEnabled)
            return NotFound(new { error = "Prometheus metrics are disabled. Set DRIVECHILL_PROMETHEUS_ENABLED=true to enable." });

        var snap = _sensors.Latest;
        var sb = new StringBuilder();
        sb.AppendLine("# HELP drivechill_temperature_c Temperature reading in Celsius");
        sb.AppendLine("# TYPE drivechill_temperature_c gauge");
        sb.AppendLine("# HELP drivechill_fan_rpm Fan speed in RPM");
        sb.AppendLine("# TYPE drivechill_fan_rpm gauge");

        foreach (var r in snap.Readings)
        {
            var sid = Regex.Replace(r.Id, @"[^a-zA-Z0-9_.\-]", "_");
            if (r.SensorType is "cpu_temp" or "gpu_temp" or "hdd_temp" or "case_temp")
                sb.AppendLine($"drivechill_temperature_c{{sensor_id=\"{sid}\",sensor_type=\"{r.SensorType}\"}} {r.Value:F3}");
            else if (r.SensorType == "fan_rpm")
                sb.AppendLine($"drivechill_fan_rpm{{sensor_id=\"{sid}\"}} {r.Value:F3}");
        }
        return Content(sb.ToString(), "text/plain; version=0.0.4");
    }
}
