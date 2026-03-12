using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using DriveChill.Api;
using DriveChill.Models;
using DriveChill.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DriveChill.Tests;

public sealed class FansControllerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AppSettings _settings;
    private readonly SettingsStore _store;
    private readonly FanService _fans;
    private readonly SensorService _sensors;
    private readonly DbService _db;
    private readonly FansController _ctrl;

    public FansControllerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
        Environment.SetEnvironmentVariable("DRIVECHILL_DATA_DIR", _tempDir);

        _settings = new AppSettings();
        _store    = new SettingsStore(_settings);
        _fans     = new FanService(new StubHardware("fan1", "fan2"), _store);
        _sensors  = new SensorService();
        _db       = new DbService(_settings, NullLogger<DbService>.Instance);
        _ctrl     = new FansController(_fans, _sensors, _db);
    }

    public void Dispose()
    {
        _db.Dispose();
        Environment.SetEnvironmentVariable("DRIVECHILL_DATA_DIR", null);
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // -----------------------------------------------------------------------
    // POST /api/fans/speed
    // -----------------------------------------------------------------------

    [Fact]
    public void SetSpeed_ReturnsOk_ForValidFan()
    {
        var result = _ctrl.SetSpeed(new SetSpeedRequest { FanId = "fan1", Speed = 50 });
        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public void SetSpeed_ReturnsBadRequest_WhenFanIdEmpty()
    {
        var result = _ctrl.SetSpeed(new SetSpeedRequest { FanId = "", Speed = 50 });
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void SetSpeed_ReturnsBadRequest_WhenSpeedOutOfRange()
    {
        var result = _ctrl.SetSpeed(new SetSpeedRequest { FanId = "fan1", Speed = 150 });
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void SetSpeed_ReturnsNotFound_ForUnknownFan()
    {
        var result = _ctrl.SetSpeed(new SetSpeedRequest { FanId = "nonexistent", Speed = 50 });
        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public void SetSpeed_ReturnsBadRequest_WhenSpeedNegative()
    {
        var result = _ctrl.SetSpeed(new SetSpeedRequest { FanId = "fan1", Speed = -10 });
        Assert.IsType<BadRequestObjectResult>(result);
    }

    // -----------------------------------------------------------------------
    // POST /api/fans/{fanId}/auto
    // -----------------------------------------------------------------------

    [Fact]
    public void SetAuto_ReturnsOk_ForValidFan()
    {
        var result = _ctrl.SetAuto("fan1");
        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public void SetAuto_ReturnsNotFound_ForUnknownFan()
    {
        var result = _ctrl.SetAuto("nonexistent");
        Assert.IsType<NotFoundObjectResult>(result);
    }

    // -----------------------------------------------------------------------
    // PUT /api/fans/curves — dangerous curve gate
    // -----------------------------------------------------------------------

    [Fact]
    public void SetCurve_ReturnsOk_ForSafeCurve()
    {
        var req = new UpdateCurveRequest
        {
            Curve = new FanCurve
            {
                FanId    = "fan1",
                SensorId = "cpu",
                Points   = [new() { Temp = 30, Speed = 30 }, new() { Temp = 80, Speed = 80 }],
            },
        };
        var result = _ctrl.SetCurve(req);
        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public void SetCurve_ReturnsConflict_ForDangerousCurve_WithoutOverride()
    {
        var req = new UpdateCurveRequest
        {
            Curve = new FanCurve
            {
                FanId    = "fan1",
                SensorId = "cpu",
                Points   = [new() { Temp = 30, Speed = 10 }, new() { Temp = 90, Speed = 10 }],
            },
            AllowDangerous = false,
        };
        var result = _ctrl.SetCurve(req);
        Assert.IsType<ConflictObjectResult>(result);
    }

    [Fact]
    public void SetCurve_ReturnsOk_ForDangerousCurve_WithOverride()
    {
        var req = new UpdateCurveRequest
        {
            Curve = new FanCurve
            {
                FanId    = "fan1",
                SensorId = "cpu",
                Points   = [new() { Temp = 30, Speed = 10 }, new() { Temp = 90, Speed = 10 }],
            },
            AllowDangerous = true,
        };
        var result = _ctrl.SetCurve(req);
        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public void SetCurve_ReturnsBadRequest_WhenFanIdEmpty()
    {
        var req = new UpdateCurveRequest
        {
            Curve = new FanCurve
            {
                FanId    = "",
                SensorId = "cpu",
                Points   = [new() { Temp = 30, Speed = 30 }, new() { Temp = 80, Speed = 80 }],
            },
        };
        var result = _ctrl.SetCurve(req);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void SetCurve_ReturnsBadRequest_WhenLessThan2Points()
    {
        var req = new UpdateCurveRequest
        {
            Curve = new FanCurve
            {
                FanId    = "fan1",
                SensorId = "cpu",
                Points   = [new() { Temp = 50, Speed = 50 }],
            },
        };
        var result = _ctrl.SetCurve(req);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    // -----------------------------------------------------------------------
    // POST /api/fans/curves/validate
    // -----------------------------------------------------------------------

    [Fact]
    public void ValidateCurve_ReturnsSafe_ForGoodCurve()
    {
        var req = new ValidateCurveRequest
        {
            Points = [new() { Temp = 30, Speed = 40 }, new() { Temp = 80, Speed = 90 }],
        };
        var result = _ctrl.ValidateCurve(req);
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains("\"safe\":true", json);
    }

    [Fact]
    public void ValidateCurve_ReturnsWarnings_ForDangerousCurve()
    {
        var req = new ValidateCurveRequest
        {
            Points = [new() { Temp = 30, Speed = 5 }, new() { Temp = 95, Speed = 5 }],
        };
        var result = _ctrl.ValidateCurve(req);
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains("\"safe\":false", json);
    }

    // -----------------------------------------------------------------------
    // Safe mode — release / resume
    // -----------------------------------------------------------------------

    [Fact]
    public void ReleaseFanControl_ReturnsOk()
    {
        var result = _ctrl.ReleaseFanControl();
        Assert.IsType<OkObjectResult>(result);
        Assert.True(_fans.IsReleased);
    }

    [Fact]
    public async Task ResumeFanControl_ReturnsConflict_WithoutActiveProfile()
    {
        _fans.ReleaseFanControl();
        var result = await _ctrl.ResumeFanControl();
        Assert.IsType<ConflictObjectResult>(result);
    }

    // -----------------------------------------------------------------------
    // Fan settings
    // -----------------------------------------------------------------------

    [Fact]
    public async Task UpdateFanSettings_ReturnsOk()
    {
        var req = new UpdateFanSettingsRequest { MinSpeedPct = 25, ZeroRpmCapable = true };
        var result = await _ctrl.UpdateFanSettings("fan1", req);
        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task UpdateFanSettings_ReturnsBadRequest_WhenOutOfRange()
    {
        var req = new UpdateFanSettingsRequest { MinSpeedPct = 150 };
        var result = await _ctrl.UpdateFanSettings("fan1", req);
        Assert.IsType<BadRequestObjectResult>(result);
    }
}
