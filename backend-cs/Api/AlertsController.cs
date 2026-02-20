using Microsoft.AspNetCore.Mvc;
using DriveChill.Models;
using DriveChill.Services;

namespace DriveChill.Api;

[ApiController]
[Route("api/alerts")]
public sealed class AlertsController : ControllerBase
{
    private readonly AlertService _alerts;

    public AlertsController(AlertService alerts)
    {
        _alerts = alerts;
    }

    // -----------------------------------------------------------------------
    // Rules
    // -----------------------------------------------------------------------

    /// <summary>GET /api/alerts/rules</summary>
    [HttpGet("rules")]
    public IActionResult GetRules() => Ok(_alerts.GetRules());

    /// <summary>POST /api/alerts/rules</summary>
    [HttpPost("rules")]
    public IActionResult CreateRule([FromBody] CreateAlertRuleRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.SensorId))
            return BadRequest(new { error = "sensor_id is required" });

        var rule = _alerts.AddRule(req);
        return CreatedAtAction(nameof(GetRules), new { id = rule.RuleId }, rule);
    }

    /// <summary>DELETE /api/alerts/rules/{ruleId}</summary>
    [HttpDelete("rules/{ruleId}")]
    public IActionResult DeleteRule(string ruleId)
    {
        return _alerts.DeleteRule(ruleId)
            ? Ok(new { ok = true })
            : NotFound(new { error = $"Rule '{ruleId}' not found" });
    }

    // -----------------------------------------------------------------------
    // Events
    // -----------------------------------------------------------------------

    /// <summary>GET /api/alerts/events?limit=100</summary>
    [HttpGet("events")]
    public IActionResult GetEvents([FromQuery] int limit = 100)
        => Ok(_alerts.GetEvents(limit));

    /// <summary>GET /api/alerts/active — currently firing alerts.</summary>
    [HttpGet("active")]
    public IActionResult GetActive() => Ok(_alerts.GetActiveEvents());
}
