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
}
