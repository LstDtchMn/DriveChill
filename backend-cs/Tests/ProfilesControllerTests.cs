using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using DriveChill.Api;
using DriveChill.Models;
using DriveChill.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DriveChill.Tests;

public sealed class ProfilesControllerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DbService _db;
    private readonly FanService _fans;
    private readonly AlertService _alerts;
    private readonly ProfilesController _ctrl;

    public ProfilesControllerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
        Environment.SetEnvironmentVariable("DRIVECHILL_DATA_DIR", _tempDir);

        var settings = new AppSettings();
        var store   = new SettingsStore(settings);
        _db     = new DbService(settings, NullLogger<DbService>.Instance);
        _fans   = new FanService(new StubHardware("fan1", "fan2"), store);
        _alerts = new AlertService(_db);
        _alerts.InitializeAsync().GetAwaiter().GetResult();
        _ctrl   = new ProfilesController(_db, _fans, _alerts);
    }

    public void Dispose()
    {
        _db.Dispose();
        Environment.SetEnvironmentVariable("DRIVECHILL_DATA_DIR", null);
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private async Task<Profile> CreateTestProfileAsync(string name = "TestProfile")
    {
        var result = await _ctrl.CreateProfile(new CreateProfileRequest
        {
            Name        = name,
            Description = "balanced",
            Curves      = [new FanCurve { FanId = "fan1", SensorId = "cpu",
                           Points = [new() { Temp = 40, Speed = 30 }, new() { Temp = 80, Speed = 80 }] }],
        });
        var created = Assert.IsType<CreatedAtActionResult>(result);
        return Assert.IsType<Profile>(created.Value);
    }

    // -----------------------------------------------------------------------
    // GET by ID
    // -----------------------------------------------------------------------

    [Fact]
    public async Task GetProfile_ById_ReturnsProfile()
    {
        var profile = await CreateTestProfileAsync();
        var result  = await _ctrl.GetProfile(profile.Id);
        var ok      = Assert.IsType<OkObjectResult>(result);
        var returned = Assert.IsType<Profile>(ok.Value);
        Assert.Equal(profile.Id, returned.Id);
    }

    [Fact]
    public async Task GetProfile_NotFound_Returns404()
    {
        var result = await _ctrl.GetProfile("nonexistent-id");
        Assert.IsType<NotFoundObjectResult>(result);
    }

    // -----------------------------------------------------------------------
    // Export
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ExportProfile_ReturnsExportVersion1()
    {
        var profile = await CreateTestProfileAsync("MyExportProfile");
        var result  = await _ctrl.ExportProfile(profile.Id);
        var ok      = Assert.IsType<OkObjectResult>(result);

        var json = JsonSerializer.Serialize(ok.Value,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(1, doc.RootElement.GetProperty("export_version").GetInt32());
    }

    [Fact]
    public async Task ExportProfile_ContainsNameAndPreset()
    {
        var profile = await CreateTestProfileAsync("ExportTest");
        var result  = await _ctrl.ExportProfile(profile.Id);
        var ok      = Assert.IsType<OkObjectResult>(result);

        var json = JsonSerializer.Serialize(ok.Value,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
        using var doc = JsonDocument.Parse(json);
        var profileEl = doc.RootElement.GetProperty("profile");
        Assert.Equal("ExportTest",  profileEl.GetProperty("name").GetString());
        Assert.Equal("balanced", profileEl.GetProperty("preset").GetString());
    }

    [Fact]
    public async Task ExportProfile_NotFound_Returns404()
    {
        var result = await _ctrl.ExportProfile("missing-id");
        Assert.IsType<NotFoundObjectResult>(result);
    }

    // -----------------------------------------------------------------------
    // Import
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ImportProfile_CreatesNewProfile()
    {
        var result = await _ctrl.ImportProfile(new ImportProfileRequest
        {
            Name   = "ImportedProfile",
            Preset = "silent",
            Curves = [new FanCurve { FanId = "fan1", SensorId = "cpu",
                      Points = [new() { Temp = 30, Speed = 20 }, new() { Temp = 80, Speed = 70 }] }],
        });

        Assert.Equal(201, ((ObjectResult)result).StatusCode);

        var allProfiles = await _db.ListProfilesAsync();
        Assert.Contains(allProfiles, p => p.Name == "ImportedProfile");
    }

    [Fact]
    public async Task ImportProfile_GeneratesNameWhenNullOrEmpty()
    {
        var result = await _ctrl.ImportProfile(new ImportProfileRequest
        {
            Name   = null,
            Preset = "custom",
            Curves = [],
        });

        Assert.Equal(201, ((ObjectResult)result).StatusCode);

        var allProfiles = await _db.ListProfilesAsync();
        Assert.Contains(allProfiles, p => p.Name.StartsWith("Imported"));
    }

    // -----------------------------------------------------------------------
    // Activate
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ActivateProfile_SetsIsActiveFlag()
    {
        var p1 = await CreateTestProfileAsync("Profile1");
        var p2 = await CreateTestProfileAsync("Profile2");

        await _ctrl.ActivateProfile(p1.Id);
        await _ctrl.ActivateProfile(p2.Id);

        var profiles = await _db.ListProfilesAsync();
        Assert.False(profiles.First(p => p.Id == p1.Id).IsActive);
        Assert.True(profiles.First(p => p.Id == p2.Id).IsActive);
    }

    [Fact]
    public async Task ActivateProfile_ClearsOrphanCurvesFromPreviousProfile()
    {
        // Profile A has fan1 curve
        var pA = await _ctrl.CreateProfile(new CreateProfileRequest
        {
            Name   = "ProfileA",
            Curves = [new FanCurve { FanId = "fan1", SensorId = "cpu",
                      Points = [new() { Temp = 40, Speed = 40 }, new() { Temp = 80, Speed = 80 }] }],
        });
        var profileA = ((CreatedAtActionResult)pA).Value as Profile;

        // Profile B has fan2 curve
        var pB = await _ctrl.CreateProfile(new CreateProfileRequest
        {
            Name   = "ProfileB",
            Curves = [new FanCurve { FanId = "fan2", SensorId = "gpu",
                      Points = [new() { Temp = 40, Speed = 30 }, new() { Temp = 80, Speed = 70 }] }],
        });
        var profileB = ((CreatedAtActionResult)pB).Value as Profile;

        await _ctrl.ActivateProfile(profileA!.Id);
        await _ctrl.ActivateProfile(profileB!.Id);

        // fan1 curve should be gone (orphan cleared), only fan2 should remain
        var curves = _fans.GetCurves();
        Assert.Single(curves);
        Assert.Equal("fan2", curves[0].FanId);
    }
}
