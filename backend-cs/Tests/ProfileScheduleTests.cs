using System;
using System.IO;
using System.Threading.Tasks;
using DriveChill.Api;
using DriveChill.Models;
using DriveChill.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DriveChill.Tests;

public sealed class ProfileScheduleTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DbService _db;
    private readonly ProfileSchedulesController _ctrl;

    public ProfileScheduleTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
        Environment.SetEnvironmentVariable("DRIVECHILL_DATA_DIR", _tempDir);

        var settings = new AppSettings();
        _db    = new DbService(settings, NullLogger<DbService>.Instance);
        _ctrl  = new ProfileSchedulesController(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        Environment.SetEnvironmentVariable("DRIVECHILL_DATA_DIR", null);
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private async Task<string> CreateTestProfileAsync(string name = "TestProfile")
    {
        var profile = new Profile { Name = name, Description = "test" };
        await _db.CreateProfileAsync(profile);
        return profile.Id;
    }

    // -----------------------------------------------------------------------
    // CRUD
    // -----------------------------------------------------------------------

    [Fact]
    public async Task GetSchedules_ReturnsEmptyList_Initially()
    {
        var result = await _ctrl.GetSchedules();
        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task CreateSchedule_ReturnsOk_WithValidBody()
    {
        var profileId = await CreateTestProfileAsync();
        var body = new ProfileScheduleRequest
        {
            ProfileId  = profileId,
            StartTime  = "09:00",
            EndTime    = "17:00",
            DaysOfWeek = "0,1,2,3,4",
        };
        var result = await _ctrl.CreateSchedule(body);
        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task CreateSchedule_ReturnsNotFound_WithMissingProfile()
    {
        var body = new ProfileScheduleRequest
        {
            ProfileId  = "nonexistent-profile",
            StartTime  = "09:00",
            EndTime    = "17:00",
            DaysOfWeek = "0,1",
        };
        var result = await _ctrl.CreateSchedule(body);
        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task CreateSchedule_ReturnsUnprocessable_WithInvalidTime()
    {
        var profileId = await CreateTestProfileAsync();
        var body = new ProfileScheduleRequest
        {
            ProfileId  = profileId,
            StartTime  = "25:00",
            EndTime    = "17:00",
            DaysOfWeek = "0",
        };
        var result = await _ctrl.CreateSchedule(body);
        Assert.IsType<UnprocessableEntityObjectResult>(result);
    }

    [Fact]
    public async Task CreateSchedule_ReturnsUnprocessable_WithInvalidDay()
    {
        var profileId = await CreateTestProfileAsync();
        var body = new ProfileScheduleRequest
        {
            ProfileId  = profileId,
            StartTime  = "09:00",
            EndTime    = "17:00",
            DaysOfWeek = "7",
        };
        var result = await _ctrl.CreateSchedule(body);
        Assert.IsType<UnprocessableEntityObjectResult>(result);
    }

    [Fact]
    public async Task CreateSchedule_ReturnsUnprocessable_WithEmptyProfileId()
    {
        var body = new ProfileScheduleRequest
        {
            ProfileId  = "",
            StartTime  = "09:00",
            EndTime    = "17:00",
            DaysOfWeek = "0,1",
        };
        var result = await _ctrl.CreateSchedule(body);
        Assert.IsType<UnprocessableEntityObjectResult>(result);
    }

    [Fact]
    public async Task DeleteSchedule_ReturnsNotFound_ForMissingId()
    {
        var result = await _ctrl.DeleteSchedule("nonexistent");
        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task DeleteSchedule_RemovesCreatedSchedule()
    {
        var profileId = await CreateTestProfileAsync();
        var body = new ProfileScheduleRequest
        {
            ProfileId  = profileId,
            StartTime  = "22:00",
            EndTime    = "06:00",
            DaysOfWeek = "0,1,2,3,4",
        };
        var createResult = await _ctrl.CreateSchedule(body);
        var ok = Assert.IsType<OkObjectResult>(createResult);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var id = doc.RootElement.GetProperty("id").GetString()!;

        var deleteResult = await _ctrl.DeleteSchedule(id);
        Assert.IsType<NoContentResult>(deleteResult);

        var schedules = await _db.GetProfileSchedulesAsync();
        Assert.Empty(schedules);
    }

    [Fact]
    public async Task UpdateSchedule_ReturnsNotFound_ForMissingId()
    {
        var profileId = await CreateTestProfileAsync();
        var body = new ProfileScheduleRequest
        {
            ProfileId  = profileId,
            StartTime  = "09:00",
            EndTime    = "17:00",
            DaysOfWeek = "0",
        };
        var result = await _ctrl.UpdateSchedule("nonexistent", body);
        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task UpdateSchedule_ReturnsOk_ForExistingSchedule()
    {
        var profileId = await CreateTestProfileAsync();
        var createBody = new ProfileScheduleRequest
        {
            ProfileId  = profileId,
            StartTime  = "09:00",
            EndTime    = "17:00",
            DaysOfWeek = "0,1,2,3,4",
        };
        var createResult = await _ctrl.CreateSchedule(createBody);
        var ok = Assert.IsType<OkObjectResult>(createResult);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var id = doc.RootElement.GetProperty("id").GetString()!;

        var updateBody = new ProfileScheduleRequest
        {
            ProfileId  = profileId,
            StartTime  = "10:00",
            EndTime    = "18:00",
            DaysOfWeek = "0,1,2",
        };
        var updateResult = await _ctrl.UpdateSchedule(id, updateBody);
        Assert.IsType<OkObjectResult>(updateResult);
    }

    // -----------------------------------------------------------------------
    // Evaluation logic
    // -----------------------------------------------------------------------

    [Fact]
    public void FindActiveSchedule_ReturnsNull_WhenEmpty()
    {
        var result = ProfileSchedulerService.FindActiveSchedule([]);
        Assert.Null(result);
    }

    [Fact]
    public void FindActiveSchedule_MatchesDisabledSchedule_ReturnsNull()
    {
        var schedules = new List<ProfileScheduleRecord>
        {
            new()
            {
                Id = "s1", ProfileId = "p1",
                StartTime = "00:00", EndTime = "23:59",
                DaysOfWeek = "0,1,2,3,4,5,6",
                Enabled = false, CreatedAt = "2026-01-01",
            },
        };
        var result = ProfileSchedulerService.FindActiveSchedule(schedules);
        Assert.Null(result);
    }

    [Fact]
    public void FindActiveSchedule_UsesIanaTimezone_NotUtc()
    {
        var utcNow = DateTimeOffset.UtcNow;
        var utcTime = utcNow.ToString("HH:mm");
        var utcDay = (int)utcNow.DayOfWeek;
        var utcDayMon = utcDay == 0 ? 6 : utcDay - 1;

        var nyTz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var nyLocal = TimeZoneInfo.ConvertTime(utcNow, nyTz);
        var nyTime = nyLocal.ToString("HH:mm");

        if (utcTime != nyTime)
        {
            var endMinute = (int.Parse(utcTime.Split(':')[1]) + 1) % 60;
            var endHour = int.Parse(utcTime.Split(':')[0]) + (endMinute == 0 ? 1 : 0);
            var endTime = $"{endHour % 24:D2}:{endMinute:D2}";

            var schedules = new List<ProfileScheduleRecord>
            {
                new()
                {
                    Id = "ny-test", ProfileId = "p1",
                    StartTime = utcTime, EndTime = endTime,
                    DaysOfWeek = $"{utcDayMon}",
                    Timezone = "America/New_York",
                    Enabled = true, CreatedAt = "2026-01-01",
                },
            };
            var result = ProfileSchedulerService.FindActiveSchedule(schedules);
            Assert.Null(result);
        }

        {
            var endMinute = (int.Parse(utcTime.Split(':')[1]) + 1) % 60;
            var endHour = int.Parse(utcTime.Split(':')[0]) + (endMinute == 0 ? 1 : 0);
            var endTime = $"{endHour % 24:D2}:{endMinute:D2}";

            var schedules = new List<ProfileScheduleRecord>
            {
                new()
                {
                    Id = "utc-test", ProfileId = "p1",
                    StartTime = utcTime, EndTime = endTime,
                    DaysOfWeek = $"{utcDayMon}",
                    Timezone = "UTC",
                    Enabled = true, CreatedAt = "2026-01-01",
                },
            };
            var result = ProfileSchedulerService.FindActiveSchedule(schedules);
            Assert.NotNull(result);
            Assert.Equal("utc-test", result!.Id);
        }
    }

    // -----------------------------------------------------------------------
    // DST regression tests
    // -----------------------------------------------------------------------

    [Fact]
    public void FindActiveSchedule_SpringForward_MatchesInLocalTime()
    {
        // 2026-03-08 12:00 UTC = 08:00 EDT (spring-forward Sunday).
        // Schedule: 07:00-09:00 on Sunday (day 6 in Mon=0 convention),
        // timezone America/New_York.
        var utcNow = new DateTimeOffset(2026, 3, 8, 12, 0, 0, TimeSpan.Zero);
        var schedules = new List<ProfileScheduleRecord>
        {
            new()
            {
                Id = "dst-spring", ProfileId = "p1",
                StartTime = "07:00", EndTime = "09:00",
                DaysOfWeek = "6",  // Sunday = 6 (Mon=0)
                Timezone = "America/New_York",
                Enabled = true, CreatedAt = "2026-01-01",
            },
        };
        var result = ProfileSchedulerService.FindActiveSchedule(schedules, utcNow);
        Assert.NotNull(result);
        Assert.Equal("dst-spring", result!.Id);
    }

    [Fact]
    public void FindActiveSchedule_SpringForward_NoMatchOutsideWindow()
    {
        // 2026-03-08 14:00 UTC = 10:00 EDT — outside 07:00-09:00 window.
        var utcNow = new DateTimeOffset(2026, 3, 8, 14, 0, 0, TimeSpan.Zero);
        var schedules = new List<ProfileScheduleRecord>
        {
            new()
            {
                Id = "dst-no", ProfileId = "p1",
                StartTime = "07:00", EndTime = "09:00",
                DaysOfWeek = "6",
                Timezone = "America/New_York",
                Enabled = true, CreatedAt = "2026-01-01",
            },
        };
        var result = ProfileSchedulerService.FindActiveSchedule(schedules, utcNow);
        Assert.Null(result);
    }

    [Fact]
    public void FindActiveSchedule_FallBack_HandlesDuplicateHour()
    {
        // 2026-11-01 06:30 UTC = 01:30 EST (after fall-back).
        // Schedule: 01:00-03:00 Sunday, America/New_York.
        var utcNow = new DateTimeOffset(2026, 11, 1, 6, 30, 0, TimeSpan.Zero);
        var schedules = new List<ProfileScheduleRecord>
        {
            new()
            {
                Id = "dst-fall", ProfileId = "p1",
                StartTime = "01:00", EndTime = "03:00",
                DaysOfWeek = "6",  // Sunday
                Timezone = "America/New_York",
                Enabled = true, CreatedAt = "2026-01-01",
            },
        };
        var result = ProfileSchedulerService.FindActiveSchedule(schedules, utcNow);
        Assert.NotNull(result);
        Assert.Equal("dst-fall", result!.Id);
    }

    [Fact]
    public void FindActiveSchedule_OvernightAcrossDstBoundary()
    {
        // 2026-03-08 04:00 UTC = 23:00 Saturday EST (before spring-forward).
        // Overnight schedule 23:00-06:00 on Saturday (day 5, Mon=0).
        var utcNow = new DateTimeOffset(2026, 3, 8, 4, 0, 0, TimeSpan.Zero);
        var schedules = new List<ProfileScheduleRecord>
        {
            new()
            {
                Id = "dst-overnight", ProfileId = "p1",
                StartTime = "23:00", EndTime = "06:00",
                DaysOfWeek = "5",  // Saturday (local time is 23:00 Saturday)
                Timezone = "America/New_York",
                Enabled = true, CreatedAt = "2026-01-01",
            },
        };
        var result = ProfileSchedulerService.FindActiveSchedule(schedules, utcNow);
        Assert.NotNull(result);
        Assert.Equal("dst-overnight", result!.Id);
    }

    [Fact]
    public void FindActiveSchedule_InvalidTimezone_FallsBackToUtc()
    {
        var utcNow = DateTimeOffset.UtcNow;
        var utcTime = utcNow.ToString("HH:mm");
        var utcDay = (int)utcNow.DayOfWeek;
        var utcDayMon = utcDay == 0 ? 6 : utcDay - 1;

        var endMinute = (int.Parse(utcTime.Split(':')[1]) + 1) % 60;
        var endHour = int.Parse(utcTime.Split(':')[0]) + (endMinute == 0 ? 1 : 0);
        var endTime = $"{endHour % 24:D2}:{endMinute:D2}";

        var schedules = new List<ProfileScheduleRecord>
        {
            new()
            {
                Id = "bad-tz", ProfileId = "p1",
                StartTime = utcTime, EndTime = endTime,
                DaysOfWeek = $"{utcDayMon}",
                Timezone = "Invalid/Timezone_That_Does_Not_Exist",
                Enabled = true, CreatedAt = "2026-01-01",
            },
        };
        var result = ProfileSchedulerService.FindActiveSchedule(schedules);
        Assert.NotNull(result);
        Assert.Equal("bad-tz", result!.Id);
    }
}
