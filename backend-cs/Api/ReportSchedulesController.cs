using System.Text.RegularExpressions;
using DriveChill.Models;
using DriveChill.Services;
using Microsoft.AspNetCore.Mvc;

namespace DriveChill.Api;

[ApiController]
[Route("api/report-schedules")]
public sealed partial class ReportSchedulesController : ControllerBase
{
    private static readonly HashSet<string> ValidFrequencies = ["daily", "weekly"];
    private readonly DbService _db;

    public ReportSchedulesController(DbService db) => _db = db;

    [HttpGet("")]
    public async Task<IActionResult> List(CancellationToken ct = default)
        => Ok(new { schedules = await _db.ListReportSchedulesAsync(ct) });

    [HttpPost("")]
    public async Task<IActionResult> Create([FromBody] ReportScheduleCreateRequest body, CancellationToken ct = default)
    {
        var error = ValidateCreate(body);
        if (error is not null)
            return UnprocessableEntity(new { detail = error });

        var schedule = new ReportScheduleRecord
        {
            Id = $"rs_{Guid.NewGuid().ToString("N")[..12]}",
            Frequency = body.Frequency,
            TimeUtc = body.TimeUtc,
            Timezone = body.Timezone ?? "UTC",
            Enabled = body.Enabled ?? true,
            CreatedAt = DateTimeOffset.UtcNow.ToString("o"),
        };

        await _db.CreateReportScheduleAsync(schedule, ct);
        return Ok(schedule);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(string id, [FromBody] ReportScheduleUpdateRequest body, CancellationToken ct = default)
    {
        var existing = await _db.GetReportScheduleAsync(id, ct);
        if (existing is null)
            return NotFound(new { detail = "Report schedule not found" });

        if (!body.HasUpdates)
            return BadRequest(new { detail = "No fields to update" });

        var error = ValidateUpdate(body);
        if (error is not null)
            return UnprocessableEntity(new { detail = error });

        var updated = await _db.UpdateReportScheduleAsync(
            id,
            body.Frequency,
            body.TimeUtc,
            body.Timezone,
            body.Enabled,
            ct);

        return updated is not null ? Ok(updated) : NotFound(new { detail = "Report schedule not found" });
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct = default)
    {
        var deleted = await _db.DeleteReportScheduleAsync(id, ct);
        return deleted ? Ok(new { success = true }) : NotFound(new { detail = "Report schedule not found" });
    }

    [GeneratedRegex(@"^\d{2}:\d{2}$")]
    private static partial Regex TimeRegex();

    private static string? ValidateCreate(ReportScheduleCreateRequest body)
    {
        if (!ValidFrequencies.Contains(body.Frequency))
            return "frequency must be 'daily' or 'weekly'";
        return ValidateTime(body.TimeUtc);
    }

    private static string? ValidateUpdate(ReportScheduleUpdateRequest body)
    {
        if (body.Frequency is not null && !ValidFrequencies.Contains(body.Frequency))
            return "frequency must be 'daily' or 'weekly'";
        if (body.TimeUtc is not null)
            return ValidateTime(body.TimeUtc);
        return null;
    }

    private static string? ValidateTime(string timeUtc)
    {
        if (!TimeRegex().IsMatch(timeUtc))
            return "time_utc must be HH:MM format";
        var parts = timeUtc.Split(':');
        return int.TryParse(parts[0], out var h)
            && int.TryParse(parts[1], out var m)
            && h is >= 0 and <= 23
            && m is >= 0 and <= 59
            ? null
            : "time_utc is invalid";
    }
}

public sealed class ReportScheduleCreateRequest
{
    public string Frequency { get; set; } = "daily";
    public string TimeUtc { get; set; } = "08:00";
    public string? Timezone { get; set; }
    public bool? Enabled { get; set; }
}

public sealed class ReportScheduleUpdateRequest
{
    public string? Frequency { get; set; }
    public string? TimeUtc { get; set; }
    public string? Timezone { get; set; }
    public bool? Enabled { get; set; }

    public bool HasUpdates =>
        Frequency is not null || TimeUtc is not null || Timezone is not null || Enabled.HasValue;
}
