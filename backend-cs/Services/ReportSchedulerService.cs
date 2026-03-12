using System.Net;
using System.Text;
using DriveChill.Models;

namespace DriveChill.Services;

/// <summary>
/// Background service that checks scheduled analytics reports every 60 seconds
/// and delivers them through the existing email settings.
/// </summary>
public sealed class ReportSchedulerService : BackgroundService
{
    private readonly DbService _db;
    private readonly EmailNotificationService _email;
    private readonly ILogger<ReportSchedulerService> _log;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly TimeSpan _pollInterval;

    /// <summary>When the last schedule check completed (for status API).</summary>
    public DateTimeOffset? LastCheckAt { get; private set; }

    /// <summary>Whether the background loop is running.</summary>
    public bool Running { get; private set; }

    public ReportSchedulerService(
        DbService db,
        EmailNotificationService email,
        ILogger<ReportSchedulerService> log)
        : this(db, email, log, () => DateTimeOffset.UtcNow, TimeSpan.FromSeconds(60))
    {
    }

    internal ReportSchedulerService(
        DbService db,
        EmailNotificationService email,
        ILogger<ReportSchedulerService> log,
        Func<DateTimeOffset> utcNow,
        TimeSpan pollInterval)
    {
        _db = db;
        _email = email;
        _log = log;
        _utcNow = utcNow;
        _pollInterval = pollInterval;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Running = true;
        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await CheckAndSendAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Report schedule check failed");
                }

