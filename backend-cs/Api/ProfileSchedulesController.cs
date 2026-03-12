using Microsoft.AspNetCore.Mvc;
using DriveChill.Models;
using DriveChill.Services;
using System.Text.RegularExpressions;

namespace DriveChill.Api;

[ApiController]
[Route("api/profile-schedules")]
public sealed partial class ProfileSchedulesController : ControllerBase
{
    private readonly DbService    _db;

    public ProfileSchedulesController(DbService db)
    {
        _db    = db;
    }

    /// <summary>GET /api/profile-schedules — list all schedules.</summary>
    [HttpGet]
    public async Task<IActionResult> GetSchedules(CancellationToken ct = default)
    {
        var schedules = await _db.GetProfileSchedulesAsync(ct);
        return Ok(new { schedules });
    }

    /// <summary>POST /api/profile-schedules — create a new schedule.</summary>
    [HttpPost]
    public async Task<IActionResult> CreateSchedule([FromBody] ProfileScheduleRequest body, CancellationToken ct = default)
    {
        var error = ValidateSchedule(body);
        if (error != null) return UnprocessableEntity(new { detail = error });

        // Verify profile exists
        var profiles = await _db.ListProfilesAsync(ct);
        if (!profiles.Any(p => p.Id == body.ProfileId))
            return NotFound(new { detail = $"Profile '{body.ProfileId}' not found" });

        var schedule = new ProfileScheduleRecord
        {
            Id         = $"psched_{Guid.NewGuid().ToString("N")[..12]}",
            ProfileId  = body.ProfileId,
            StartTime  = body.StartTime,
            EndTime    = body.EndTime,
            DaysOfWeek = body.DaysOfWeek,
            Timezone   = body.Timezone ?? "UTC",
            Enabled    = body.Enabled ?? true,
            CreatedAt  = DateTimeOffset.UtcNow.ToString("o"),
        };

        await _db.CreateProfileScheduleAsync(schedule, ct);
        return Ok(new
        {
            id          = schedule.Id,
            profile_id  = schedule.ProfileId,
            start_time  = schedule.StartTime,
            end_time    = schedule.EndTime,
            days_of_week = schedule.DaysOfWeek,
            timezone    = schedule.Timezone,
            enabled     = schedule.Enabled,
            created_at  = schedule.CreatedAt,
        });
    }

    /// <summary>PUT /api/profile-schedules/{id} — update a schedule.</summary>
    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateSchedule(string id, [FromBody] ProfileScheduleRequest body,
        CancellationToken ct = default)
    {
        var error = ValidateSchedule(body);
        if (error != null) return UnprocessableEntity(new { detail = error });

        var profiles = await _db.ListProfilesAsync(ct);
        if (!profiles.Any(p => p.Id == body.ProfileId))
            return NotFound(new { detail = $"Profile '{body.ProfileId}' not found" });

        var schedule = new ProfileScheduleRecord
        {
            ProfileId  = body.ProfileId,
            StartTime  = body.StartTime,
            EndTime    = body.EndTime,
            DaysOfWeek = body.DaysOfWeek,
            Timezone   = body.Timezone ?? "UTC",
            Enabled    = body.Enabled ?? true,
        };

        var updated = await _db.UpdateProfileScheduleAsync(id, schedule, ct);
        return updated ? Ok(new { success = true }) : NotFound(new { detail = "Schedule not found" });
    }

    /// <summary>DELETE /api/profile-schedules/{id} — delete a schedule.</summary>
    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteSchedule(string id, CancellationToken ct = default)
    {
        var deleted = await _db.DeleteProfileScheduleAsync(id, ct);
        return deleted ? NoContent() : NotFound(new { detail = "Schedule not found" });
    }

    // -----------------------------------------------------------------------

    [GeneratedRegex(@"^\d{2}:\d{2}$")]
    private static partial Regex TimeRegex();

    private static string? ValidateSchedule(ProfileScheduleRequest body)
    {
        if (string.IsNullOrWhiteSpace(body.ProfileId)) return "profile_id is required";
        if (!TimeRegex().IsMatch(body.StartTime)) return "start_time must be HH:MM format";
        if (!TimeRegex().IsMatch(body.EndTime)) return "end_time must be HH:MM format";
        if (!ValidateTime(body.StartTime)) return "start_time is invalid";
        if (!ValidateTime(body.EndTime)) return "end_time is invalid";
        if (string.IsNullOrWhiteSpace(body.DaysOfWeek)) return "days_of_week is required";

        foreach (var d in body.DaysOfWeek.Split(','))
        {
            if (!int.TryParse(d.Trim(), out var day) || day < 0 || day > 6)
                return "days_of_week must be comma-separated integers 0-6";
        }

        return null;
    }

    private static bool ValidateTime(string time)
    {
        var parts = time.Split(':');
        return int.TryParse(parts[0], out var h) && int.TryParse(parts[1], out var m)
            && h is >= 0 and <= 23 && m is >= 0 and <= 59;
    }
}

/// <summary>Request body for profile schedule create/update.</summary>
public sealed class ProfileScheduleRequest
{
    public string ProfileId { get; set; } = "";
    public string StartTime { get; set; } = "00:00";
    public string EndTime { get; set; } = "00:00";
    public string DaysOfWeek { get; set; } = "0,1,2,3,4,5,6";
    public string? Timezone { get; set; }
    public bool? Enabled { get; set; }
}
