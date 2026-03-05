using Microsoft.AspNetCore.Mvc;
using DriveChill.Services;
using System.Text.RegularExpressions;

namespace DriveChill.Api;

[ApiController]
[Route("api/quiet-hours")]
public sealed partial class QuietHoursController : ControllerBase
{
    private readonly DbService      _db;
    private readonly SettingsStore   _store;

    public QuietHoursController(DbService db, SettingsStore store)
    {
        _db    = db;
        _store = store;
    }

    /// <summary>GET /api/quiet-hours — list all rules.</summary>
    [HttpGet]
    public async Task<IActionResult> GetRules(CancellationToken ct = default)
    {
        var rules = await _db.GetQuietHoursAsync(ct);
        return Ok(new { rules });
    }

    /// <summary>POST /api/quiet-hours — create a new rule.</summary>
    [HttpPost]
    public async Task<IActionResult> CreateRule([FromBody] QuietHoursRule rule, CancellationToken ct = default)
    {
        var error = ValidateRule(rule);
        if (error != null) return UnprocessableEntity(new { detail = error });

        // Verify profile exists
        var profiles = _store.LoadProfiles();
        if (!profiles.Any(p => p.Id == rule.ProfileId))
            return NotFound(new { detail = $"Profile '{rule.ProfileId}' not found" });

        var id = await _db.CreateQuietHoursAsync(rule, ct);
        return Ok(new { success = true, id });
    }

    /// <summary>PUT /api/quiet-hours/{ruleId} — update a rule.</summary>
    [HttpPut("{ruleId:int}")]
    public async Task<IActionResult> UpdateRule(int ruleId, [FromBody] QuietHoursRule rule,
        CancellationToken ct = default)
    {
        var error = ValidateRule(rule);
        if (error != null) return UnprocessableEntity(new { detail = error });

        var profiles = _store.LoadProfiles();
        if (!profiles.Any(p => p.Id == rule.ProfileId))
            return NotFound(new { detail = $"Profile '{rule.ProfileId}' not found" });

        var updated = await _db.UpdateQuietHoursAsync(ruleId, rule, ct);
        return updated ? Ok(new { success = true }) : NotFound(new { detail = "Rule not found" });
    }

    /// <summary>DELETE /api/quiet-hours/{ruleId} — delete a rule.</summary>
    [HttpDelete("{ruleId:int}")]
    public async Task<IActionResult> DeleteRule(int ruleId, CancellationToken ct = default)
    {
        var deleted = await _db.DeleteQuietHoursAsync(ruleId, ct);
        return deleted ? Ok(new { success = true }) : NotFound(new { detail = "Rule not found" });
    }

    // -----------------------------------------------------------------------

    [GeneratedRegex(@"^\d{2}:\d{2}$")]
    private static partial Regex TimeRegex();

    private static string? ValidateRule(QuietHoursRule rule)
    {
        if (rule.DayOfWeek is < 0 or > 6) return "day_of_week must be 0-6";
        if (!TimeRegex().IsMatch(rule.StartTime)) return "start_time must be HH:MM format";
        if (!TimeRegex().IsMatch(rule.EndTime)) return "end_time must be HH:MM format";
        if (!ValidateTime(rule.StartTime)) return "start_time is invalid";
        if (!ValidateTime(rule.EndTime)) return "end_time is invalid";
        if (string.IsNullOrWhiteSpace(rule.ProfileId)) return "profile_id is required";
        return null;
    }

    private static bool ValidateTime(string time)
    {
        var parts = time.Split(':');
        return int.TryParse(parts[0], out var h) && int.TryParse(parts[1], out var m)
            && h is >= 0 and <= 23 && m is >= 0 and <= 59;
    }
}
