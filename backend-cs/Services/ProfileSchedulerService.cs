using DriveChill.Models;

namespace DriveChill.Services;

/// <summary>
/// Background service that checks profile schedules every 60 seconds and
/// activates the matching profile — but only if the system is NOT in panic
/// mode, NOT in an alert-triggered profile switch, and NOT in quiet hours.
///
/// Priority order (highest to lowest):
///   1. Panic mode (always wins)
///   2. Alert-triggered profile (safety)
///   3. Quiet hours (explicit user silence preference)
///   4. Profile schedule (automation convenience)
///   5. Manual profile selection (default)
/// </summary>
public sealed class ProfileSchedulerService : BackgroundService
{
    private readonly DbService     _db;
    private readonly SettingsStore  _store;
    private readonly FanService    _fans;
    private readonly AlertService  _alerts;
    private readonly ILogger<ProfileSchedulerService> _log;

    /// <summary>Track which schedule is currently applied to avoid re-activating every 60s.</summary>
    private string? _activeScheduleId;

    public ProfileSchedulerService(
        DbService db,
        SettingsStore store,
        FanService fans,
        AlertService alerts,
        ILogger<ProfileSchedulerService> log)
    {
        _db     = db;
        _store  = store;
        _fans   = fans;
        _alerts = alerts;
        _log    = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Small delay so other services can initialise first
        await Task.Delay(2000, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckScheduleAsync(stoppingToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Profile schedule check failed");
            }

            await Task.Delay(60_000, stoppingToken);
        }
    }

    private async Task CheckScheduleAsync(CancellationToken ct)
    {
        // Higher-priority overrides: panic, alert-triggered, quiet hours
        if (_fans.IsInPanic)
            return;
        if (_alerts.HasActiveProfileSwitch)
            return;
        // Check quiet hours: if the SensorWorker's quiet hours logic has an active rule
        // we defer. We check via DbService if any quiet hours rule matches now.
        if (await IsQuietHoursActiveAsync(ct))
            return;

        var schedules = await _db.GetProfileSchedulesAsync(ct);
        var matched = FindActiveSchedule(schedules);

        if (matched == null)
        {
            if (_activeScheduleId != null)
                _activeScheduleId = null;
            return;
        }

        if (_activeScheduleId == matched.Id)
            return;

        // Activate the profile
        var profiles = _store.LoadProfiles().ToList();
        var profile = profiles.FirstOrDefault(p => p.Id == matched.ProfileId);
        if (profile == null)
        {
            _log.LogWarning("Profile schedule {ScheduleId}: profile {ProfileId} not found",
                matched.Id, matched.ProfileId);
            return;
        }

        _activeScheduleId = matched.Id;

        foreach (var p in profiles) p.IsActive = p.Id == matched.ProfileId;
        _store.SaveProfiles(profiles);
        _fans.SetCurves(profile.Curves);
        _log.LogInformation("Profile schedule: activated profile {ProfileId} (schedule {ScheduleId})",
            matched.ProfileId, matched.Id);
    }

    private async Task<bool> IsQuietHoursActiveAsync(CancellationToken ct)
    {
        var rules = await _db.GetQuietHoursAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var dow = (int)now.DayOfWeek;
        // Convert .NET DayOfWeek (0=Sunday) to Python convention (0=Monday)
        dow = dow == 0 ? 6 : dow - 1;
        var currentTime = now.ToString("HH:mm");

        foreach (var rule in rules)
        {
            if (!rule.Enabled) continue;
            if (rule.DayOfWeek != dow) continue;

            if (string.Compare(rule.StartTime, rule.EndTime, StringComparison.Ordinal) <= 0)
            {
                if (string.Compare(currentTime, rule.StartTime, StringComparison.Ordinal) >= 0
                    && string.Compare(currentTime, rule.EndTime, StringComparison.Ordinal) < 0)
                    return true;
            }
            else
            {
                // Overnight span
                if (string.Compare(currentTime, rule.StartTime, StringComparison.Ordinal) >= 0
                    || string.Compare(currentTime, rule.EndTime, StringComparison.Ordinal) < 0)
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Find the best matching schedule for the current UTC time.
    /// Most specific (fewest days) wins; ties broken by most recently created.
    /// </summary>
    internal static ProfileScheduleRecord? FindActiveSchedule(List<ProfileScheduleRecord> schedules)
    {
        var utcNow = DateTimeOffset.UtcNow;

        var matching = new List<ProfileScheduleRecord>();

        foreach (var schedule in schedules)
        {
            if (!schedule.Enabled) continue;

            // Convert UTC now to the schedule's local timezone for comparison
            // Supports both IANA (e.g. "America/New_York") and Windows timezone IDs
            DateTimeOffset localNow;
            try
            {
                var tz = TimeZoneInfo.FindSystemTimeZoneById(schedule.Timezone ?? "UTC");
                localNow = TimeZoneInfo.ConvertTime(utcNow, tz);
            }
            catch (TimeZoneNotFoundException)
            {
                localNow = utcNow; // fall back to UTC if timezone is invalid
            }

            var dow = (int)localNow.DayOfWeek;
            // Convert .NET DayOfWeek (0=Sunday) to Python convention (0=Monday)
            dow = dow == 0 ? 6 : dow - 1;
            var currentTime = localNow.ToString("HH:mm");

            var days = schedule.DaysOfWeek.Split(',')
                .Select(d => d.Trim())
                .Where(d => int.TryParse(d, out _))
                .Select(int.Parse)
                .ToList();
            if (!days.Contains(dow)) continue;

            var start = schedule.StartTime;
            var end = schedule.EndTime;

            if (string.Compare(start, end, StringComparison.Ordinal) <= 0)
            {
                if (string.Compare(currentTime, start, StringComparison.Ordinal) >= 0
                    && string.Compare(currentTime, end, StringComparison.Ordinal) < 0)
                    matching.Add(schedule);
            }
            else
            {
                // Overnight span
                if (string.Compare(currentTime, start, StringComparison.Ordinal) >= 0
                    || string.Compare(currentTime, end, StringComparison.Ordinal) < 0)
                    matching.Add(schedule);
            }
        }

        if (matching.Count == 0) return null;

        // Most specific (fewest days) wins, then most recently created
        return matching
            .OrderBy(s => s.DaysOfWeek.Split(',').Length)
            .ThenByDescending(s => s.CreatedAt)
            .First();
    }
}
