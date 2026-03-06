using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DriveChill.Api;
using DriveChill.Models;
using DriveChill.Services;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace DriveChill.Tests;

public sealed class ProfilesControllerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SettingsStore _store;
    private readonly FanService _fans;
    private readonly AlertService _alerts;
    private readonly ProfilesController _ctrl;

    public ProfilesControllerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
        Environment.SetEnvironmentVariable("DRIVECHILL_DATA_DIR", _tempDir);

        var settings = new AppSettings();
        _store  = new SettingsStore(settings);
        _fans   = new FanService(new StubHardware("fan1", "fan2"), _store);
        _alerts = new AlertService(_store);
        _ctrl   = new ProfilesController(_store, _fans, _alerts);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("DRIVECHILL_DATA_DIR", null);
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private Profile CreateTestProfile(string name = "TestProfile")
    {
        var result = _ctrl.CreateProfile(new CreateProfileRequest
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
    public void GetProfile_ById_ReturnsProfile()
    {
        var profile = CreateTestProfile();
        var result  = _ctrl.GetProfile(profile.Id);
        var ok      = Assert.IsType<OkObjectResult>(result);
        var returned = Assert.IsType<Profile>(ok.Value);
        Assert.Equal(profile.Id, returned.Id);
    }

    [Fact]
    public void GetProfile_NotFound_Returns404()
    {
        var result = _ctrl.GetProfile("nonexistent-id");
        Assert.IsType<NotFoundObjectResult>(result);
    }

    // -----------------------------------------------------------------------
    // Export
    // -----------------------------------------------------------------------

    [Fact]
    public void ExportProfile_ReturnsExportVersion1()
    {
        var profile = CreateTestProfile("MyExportProfile");
        var result  = _ctrl.ExportProfile(profile.Id);
        var ok      = Assert.IsType<OkObjectResult>(result);

        // Serialize to JSON and check export_version field
        var json = JsonSerializer.Serialize(ok.Value,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(1, doc.RootElement.GetProperty("export_version").GetInt32());
    }

    [Fact]
    public void ExportProfile_ContainsNameAndPreset()
    {
        var profile = CreateTestProfile("ExportTest");
        var result  = _ctrl.ExportProfile(profile.Id);
        var ok      = Assert.IsType<OkObjectResult>(result);

        var json = JsonSerializer.Serialize(ok.Value,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
        using var doc = JsonDocument.Parse(json);
        var profileEl = doc.RootElement.GetProperty("profile");
        Assert.Equal("ExportTest",  profileEl.GetProperty("name").GetString());
        Assert.Equal("balanced", profileEl.GetProperty("preset").GetString());
    }

    [Fact]
    public void ExportProfile_NotFound_Returns404()
    {
        var result = _ctrl.ExportProfile("missing-id");
        Assert.IsType<NotFoundObjectResult>(result);
    }

    // -----------------------------------------------------------------------
    // Import
    // -----------------------------------------------------------------------

    [Fact]
    public void ImportProfile_CreatesNewProfile()
    {
        var result = _ctrl.ImportProfile(new ImportProfileRequest
        {
            Name   = "ImportedProfile",
            Preset = "silent",
            Curves = [new FanCurve { FanId = "fan1", SensorId = "cpu",
                      Points = [new() { Temp = 30, Speed = 20 }, new() { Temp = 80, Speed = 70 }] }],
        });

        Assert.Equal(201, ((ObjectResult)result).StatusCode);

        var allProfiles = _store.LoadProfiles();
        Assert.Contains(allProfiles, p => p.Name == "ImportedProfile");
    }

    [Fact]
    public void ImportProfile_GeneratesNameWhenNullOrEmpty()
    {
        var result = _ctrl.ImportProfile(new ImportProfileRequest
        {
            Name   = null,
            Preset = "custom",
            Curves = [],
        });

        Assert.Equal(201, ((ObjectResult)result).StatusCode);

        var allProfiles = _store.LoadProfiles();
        Assert.Contains(allProfiles, p => p.Name.StartsWith("Imported"));
    }

    // -----------------------------------------------------------------------
    // Activate
    // -----------------------------------------------------------------------

    [Fact]
    public void ActivateProfile_SetsIsActiveFlag()
    {
        var p1 = CreateTestProfile("Profile1");
        var p2 = CreateTestProfile("Profile2");

        _ctrl.ActivateProfile(p1.Id);
        _ctrl.ActivateProfile(p2.Id);

        var profiles = _store.LoadProfiles().ToList();
        Assert.False(profiles.First(p => p.Id == p1.Id).IsActive);
        Assert.True(profiles.First(p => p.Id == p2.Id).IsActive);
    }

    [Fact]
    public void ActivateProfile_ClearsOrphanCurvesFromPreviousProfile()
    {
        // Profile A has fan1 curve
        var pA = _ctrl.CreateProfile(new CreateProfileRequest
        {
            Name   = "ProfileA",
            Curves = [new FanCurve { FanId = "fan1", SensorId = "cpu",
                      Points = [new() { Temp = 40, Speed = 40 }, new() { Temp = 80, Speed = 80 }] }],
        });
        var profileA = ((CreatedAtActionResult)pA).Value as Profile;

        // Profile B has fan2 curve
        var pB = _ctrl.CreateProfile(new CreateProfileRequest
        {
            Name   = "ProfileB",
            Curves = [new FanCurve { FanId = "fan2", SensorId = "gpu",
                      Points = [new() { Temp = 40, Speed = 30 }, new() { Temp = 80, Speed = 70 }] }],
        });
        var profileB = ((CreatedAtActionResult)pB).Value as Profile;

        _ctrl.ActivateProfile(profileA!.Id);
        _ctrl.ActivateProfile(profileB!.Id);

        // fan1 curve should be gone (orphan cleared), only fan2 should remain
        var curves = _fans.GetCurves();
        Assert.Single(curves);
        Assert.Equal("fan2", curves[0].FanId);
    }
}
