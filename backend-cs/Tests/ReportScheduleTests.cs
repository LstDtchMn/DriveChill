using System.IO;
using System.Text.Json;
using DriveChill.Api;
using DriveChill.Models;
using DriveChill.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DriveChill.Tests;

public sealed class ReportScheduleTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DbService _db;
    private readonly ReportSchedulesController _ctrl;

    public ReportScheduleTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
        Environment.SetEnvironmentVariable("DRIVECHILL_DATA_DIR", _tempDir);

        var settings = new AppSettings();
        _db = new DbService(settings, NullLogger<DbService>.Instance);
        _ctrl = new ReportSchedulesController(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        Environment.SetEnvironmentVariable("DRIVECHILL_DATA_DIR", null);
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task CreateUpdateDelete_RoundTripsSchedule()
    {
        var create = await _ctrl.Create(new ReportScheduleCreateRequest
        {
            Frequency = "daily",
            TimeUtc = "08:00",
            Timezone = "UTC",
            Enabled = true,
        });

        var ok = Assert.IsType<OkObjectResult>(create);
        var created = Assert.IsType<ReportScheduleRecord>(ok.Value);
        Assert.Equal("daily", created.Frequency);

        var update = await _ctrl.Update(created.Id, new ReportScheduleUpdateRequest
        {
            Enabled = false,
        });
        var updateOk = Assert.IsType<OkObjectResult>(update);
        var updated = Assert.IsType<ReportScheduleRecord>(updateOk.Value);
        Assert.False(updated.Enabled);

        var delete = await _ctrl.Delete(created.Id);
        Assert.IsType<OkObjectResult>(delete);
    }

    [Fact]
    public async Task Update_RejectsEmptyBody()
    {
        var create = await _ctrl.Create(new ReportScheduleCreateRequest
        {
            Frequency = "weekly",
            TimeUtc = "14:00",
        });
        var ok = Assert.IsType<OkObjectResult>(create);
        var created = Assert.IsType<ReportScheduleRecord>(ok.Value);

        var result = await _ctrl.Update(created.Id, new ReportScheduleUpdateRequest());
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Create_RejectsInvalidTime()
    {
        var result = await _ctrl.Create(new ReportScheduleCreateRequest
        {
            Frequency = "daily",
            TimeUtc = "99:00",
        });

        Assert.IsType<UnprocessableEntityObjectResult>(result);
    }

    [Fact]
    public void IsDue_DailyAndWeeklyBehaviorsMatchContract()
    {
        var now = new DateTimeOffset(2026, 3, 10, 8, 0, 0, TimeSpan.Zero);

        Assert.True(ReportSchedulerService.IsDue(new ReportScheduleRecord
        {
            Frequency = "daily",
            TimeUtc = "08:00",
            Enabled = true,
        }, now));

        Assert.False(ReportSchedulerService.IsDue(new ReportScheduleRecord
        {
            Frequency = "daily",
            TimeUtc = "08:00",
            LastSentAt = "2026-03-10T06:00:00+00:00",
        }, now));

        Assert.True(ReportSchedulerService.IsDue(new ReportScheduleRecord
        {
            Frequency = "weekly",
            TimeUtc = "08:00",
            LastSentAt = "2026-03-02T08:00:00+00:00",
        }, now));

        Assert.False(ReportSchedulerService.IsDue(new ReportScheduleRecord
        {
            Frequency = "daily",
            TimeUtc = "bad-time",
        }, now));
    }

    [Fact]
    public void WeekStartUtc_UsesMondayBoundary()
    {
        var sunday = new DateTimeOffset(2026, 3, 15, 23, 59, 0, TimeSpan.Zero);
        Assert.Equal(
            new DateTimeOffset(2026, 3, 9, 0, 0, 0, TimeSpan.Zero),
            ReportSchedulerService.WeekStartUtc(sunday));
    }

    [Fact]
    public async Task Scheduler_CheckAndSend_UpdatesLastSentOnSuccess()
    {
        var now = new DateTimeOffset(2026, 3, 10, 8, 0, 0, TimeSpan.Zero);
        await _db.CreateReportScheduleAsync(new ReportScheduleRecord
        {
            Id = "rs_test",
            Frequency = "daily",
            TimeUtc = "08:00",
            Timezone = "UTC",
            Enabled = true,
            CreatedAt = now.AddMinutes(-5).ToString("o"),
        });

        var email = new FakeEmailNotificationService(_db) { NextResult = true };
        var svc = new ReportSchedulerService(
            _db,
            email,
            NullLogger<ReportSchedulerService>.Instance,
            () => now,
            TimeSpan.FromSeconds(1));

        await svc.CheckAndSendAsync();

        var updated = await _db.GetReportScheduleAsync("rs_test");
        Assert.Equal(now.ToString("o"), updated!.LastSentAt);
        Assert.Single(email.SentSubjects);
        Assert.Contains("Daily Report", email.SentSubjects[0]);
    }

    [Fact]
    public async Task Scheduler_CheckAndSend_DoesNotUpdateLastSentOnFailure()
    {
        var now = new DateTimeOffset(2026, 3, 10, 8, 0, 0, TimeSpan.Zero);
        await _db.CreateReportScheduleAsync(new ReportScheduleRecord
        {
            Id = "rs_fail",
            Frequency = "daily",
            TimeUtc = "08:00",
            Timezone = "UTC",
            Enabled = true,
            CreatedAt = now.AddMinutes(-5).ToString("o"),
        });

        var email = new FakeEmailNotificationService(_db) { NextResult = false };
        var svc = new ReportSchedulerService(
            _db,
            email,
            NullLogger<ReportSchedulerService>.Instance,
            () => now,
            TimeSpan.FromSeconds(1));

        await svc.CheckAndSendAsync();

        var updated = await _db.GetReportScheduleAsync("rs_fail");
        Assert.Null(updated!.LastSentAt);
    }

    // -----------------------------------------------------------------------
    // DST regression tests
    // -----------------------------------------------------------------------

    [Fact]
    public void IsDue_Daily_SpringForwardDay_FiresCorrectly()
    {
        // The C# IsDue operates in UTC (no timezone conversion), so DST
        // does not affect the hour/minute comparison.  This test confirms
        // that a daily schedule at 08:00 UTC fires on a US spring-forward
        // day (2026-03-08) when the UTC clock reads 08:00.
        var now = new DateTimeOffset(2026, 3, 8, 8, 0, 0, TimeSpan.Zero);
        Assert.True(ReportSchedulerService.IsDue(new ReportScheduleRecord
        {
            Frequency = "daily",
            TimeUtc = "08:00",
            Enabled = true,
            LastSentAt = "2026-03-07T08:00:00+00:00",
        }, now));
    }

    [Fact]
    public void IsDue_Daily_FallBackDay_DoesNotFireTwice()
    {
        // On US fall-back day (2026-11-01), if already sent today at 06:00
        // UTC, a second check at 08:00 UTC should NOT fire again.
        var now = new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);
        Assert.False(ReportSchedulerService.IsDue(new ReportScheduleRecord
        {
            Frequency = "daily",
            TimeUtc = "08:00",
            Enabled = true,
            LastSentAt = "2026-11-01T06:00:00+00:00",
        }, now));
    }

    [Fact]
    public void WeekStartUtc_DstTransitionSunday()
    {
        // 2026-03-08 is the US spring-forward Sunday.
        // WeekStartUtc should still return the previous Monday (2026-03-02).
        var springForwardSunday = new DateTimeOffset(2026, 3, 8, 12, 0, 0, TimeSpan.Zero);
        var weekStart = ReportSchedulerService.WeekStartUtc(springForwardSunday);
        Assert.Equal(new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.Zero), weekStart);
    }

    [Fact]
    public void IsDue_Weekly_AcrossDstBoundary()
    {
        // Monday 2026-03-09 08:00 UTC.  Last sent previous Monday 2026-03-02.
        // Different ISO weeks — should fire even though DST changed between.
        var now = new DateTimeOffset(2026, 3, 9, 8, 0, 0, TimeSpan.Zero);
        Assert.True(ReportSchedulerService.IsDue(new ReportScheduleRecord
        {
            Frequency = "weekly",
            TimeUtc = "08:00",
            Enabled = true,
            LastSentAt = "2026-03-02T08:00:00+00:00",
        }, now));
    }

    // -----------------------------------------------------------------------
    // v3.1.0 regression: IsDue converts to schedule's local timezone
    // -----------------------------------------------------------------------

    [Fact]
    public void IsDue_NonUtcTimezone_ConvertsBeforeComparing()
    {
        // Schedule is set for 08:00 America/New_York (EST = UTC-5).
        // At 13:00 UTC, local time is 08:00 ET — should fire.
        var utcNow = new DateTimeOffset(2026, 1, 15, 13, 0, 0, TimeSpan.Zero);
        Assert.True(ReportSchedulerService.IsDue(new ReportScheduleRecord
        {
            Frequency = "daily",
            TimeUtc = "08:00",
            Timezone = "America/New_York",
            Enabled = true,
        }, utcNow), "IsDue must convert to local timezone before comparing hour/minute");
    }

    [Fact]
    public void IsDue_NonUtcTimezone_DoesNotFireAtUtcTime()
    {
        // Schedule is 08:00 America/New_York (UTC-5).
        // At 08:00 UTC, local time is 03:00 ET — should NOT fire.
        var utcNow = new DateTimeOffset(2026, 1, 15, 8, 0, 0, TimeSpan.Zero);
        Assert.False(ReportSchedulerService.IsDue(new ReportScheduleRecord
        {
            Frequency = "daily",
            TimeUtc = "08:00",
            Timezone = "America/New_York",
            Enabled = true,
        }, utcNow), "IsDue must not fire at UTC time when schedule is in a different timezone");
    }

    private sealed class FakeEmailNotificationService : EmailNotificationService
    {
        public bool NextResult { get; set; } = true;
        public List<string> SentSubjects { get; } = [];

        public FakeEmailNotificationService(DbService db)
            : base(db, NullLogger<EmailNotificationService>.Instance)
        {
        }

        public override Task<bool> SendHtmlReportAsync(string subject, string htmlBody, CancellationToken ct = default)
        {
            SentSubjects.Add(subject);
            return Task.FromResult(NextResult);
        }
    }
}
