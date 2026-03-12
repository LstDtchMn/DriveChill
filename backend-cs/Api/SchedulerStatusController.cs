using DriveChill.Services;
using Microsoft.AspNetCore.Mvc;

namespace DriveChill.Api;

[ApiController]
[Route("api/scheduler")]
public sealed class SchedulerStatusController : ControllerBase
{
    private readonly ProfileSchedulerService _profileScheduler;
    private readonly ReportSchedulerService _reportScheduler;
    private readonly DbService _db;

    public SchedulerStatusController(
        ProfileSchedulerService profileScheduler,
        ReportSchedulerService reportScheduler,
        DbService db)
    {
        _profileScheduler = profileScheduler;
        _reportScheduler = reportScheduler;
        _db = db;
    }

    [HttpGet("status")]
    public async Task<IActionResult> GetStatus(CancellationToken ct = default)
    {
        var schedules = await _db.ListReportSchedulesAsync(ct);
        var now = DateTimeOffset.UtcNow;

        var reportScheduleItems = schedules.Select(s => new
        {
            id = s.Id,
            frequency = s.Frequency,
            time_utc = s.TimeUtc,
            timezone = s.Timezone,
            enabled = s.Enabled,
            last_sent_at = s.LastSentAt,
            last_attempted_at = s.LastAttemptedAt,
            last_error = s.LastError,
            consecutive_failures = s.ConsecutiveFailures,
            next_due_at = s.Enabled ? ReportSchedulerService.NextDueAt(s, now)?.ToString("o") : null,
        }).ToList();

        return Ok(new
        {
            profile_scheduler = new
            {
                running = _profileScheduler.Running,
                active_schedule_id = _profileScheduler.ActiveScheduleId,
                last_check_at = _profileScheduler.LastCheckAt?.ToString("o"),
            },
            report_scheduler = new
            {
                running = _reportScheduler.Running,
                last_check_at = _reportScheduler.LastCheckAt?.ToString("o"),
                schedules = reportScheduleItems,
            },
        });
    }
}
