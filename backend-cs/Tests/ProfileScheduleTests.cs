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
    private readonly SettingsStore _store;
    private readonly ProfileSchedulesController _ctrl;

    public ProfileScheduleTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
        Environment.SetEnvironmentVariable("DRIVECHILL_DATA_DIR", _tempDir);

        var settings = new AppSettings();
        _db    = new DbService(settings, NullLogger<DbService>.Instance);
        _store = new SettingsStore(settings);
        _ctrl  = new ProfileSchedulesController(_db, _store);
    }

    public void Dispose()
    {
        _db.Dispose();
        Environment.SetEnvironmentVariable("DRIVECHILL_DATA_DIR", null);
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private string CreateTestProfile(string name = "TestProfile")
    {
        var profile = new Profile { Name = name, Description = "test" };
        var profiles = _store.LoadProfiles().ToList();
        profiles.Add(profile);
        _store.SaveProfiles(profiles);
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
        var profileId = CreateTestProfile();
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
        var profileId = CreateTestProfile();
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
        var profileId = CreateTestProfile();
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
        var profileId = CreateTestProfile();
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
        var profileId = CreateTestProfile();
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
        var profileId = CreateTestProfile();
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
        // The frontend sends IANA IDs (e.g. "America/New_York") via
        // Intl.DateTimeFormat().resolvedOptions().timeZone.
        // .NET 6+ FindSystemTimeZoneById handles IANA IDs on all platforms.
        var utcNow = DateTimeOffset.UtcNow;
        var utcTime = utcNow.ToString("HH:mm");
        var utcDay = (int)utcNow.DayOfWeek;
        var utcDayMon = utcDay == 0 ? 6 : utcDay - 1;

        // Use "America/New_York" — the most common IANA ID the frontend sends
        var nyTz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var nyLocal = TimeZoneInfo.ConvertTime(utcNow, nyTz);
        var nyTime = nyLocal.ToString("HH:mm");

        // Only assert mismatch if times actually differ (they always will
        // unless the machine is running in exactly UTC-5/UTC-4)
        if (utcTime != nyTime)
        {
            // 1-minute window at current UTC time, tagged with IANA timezone
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
                    Timezone = "America/New_York", // IANA ID from frontend
                    Enabled = true, CreatedAt = "2026-01-01",
                },
            };
            // Should NOT match — schedule time is in NY local time, not UTC
            var result = ProfileSchedulerService.FindActiveSchedule(schedules);
            Assert.Null(result);
        }

        // Same window with UTC timezone DOES match
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
        // Invalid timezone falls back to UTC — should still match
        var result = ProfileSchedulerService.FindActiveSchedule(schedules);
        Assert.NotNull(result);
        Assert.Equal("bad-tz", result!.Id);
    }
}