                await Task.Delay(_pollInterval, stoppingToken);
            }
        }
        finally
        {
            Running = false;
        }
    }

    internal async Task CheckAndSendAsync(CancellationToken ct = default)
    {
        var now = _utcNow();
        var schedules = await _db.ListReportSchedulesAsync(ct);

        foreach (var schedule in schedules.Where(s => s.Enabled))
        {
            if (!IsDue(schedule, now))
                continue;

            var attemptedAt = now.ToString("o");

            try
            {
                if (await SendReportAsync(schedule, now, ct))
                {
                    await _db.UpdateReportScheduleStatusAsync(
                        schedule.Id, now.ToString("o"), attemptedAt, null, 0, ct);
                    _log.LogInformation("Scheduled report {ScheduleId} sent", schedule.Id);
                }
                else
                {
                    var failures = schedule.ConsecutiveFailures + 1;
                    await _db.UpdateReportScheduleStatusAsync(
                        schedule.Id, null, attemptedAt, "Email delivery returned false", failures, ct);
                    _log.LogWarning("Scheduled report {ScheduleId} was due but email delivery failed", schedule.Id);
                }
            }
            catch (Exception ex)
            {
                var failures = schedule.ConsecutiveFailures + 1;
                await _db.UpdateReportScheduleStatusAsync(
                    schedule.Id, null, attemptedAt, ex.Message, failures, ct);
                _log.LogWarning(ex, "Failed to send scheduled report {ScheduleId}", schedule.Id);
            }
        }

        LastCheckAt = now;
    }

    internal async Task<bool> SendReportAsync(ReportScheduleRecord schedule, DateTimeOffset now, CancellationToken ct = default)
    {
        var windowHours = schedule.Frequency == "weekly" ? 168.0 : 24.0;
        var start = now.AddHours(-windowHours);

        var (stats, _, _) = await _db.GetAnalyticsStatsAsync(start, now, null, ct);
        var (anomalies, _, _) = await _db.GetAnalyticsAnomaliesAsync(start, now, null, 3.0, ct);
        var html = BuildReportHtml(stats, anomalies, windowHours, now);

        var frequencyLabel = schedule.Frequency == "weekly" ? "Weekly" : "Daily";
        var subject = $"DriveChill {frequencyLabel} Report - {now:yyyy-MM-dd}";
        return await _email.SendHtmlReportAsync(subject, html, ct);
    }

    internal static DateTimeOffset WeekStartUtc(DateTimeOffset dt)
    {
        var utc = dt.ToUniversalTime();
        var daysFromMonday = ((int)utc.DayOfWeek + 6) % 7;
        var monday = utc.AddDays(-daysFromMonday);
        return new DateTimeOffset(monday.Year, monday.Month, monday.Day, 0, 0, 0, TimeSpan.Zero);
    }

    internal static bool IsDue(ReportScheduleRecord schedule, DateTimeOffset now)
    {
        var parts = schedule.TimeUtc.Split(':');
        if (parts.Length != 2
            || !int.TryParse(parts[0], out var hour)
            || !int.TryParse(parts[1], out var minute))
        {
            return false;
        }

        // Convert to the schedule's timezone before comparing hour/minute
        TimeZoneInfo tz;
        try { tz = TimeZoneInfo.FindSystemTimeZoneById(schedule.Timezone ?? "UTC"); }
        catch (TimeZoneNotFoundException) { tz = TimeZoneInfo.Utc; }
        var localNow = TimeZoneInfo.ConvertTime(now, tz);

        if (localNow.Hour != hour || localNow.Minute != minute)
            return false;

        if (string.IsNullOrWhiteSpace(schedule.LastSentAt))
            return true;

        if (!DateTimeOffset.TryParse(schedule.LastSentAt, out var lastSent))
            return true;

        lastSent = lastSent.ToUniversalTime();
        now = now.ToUniversalTime();

        return schedule.Frequency switch
        {
            "daily" => lastSent < now.Date,
            "weekly" => lastSent < WeekStartUtc(now),
            _ => false,
        };
    }

    /// <summary>
    /// Compute the next UTC time this schedule will fire, starting from <paramref name="now"/>.
    /// Returns null if the schedule has an invalid time_utc.
    /// </summary>
    internal static DateTimeOffset? NextDueAt(ReportScheduleRecord schedule, DateTimeOffset now)
    {
        var parts = schedule.TimeUtc.Split(':');
        if (parts.Length != 2
            || !int.TryParse(parts[0], out var hour)
            || !int.TryParse(parts[1], out var minute))
        {
            return null;
        }

        // Resolve timezone
        TimeZoneInfo tz;
        try
        {
            tz = TimeZoneInfo.FindSystemTimeZoneById(schedule.Timezone ?? "UTC");
        }
        catch (TimeZoneNotFoundException)
        {
            tz = TimeZoneInfo.Utc;
        }

        var localNow = TimeZoneInfo.ConvertTime(now, tz);

        // Build candidate: today at the scheduled time in the schedule's timezone
        var candidateLocal = new DateTimeOffset(
            localNow.Year, localNow.Month, localNow.Day, hour, minute, 0, localNow.Offset);

        // If that time is in the past (or right now), advance by 1 day (daily) or 7 days (weekly)
        if (candidateLocal <= localNow)
        {
            candidateLocal = candidateLocal.AddDays(schedule.Frequency == "weekly" ? 7 : 1);
        }

        return candidateLocal.ToUniversalTime();
    }

    internal static string BuildReportHtml(
        IReadOnlyList<AnalyticsStat> stats,
        IReadOnlyList<AnalyticsAnomaly> anomalies,
        double windowHours,
        DateTimeOffset now)
    {
        var statsTable = BuildStatsTable(stats);
        var anomaliesTable = BuildAnomaliesTable(anomalies);

        return $$"""
<!DOCTYPE html>
<html>
<head>
<meta charset="utf-8">
<title>DriveChill Analytics Report</title>
<style>
  body { font-family: Arial, sans-serif; font-size: 14px; color: #1a1a2e; background: #f4f6fb; margin: 0; padding: 24px; }
  h1 { font-size: 20px; color: #2563eb; margin-bottom: 4px; }
  h2 { font-size: 15px; color: #374151; border-bottom: 1px solid #e2e8f0; padding-bottom: 4px; margin-top: 24px; }
  .meta { color: #6b7280; font-size: 12px; margin-bottom: 20px; }
  table { width: 100%; border-collapse: collapse; font-size: 13px; margin-top: 8px; }
  th { background: #2563eb; color: #fff; text-align: left; padding: 6px 10px; }
  td { padding: 5px 10px; border-bottom: 1px solid #e2e8f0; }
  tr:nth-child(even) td { background: #f0f4ff; }
  .badge-warn { background: #fef3c7; color: #92400e; padding: 1px 6px; border-radius: 4px; font-size: 11px; }
  .badge-crit { background: #fee2e2; color: #991b1b; padding: 1px 6px; border-radius: 4px; font-size: 11px; }
  .none { color: #6b7280; font-style: italic; }
</style>
</head>
<body>
<h1>DriveChill Analytics Report</h1>
<p class="meta">Generated: {{now:yyyy-MM-dd HH:mm}} UTC | Window: last {{windowHours:0}} hours</p>

<h2>Sensor Statistics</h2>
{{statsTable}}

<h2>Anomalies Detected</h2>
{{anomaliesTable}}

<p style="color:#9ca3af;font-size:11px;margin-top:32px;">
  This report was sent automatically by DriveChill. To manage schedules, open Settings -> Report Schedules.
</p>
</body>
</html>
""";
    }

    private static string BuildStatsTable(IReadOnlyList<AnalyticsStat> stats)
    {
        if (stats.Count == 0)
            return "<p class=\"none\">No sensor data in this window.</p>";

        var sb = new StringBuilder();
        sb.Append("<table><thead><tr><th>Sensor</th><th>Type</th><th>Avg</th><th>Min</th><th>Max</th><th>Samples</th></tr></thead><tbody>");
        foreach (var stat in stats)
        {
            sb.Append("<tr>")
              .Append("<td>").Append(WebUtility.HtmlEncode(stat.SensorName)).Append("</td>")
              .Append("<td>").Append(WebUtility.HtmlEncode(stat.SensorType)).Append("</td>")
              .Append("<td>").Append(stat.AvgValue.ToString("F1")).Append(' ').Append(WebUtility.HtmlEncode(stat.Unit)).Append("</td>")
              .Append("<td>").Append(stat.MinValue.ToString("F1")).Append("</td>")
              .Append("<td>").Append(stat.MaxValue.ToString("F1")).Append("</td>")
              .Append("<td>").Append(stat.SampleCount).Append("</td>")
              .Append("</tr>");
        }
        sb.Append("</tbody></table>");
        return sb.ToString();
    }

    private static string BuildAnomaliesTable(IReadOnlyList<AnalyticsAnomaly> anomalies)
    {
        if (anomalies.Count == 0)
            return "<p class=\"none\">No anomalies detected.</p>";

        var sb = new StringBuilder();
        sb.Append("<table><thead><tr><th>Time</th><th>Sensor</th><th>Value</th><th>Z-score</th><th>Severity</th></tr></thead><tbody>");
        foreach (var anomaly in anomalies.Take(50))
        {
            var badgeClass = anomaly.Severity == "critical" ? "badge-crit" : "badge-warn";
            sb.Append("<tr>")
              .Append("<td>").Append(WebUtility.HtmlEncode(anomaly.TimestampUtc)).Append("</td>")
              .Append("<td>").Append(WebUtility.HtmlEncode(anomaly.SensorName)).Append("</td>")
              .Append("<td>").Append(anomaly.Value.ToString("F1")).Append(' ').Append(WebUtility.HtmlEncode(anomaly.Unit)).Append("</td>")
              .Append("<td>").Append(anomaly.ZScore.ToString("F2")).Append("</td>")
              .Append("<td><span class=\"").Append(badgeClass).Append("\">")
              .Append(WebUtility.HtmlEncode(anomaly.Severity)).Append("</span></td>")
              .Append("</tr>");
        }
        sb.Append("</tbody></table>");
        return sb.ToString();
    }
}
