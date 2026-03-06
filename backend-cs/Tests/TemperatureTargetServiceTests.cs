using System;
using System.Collections.Generic;
using System.IO;
using DriveChill.Models;
using DriveChill.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DriveChill.Tests;

public sealed class TemperatureTargetServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AppSettings _settings;
    private readonly DbService _db;

    public TemperatureTargetServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
        Environment.SetEnvironmentVariable("DRIVECHILL_DATA_DIR", _tempDir);
        _settings = new AppSettings();
        _db = new DbService(_settings, NullLogger<DbService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        Environment.SetEnvironmentVariable("DRIVECHILL_DATA_DIR", null);
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }
    // target=45, tolerance=5 → low=40, high=50, floor=20%

    [Fact]
    public void BelowBand_ReturnsFloorSpeed()
    {
        var speed = TemperatureTargetService.ComputeProportionalSpeed(35.0, 45.0, 5.0, 20.0);
        Assert.Equal(20.0, speed);
    }

    [Fact]
    public void AtLowBoundary_ReturnsFloorSpeed()
    {
        var speed = TemperatureTargetService.ComputeProportionalSpeed(40.0, 45.0, 5.0, 20.0);
        Assert.Equal(20.0, speed);
    }

    [Fact]
    public void AboveBand_Returns100()
    {
        var speed = TemperatureTargetService.ComputeProportionalSpeed(55.0, 45.0, 5.0, 20.0);
        Assert.Equal(100.0, speed);
    }

    [Fact]
    public void AtHighBoundary_Returns100()
    {
        var speed = TemperatureTargetService.ComputeProportionalSpeed(50.0, 45.0, 5.0, 20.0);
        Assert.Equal(100.0, speed);
    }

    [Fact]
    public void MidpointInterpolation()
    {
        // At target (midpoint), t = (45-40)/(2*5) = 0.5
        // speed = 20 + 0.5*(100-20) = 20 + 40 = 60
        var speed = TemperatureTargetService.ComputeProportionalSpeed(45.0, 45.0, 5.0, 20.0);
        Assert.Equal(60.0, speed, precision: 1);
    }

    [Fact]
    public void QuarterPoint()
    {
        // temp=42.5 → t = (42.5-40)/(10) = 0.25 → 20 + 0.25*80 = 40
        var speed = TemperatureTargetService.ComputeProportionalSpeed(42.5, 45.0, 5.0, 20.0);
        Assert.Equal(40.0, speed, precision: 1);
    }

    [Fact]
    public void ZeroFloorSpeed()
    {
        // floor=0, midpoint → 0 + 0.5*100 = 50
        var speed = TemperatureTargetService.ComputeProportionalSpeed(45.0, 45.0, 5.0, 0.0);
        Assert.Equal(50.0, speed, precision: 1);
    }

    [Fact]
    public void FullFloorSpeed()
    {
        // floor=100 → always 100 regardless of temp
        var speed = TemperatureTargetService.ComputeProportionalSpeed(45.0, 45.0, 5.0, 100.0);
        Assert.Equal(100.0, speed, precision: 1);
    }

    // -----------------------------------------------------------------------
    // PID mode — via Evaluate()
    // -----------------------------------------------------------------------

    [Fact]
    public async System.Threading.Tasks.Task Evaluate_PidMode_UsedWhenEnabled()
    {
        var svc = new TemperatureTargetService(_db);

        var target = await svc.AddAsync(new TemperatureTarget
        {
            Name = "CPU PID",
            SensorId = "cpu",
            FanIds = ["fan1"],
            TargetTempC = 50,
            ToleranceC = 10,
            MinFanSpeed = 20,
            Enabled = true,
            PidMode = true,
            PidKp = 5.0,
            PidKi = 0.1,
            PidKd = 0.0,
        }, default);

        // With temp > target, PID should produce speed > minFanSpeed
        var sensorMap = new Dictionary<string, double> { ["cpu"] = 55.0 };
        var result = svc.Evaluate(sensorMap);

        Assert.True(result.ContainsKey("fan1"));
        Assert.True(result["fan1"] > 20.0, "PID should exceed min at temp > target");
    }

    [Fact]
    public async System.Threading.Tasks.Task Evaluate_PidMode_AtTarget_ReturnsMinFanSpeed()
    {
        var svc = new TemperatureTargetService(_db);

        await svc.AddAsync(new TemperatureTarget
        {
            Name = "CPU PID exact",
            SensorId = "cpu",
            FanIds = ["fan1"],
            TargetTempC = 50,
            ToleranceC = 10,
            MinFanSpeed = 20,
            Enabled = true,
            PidMode = true,
            PidKp = 5.0,
            PidKi = 0.0,
            PidKd = 0.0,
        }, default);

        // error=0, integral=0, derivative=0 → output=0 → speed=minFanSpeed
        var sensorMap = new Dictionary<string, double> { ["cpu"] = 50.0 };
        var result = svc.Evaluate(sensorMap);

        Assert.True(result.ContainsKey("fan1"));
        Assert.Equal(20.0, result["fan1"]);
    }

    [Fact]
    public async System.Threading.Tasks.Task Evaluate_PidMode_IntegralClamped()
    {
        var svc = new TemperatureTargetService(_db);

        await svc.AddAsync(new TemperatureTarget
        {
            Name = "Integral test",
            SensorId = "cpu",
            FanIds = ["fan1"],
            TargetTempC = 50,
            ToleranceC = 10,
            MinFanSpeed = 0,
            Enabled = true,
            PidMode = true,
            PidKp = 0.0,
            PidKi = 1000.0,  // Very high Ki to test clamping
            PidKd = 0.0,
        }, default);

        // Multiple evaluations with large error — integral should be clamped
        var sensorMap = new Dictionary<string, double> { ["cpu"] = 1000.0 };
        for (int i = 0; i < 100; i++)
            svc.Evaluate(sensorMap);

        var result = svc.Evaluate(sensorMap);

        Assert.True(result.ContainsKey("fan1"));
        // Output should be clamped to 100%, not exceed it
        Assert.True(result["fan1"] <= 100.0, "Speed must not exceed 100%");
    }

    [Fact]
    public async System.Threading.Tasks.Task Evaluate_MultiFan_TakesMax()
    {
        var svc = new TemperatureTargetService(_db);

        // Two targets driving the same fan — hottest should win
        await svc.AddAsync(new TemperatureTarget
        {
            Id = "t_hot",
            Name = "Hot",
            SensorId = "cpu",
            FanIds = ["fan1"],
            TargetTempC = 50,
            ToleranceC = 10,
            MinFanSpeed = 20,
            Enabled = true,
        }, default);

        await svc.AddAsync(new TemperatureTarget
        {
            Id = "t_cool",
            Name = "Cool",
            SensorId = "gpu",
            FanIds = ["fan1"],
            TargetTempC = 50,
            ToleranceC = 10,
            MinFanSpeed = 20,
            Enabled = true,
        }, default);

        var sensorMap = new Dictionary<string, double> { ["cpu"] = 58.0, ["gpu"] = 42.0 };
        var result = svc.Evaluate(sensorMap);

        // CPU produces a higher speed than GPU — max should be used
        var cpuOnly = TemperatureTargetService.ComputeProportionalSpeed(58, 50, 10, 20);
        Assert.Equal(cpuOnly, result["fan1"]);
    }
}
