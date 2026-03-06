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

public sealed class QuietHoursControllerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DbService _db;
    private readonly SettingsStore _store;
    private readonly QuietHoursController _ctrl;

    public QuietHoursControllerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
        Environment.SetEnvironmentVariable("DRIVECHILL_DATA_DIR", _tempDir);

        var settings = new AppSettings();
        _db    = new DbService(settings, NullLogger<DbService>.Instance);
        _store = new SettingsStore(settings);
        _ctrl  = new QuietHoursController(_db, _store);
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
    public async Task GetRules_ReturnsEmptyList_Initially()
    {
        var result = await _ctrl.GetRules();
        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task CreateRule_ReturnsOk_WithValidRule()
    {
        var profileId = CreateTestProfile();
        var rule = new QuietHoursRule
        {
            DayOfWeek = 1,
            StartTime = "22:00",
            EndTime   = "06:00",
            ProfileId = profileId,
            Enabled   = true,
        };
        var result = await _ctrl.CreateRule(rule);
        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task CreateRule_ReturnsUnprocessable_WithInvalidDayOfWeek()
    {
        var profileId = CreateTestProfile();
        var rule = new QuietHoursRule
        {
            DayOfWeek = 7, // Invalid — must be 0-6
            StartTime = "22:00",
            EndTime   = "06:00",
            ProfileId = profileId,
        };
        var result = await _ctrl.CreateRule(rule);
        Assert.IsType<UnprocessableEntityObjectResult>(result);
    }

    [Fact]
    public async Task CreateRule_ReturnsUnprocessable_WithInvalidTimeFormat()
    {
        var profileId = CreateTestProfile();
        var rule = new QuietHoursRule
        {
            DayOfWeek = 1,
            StartTime = "25:00", // Invalid hour
            EndTime   = "06:00",
            ProfileId = profileId,
        };
        var result = await _ctrl.CreateRule(rule);
        Assert.IsType<UnprocessableEntityObjectResult>(result);
    }

    [Fact]
    public async Task CreateRule_ReturnsNotFound_WithMissingProfile()
    {
        var rule = new QuietHoursRule
        {
            DayOfWeek = 1,
            StartTime = "22:00",
            EndTime   = "06:00",
            ProfileId = "nonexistent-profile-id",
        };
        var result = await _ctrl.CreateRule(rule);
        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task CreateRule_ReturnsUnprocessable_WithMissingProfileId()
    {
        var rule = new QuietHoursRule
        {
            DayOfWeek = 1,
            StartTime = "22:00",
            EndTime   = "06:00",
            ProfileId = "",
        };
        var result = await _ctrl.CreateRule(rule);
        Assert.IsType<UnprocessableEntityObjectResult>(result);
    }

    [Fact]
    public async Task DeleteRule_ReturnsNotFound_ForMissingId()
    {
        var result = await _ctrl.DeleteRule(999);
        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task DeleteRule_RemovesCreatedRule()
    {
        var profileId = CreateTestProfile();
        var rule = new QuietHoursRule
        {
            DayOfWeek = 3,
            StartTime = "23:00",
            EndTime   = "07:00",
            ProfileId = profileId,
        };
        var createResult = await _ctrl.CreateRule(rule);
        var ok = Assert.IsType<OkObjectResult>(createResult);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var id = doc.RootElement.GetProperty("id").GetInt32();

        var deleteResult = await _ctrl.DeleteRule(id);
        Assert.IsType<OkObjectResult>(deleteResult);

        var rules = await _db.GetQuietHoursAsync();
        Assert.Empty(rules);
    }

    [Fact]
    public async Task UpdateRule_ReturnsNotFound_ForMissingId()
    {
        var profileId = CreateTestProfile();
        var rule = new QuietHoursRule
        {
            DayOfWeek = 1,
            StartTime = "22:00",
            EndTime   = "06:00",
            ProfileId = profileId,
        };
        var result = await _ctrl.UpdateRule(999, rule);
        Assert.IsType<NotFoundObjectResult>(result);
    }

    // -----------------------------------------------------------------------
    // Time format validation
    // -----------------------------------------------------------------------

    [Fact]
    public async Task CreateRule_ReturnsUnprocessable_WithBadTimeFormat()
    {
        var profileId = CreateTestProfile();
        var rule = new QuietHoursRule
        {
            DayOfWeek = 0,
            StartTime = "abc",
            EndTime   = "06:00",
            ProfileId = profileId,
        };
        var result = await _ctrl.CreateRule(rule);
        Assert.IsType<UnprocessableEntityObjectResult>(result);
    }
}
