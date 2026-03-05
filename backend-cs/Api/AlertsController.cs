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
    // Combined endpoint (parity with Python GET /api/alerts)
    // -----------------------------------------------------------------------

    /// <summary>GET /api/alerts — returns rules, recent events, and active alert IDs.</summary>
    [HttpGet("")]
    public IActionResult GetAll()
        => Ok(new { rules = _alerts.GetRules(), events = _alerts.GetEvents(50), active = _alerts.GetActiveEvents() });

    // -----------------------------------------------------------------------
    // Rules
    // -----------------------------------------------------------------------

    /// <summary>GET /api/alerts/rules</summary>
    [HttpGet("rules")]
    public IActionResult GetRules() => Ok(new { rules = _alerts.GetRules() });

    /// <summary>POST /api/alerts/rules</summary>
    [HttpPost("rules")]
    public IActionResult CreateRule([FromBody] CreateAlertRuleRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.SensorId))
            return BadRequest(new { detail = "sensor_id is required" });

        var rule = _alerts.AddRule(req);
        return CreatedAtAction(nameof(GetRules), new { id = rule.RuleId }, new { success = true, rule });
    }

    /// <summary>DELETE /api/alerts/rules/{ruleId}</summary>
    [HttpDelete("rules/{ruleId}")]
    public IActionResult DeleteRule(string ruleId)
    {
        return _alerts.DeleteRule(ruleId)
            ? Ok(new { success = true })
            : NotFound(new { detail = $"Rule '{ruleId}' not found" });
    }

    // -----------------------------------------------------------------------
    // Events
    // -----------------------------------------------------------------------

    /// <summary>GET /api/alerts/events?limit=100</summary>
    [HttpGet("events")]
    public IActionResult GetEvents([FromQuery] int limit = 100)
        => Ok(new { events = _alerts.GetEvents(limit) });

    /// <summary>GET /api/alerts/active — currently firing alerts.</summary>
    [HttpGet("active")]
    public IActionResult GetActive() => Ok(new { active = _alerts.GetActiveEvents() });

    /// <summary>POST /api/alerts/clear — clear all events.</summary>
    [HttpPost("clear")]
    public IActionResult ClearEvents()
    {
        _alerts.ClearEvents();
        return Ok(new { success = true });
    }
}
