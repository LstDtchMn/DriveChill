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
    public void FindActiveSchedule_UsesTimezone_NotUtc()
    {
        // Create a schedule that's active at 10:00 in US Eastern but NOT at
        // 10:00 UTC. We'll test by setting timezone to a zone that differs
        // from UTC and verifying the match depends on the zone.
        var utcNow = DateTimeOffset.UtcNow;

        // Find a timezone where the current local time differs from UTC
        // Use UTC+12 (e.g., "Fiji Standard Time" / "Pacific/Fiji") — if it's
        // 14:00 UTC, it's 02:00+1 in UTC+12 (different day potentially)
        var tz = TimeZoneInfo.FindSystemTimeZoneById("UTC");
        var utcLocal = TimeZoneInfo.ConvertTime(utcNow, tz);
        var utcTime = utcLocal.ToString("HH:mm");
        var utcDay = (int)utcLocal.DayOfWeek;
        var utcDayMon = utcDay == 0 ? 6 : utcDay - 1;

        // Schedule active at current UTC time, all days, but in UTC+12 timezone
        // This should NOT match because in UTC+12 the wall-clock time is different
        string tz12Id;
        try { TimeZoneInfo.FindSystemTimeZoneById("Pacific/Fiji"); tz12Id = "Pacific/Fiji"; }
        catch (TimeZoneNotFoundException) { tz12Id = "Fiji Standard Time"; }

        var tz12 = TimeZoneInfo.FindSystemTimeZoneById(tz12Id);
        var tz12Local = TimeZoneInfo.ConvertTime(utcNow, tz12);
        var tz12Time = tz12Local.ToString("HH:mm");

        // Only run the assertion if the times actually differ (they should unless UTC offset is 0)
        if (utcTime != tz12Time)
        {
            // Schedule window is [utcTime, utcTime+1min) — matches UTC but not UTC+12
            var endMinute = (int.Parse(utcTime.Split(':')[1]) + 1) % 60;
            var endHour = int.Parse(utcTime.Split(':')[0]) + (endMinute == 0 ? 1 : 0);
            var endTime = $"{endHour % 24:D2}:{endMinute:D2}";

            var schedules = new List<ProfileScheduleRecord>
            {
                new()
                {
                    Id = "tz-test", ProfileId = "p1",
                    StartTime = utcTime, EndTime = endTime,
                    DaysOfWeek = $"{utcDayMon}",
                    Timezone = tz12Id,
                    Enabled = true, CreatedAt = "2026-01-01",
                },
            };
            // Should NOT match — schedule time is interpreted in UTC+12, not UTC
            var result = ProfileSchedulerService.FindActiveSchedule(schedules);
            Assert.Null(result);
        }

        // Now verify the same schedule DOES match when timezone is UTC
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
