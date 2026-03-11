using System.IO;
using System.Linq;
using System.Text.Json;
using DriveChill.Api;
using DriveChill.Models;
using DriveChill.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DriveChill.Tests;

public sealed class NoiseProfilesControllerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DbService _db;
    private readonly NoiseProfilesController _ctrl;

    public NoiseProfilesControllerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
        Environment.SetEnvironmentVariable("DRIVECHILL_DATA_DIR", _tempDir);

        var settings = new AppSettings();
        _db = new DbService(settings, NullLogger<DbService>.Instance);
        _ctrl = new NoiseProfilesController(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        Environment.SetEnvironmentVariable("DRIVECHILL_DATA_DIR", null);
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task List_ReturnsEmptyInitially()
    {
        var result = await _ctrl.List();
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value);
        using var doc = JsonDocument.Parse(json);
        Assert.Empty(doc.RootElement.GetProperty("profiles").EnumerateArray());
    }

    [Fact]
    public async Task CreateGetDelete_RoundTripsProfile()
    {
        var createResult = await _ctrl.Create(new NoiseProfileRequest
        {
            FanId = "fan_1",
            Mode = "quick",
            Data = [new NoiseDataPoint { Rpm = 500, Db = 25.0 }],
        });

        var ok = Assert.IsType<OkObjectResult>(createResult);
        var profile = Assert.IsType<NoiseProfile>(ok.Value);
        Assert.Equal("fan_1", profile.FanId);
        Assert.Single(profile.Data);

        var getResult = await _ctrl.Get(profile.Id);
        var getOk = Assert.IsType<OkObjectResult>(getResult);
        var fetched = Assert.IsType<NoiseProfile>(getOk.Value);
        Assert.Equal(profile.Id, fetched.Id);

        var deleteResult = await _ctrl.Delete(profile.Id);
        Assert.IsType<OkObjectResult>(deleteResult);
        Assert.Empty(await _db.ListNoiseProfilesAsync());
    }

    [Fact]
    public async Task Create_RejectsInvalidMode()
    {
        var result = await _ctrl.Create(new NoiseProfileRequest
        {
            FanId = "fan_1",
            Mode = "loud",
            Data = [new NoiseDataPoint { Rpm = 500, Db = 25.0 }],
        });

        Assert.IsType<UnprocessableEntityObjectResult>(result);
    }

    [Fact]
    public async Task Create_RejectsNegativeData()
    {
        var result = await _ctrl.Create(new NoiseProfileRequest
        {
            FanId = "fan_1",
            Mode = "precise",
            Data = [new NoiseDataPoint { Rpm = -1, Db = 25.0 }],
        });

        Assert.IsType<UnprocessableEntityObjectResult>(result);
    }

    [Fact]
    public async Task Delete_ReturnsNotFound_ForMissingProfile()
    {
        var result = await _ctrl.Delete("np_missing");
        Assert.IsType<NotFoundObjectResult>(result);
    }
}
