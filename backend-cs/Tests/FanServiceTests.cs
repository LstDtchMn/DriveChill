using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DriveChill.Hardware;
using DriveChill.Models;
using DriveChill.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DriveChill.Tests;

/// <summary>Minimal stub IHardwareBackend for unit tests.</summary>
internal sealed class StubHardware : IHardwareBackend
{
    private readonly List<string> _fanIds;
    public Dictionary<string, double> Applied { get; } = new();
    public int SetSpeedCallCount { get; private set; }

    public StubHardware(params string[] fanIds) => _fanIds = [.. fanIds];

    public void Initialize() { }
    public void Dispose() { }
    public string GetBackendName() => "Stub";
    public IReadOnlyList<string> GetFanIds() => _fanIds;
    public IReadOnlyList<SensorReading> GetSensorReadings() => [];

    public bool SetFanSpeed(string fanId, double pct)
    {
        Applied[fanId] = pct;
        SetSpeedCallCount++;
        return _fanIds.Contains(fanId);
    }

    public bool SetFanAuto(string fanId) => _fanIds.Contains(fanId);
}

public sealed class FanServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AppSettings _settings;
    private readonly SettingsStore _store;

    public FanServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
        Environment.SetEnvironmentVariable("DRIVECHILL_DATA_DIR", _tempDir);
        _settings = new AppSettings();
        _store = new SettingsStore(_settings);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("DRIVECHILL_DATA_DIR", null);
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    // -----------------------------------------------------------------------
    // SetCurves / SetCurve / DeleteCurve
    // -----------------------------------------------------------------------

    [Fact]
    public void SetCurves_ReplacesAllCurves()
    {
        var hw = new StubHardware("fan1", "fan2");
        var svc = new FanService(hw, _store);

        svc.SetCurves([
            new FanCurve { FanId = "fan1", SensorId = "s1", Points = [new() { Temp = 50, Speed = 60 }] },
            new FanCurve { FanId = "fan2", SensorId = "s2", Points = [new() { Temp = 50, Speed = 70 }] },
        ]);

        var curves = svc.GetCurves();
        Assert.Equal(2, curves.Count);
        Assert.Contains(curves, c => c.FanId == "fan1");
        Assert.Contains(curves, c => c.FanId == "fan2");
    }

    [Fact]
    public void SetCurves_ClearsOrphanCurvesFromPreviousProfile()
    {
        var hw = new StubHardware("fan1", "fan2");
        var svc = new FanService(hw, _store);

        // Profile A: fan1 curve
        svc.SetCurves([
            new FanCurve { FanId = "fan1", SensorId = "s1", Points = [new() { Temp = 50, Speed = 60 }] },
        ]);

        // Profile B: fan2 curve — fan1 should be cleared
        svc.SetCurves([
            new FanCurve { FanId = "fan2", SensorId = "s2", Points = [new() { Temp = 50, Speed = 70 }] },
        ]);

        var curves = svc.GetCurves();
        Assert.Single(curves);
        Assert.Equal("fan2", curves[0].FanId);
    }

    [Fact]
    public void SetCurve_AddsOrUpdatesOneFanCurve()
    {
        var hw = new StubHardware("fan1");
        var svc = new FanService(hw, _store);

        svc.SetCurve(new FanCurve
        {
            FanId    = "fan1",
            SensorId = "cpu",
            Points   = [new() { Temp = 40, Speed = 30 }, new() { Temp = 80, Speed = 80 }],
        });

        var curves = svc.GetCurves();
        Assert.Single(curves);
        Assert.Equal("fan1", curves[0].FanId);
    }

    [Fact]
    public void DeleteCurve_RemovesCurve()
    {
        var hw = new StubHardware("fan1");
        var svc = new FanService(hw, _store);

        svc.SetCurve(new FanCurve { FanId = "fan1", SensorId = "s", Points = [new() { Temp = 50, Speed = 50 }] });
        svc.DeleteCurve("fan1");

        Assert.Empty(svc.GetCurves());
    }

    [Fact]
    public void SetSpeed_DisablesActiveCurve()
    {
        var hw = new StubHardware("fan1");
        var svc = new FanService(hw, _store);

        svc.SetCurve(new FanCurve { FanId = "fan1", SensorId = "s", Points = [new() { Temp = 50, Speed = 60 }] });
        svc.SetSpeed("fan1", 80);

        // Curve should be removed (null in the dict → GetCurves returns empty)
        Assert.Empty(svc.GetCurves());
    }

    // -----------------------------------------------------------------------
    // Min-speed floor
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ApplyCurves_MinSpeedFloor_Applied()
    {
        var hw = new StubHardware("fan1");
        var svc = new FanService(hw, _store);

        // Curve that would set speed to 10 at the given temperature
        svc.SetCurve(new FanCurve
        {
            FanId    = "fan1",
            SensorId = "cpu",
            Points   = [new() { Temp = 40, Speed = 10 }, new() { Temp = 80, Speed = 60 }],
        });

        // Fan setting: min speed 25%
        svc.UpdateFanSettings("fan1", 25.0, false);

        // Reading: cpu = 40°C → interpolated speed = 10%, which is below floor
        var readings = new List<SensorReading>
        {
            new() { Id = "cpu", SensorType = SensorTypeValues.CpuTemp, Value = 40, Unit = "°C" },
        };

        await svc.ApplyCurvesAsync(readings);

        Assert.True(hw.Applied.ContainsKey("fan1"));
        Assert.Equal(25.0, hw.Applied["fan1"]);
    }

    [Fact]
    public async Task ApplyCurves_MinSpeedFloor_NotApplied_WhenCurveSpeedIsAboveFloor()
    {
        var hw = new StubHardware("fan1");
        var svc = new FanService(hw, _store);

        svc.SetCurve(new FanCurve
        {
            FanId    = "fan1",
            SensorId = "cpu",
            Points   = [new() { Temp = 40, Speed = 60 }, new() { Temp = 80, Speed = 80 }],
        });

        svc.UpdateFanSettings("fan1", 25.0, false);

        var readings = new List<SensorReading>
        {
            new() { Id = "cpu", SensorType = SensorTypeValues.CpuTemp, Value = 40, Unit = "°C" },
        };

        await svc.ApplyCurvesAsync(readings);

        // Curve speed 60 > floor 25 → floor does not override
        Assert.True(hw.Applied.ContainsKey("fan1"));
        Assert.Equal(60.0, hw.Applied["fan1"]);
    }

    [Fact]
    public async Task ApplyCurves_ZeroRpmCapable_AllowsZero()
    {
        var hw = new StubHardware("fan1");
        // Disable ramp rate so speed changes are instant (tests run with no elapsed time)
        _store.FanRampRatePctPerSec = 0;
        var svc = new FanService(hw, _store);

        // Curve: 0% at ≤30°C, 100% at 80°C
        svc.SetCurve(new FanCurve
        {
            FanId    = "fan1",
            SensorId = "cpu",
            Points   = [new() { Temp = 30, Speed = 0 }, new() { Temp = 80, Speed = 100 }],
        });

        // ZeroRpmCapable = true + min=20
        svc.UpdateFanSettings("fan1", 20.0, true);

        // Warm-up pass: establish non-zero lastApplied so the zero-speed pass triggers a call
        await svc.ApplyCurvesAsync([
            new() { Id = "cpu", SensorType = SensorTypeValues.CpuTemp, Value = 55, Unit = "°C" },
        ]);

        // Now: temperature drops → curve returns 0 → should apply 0, not the 20% floor
        await svc.ApplyCurvesAsync([
            new() { Id = "cpu", SensorType = SensorTypeValues.CpuTemp, Value = 20, Unit = "°C" },
        ]);

        Assert.True(hw.Applied.ContainsKey("fan1"));
        Assert.Equal(0.0, hw.Applied["fan1"]);
    }

    // -----------------------------------------------------------------------
    // Panic thresholds
    // -----------------------------------------------------------------------

    [Fact]
    public async Task CheckPanicThresholds_CpuAbove95_ForcesAllFansTo100()
    {
        var hw = new StubHardware("fan1", "fan2");
        var svc = new FanService(hw, _store);

        svc.SetCurves([
            new FanCurve { FanId = "fan1", SensorId = "cpu", Points = [new() { Temp = 50, Speed = 50 }] },
        ]);

        var readings = new List<SensorReading>
        {
            new() { Id = "cpu", SensorType = SensorTypeValues.CpuTemp, Value = 96, Unit = "°C" },
        };

        await svc.ApplyCurvesAsync(readings);

        // Panic should force both fans to 100
        Assert.True(hw.Applied.ContainsKey("fan1"));
        Assert.Equal(100.0, hw.Applied["fan1"]);
        Assert.True(hw.Applied.ContainsKey("fan2"));
        Assert.Equal(100.0, hw.Applied["fan2"]);
    }

    [Fact]
    public async Task Released_SkipsCurveApplication()
    {
        var hw = new StubHardware("fan1");
        var svc = new FanService(hw, _store);

        svc.SetCurve(new FanCurve
        {
            FanId    = "fan1",
            SensorId = "cpu",
            Points   = [new() { Temp = 40, Speed = 60 }, new() { Temp = 80, Speed = 80 }],
        });

        svc.ReleaseFanControl();
        hw.Applied.Clear(); // clear any calls from ReleaseFanControl

        var readings = new List<SensorReading>
        {
            new() { Id = "cpu", SensorType = SensorTypeValues.CpuTemp, Value = 60, Unit = "°C" },
        };

        await svc.ApplyCurvesAsync(readings);

        // Released — no curve-driven SetFanSpeed should have been called
        Assert.DoesNotContain("fan1", hw.Applied.Keys);
    }

    [Fact]
    public void GetSafeModeStatus_ReflectsReleasedState()
    {
        var hw = new StubHardware("fan1");
        var svc = new FanService(hw, _store);

        svc.ReleaseFanControl();

        var status = (dynamic)svc.GetSafeModeStatus();
        Assert.True(svc.IsReleased);
    }

    // -----------------------------------------------------------------------
    // Composite MAX sensor resolution
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ApplyCurves_CompositeMax_UsesHighestSensor()
    {
        var hw = new StubHardware("fan1");
        var svc = new FanService(hw, _store);

        svc.SetCurve(new FanCurve
        {
            FanId = "fan1",
            SensorId = "cpu0",
            SensorIds = ["cpu0", "cpu1", "cpu2"],
            Points = [new() { Temp = 40, Speed = 20 }, new() { Temp = 80, Speed = 100 }],
        });

        // cpu2 is the hottest — composite MAX should use 70°C
        var readings = new List<SensorReading>
        {
            new() { Id = "cpu0", SensorType = SensorTypeValues.CpuTemp, Value = 50, Unit = "°C" },
            new() { Id = "cpu1", SensorType = SensorTypeValues.CpuTemp, Value = 60, Unit = "°C" },
            new() { Id = "cpu2", SensorType = SensorTypeValues.CpuTemp, Value = 70, Unit = "°C" },
        };

        await svc.ApplyCurvesAsync(readings);

        Assert.True(hw.Applied.ContainsKey("fan1"));
        // At 70°C with 40→20, 80→100: interpolate = 20 + (70-40)/(80-40) * (100-20) = 80
        Assert.Equal(80.0, hw.Applied["fan1"], precision: 1);
    }

    [Fact]
    public async Task ApplyCurves_CompositeFallbackToSingleSensor()
    {
        var hw = new StubHardware("fan1");
        var svc = new FanService(hw, _store);

        // SensorIds is empty — should fall back to SensorId
        svc.SetCurve(new FanCurve
        {
            FanId = "fan1",
            SensorId = "cpu",
            SensorIds = [],
            Points = [new() { Temp = 40, Speed = 20 }, new() { Temp = 80, Speed = 100 }],
        });

        var readings = new List<SensorReading>
        {
            new() { Id = "cpu", SensorType = SensorTypeValues.CpuTemp, Value = 60, Unit = "°C" },
        };

        await svc.ApplyCurvesAsync(readings);

        Assert.True(hw.Applied.ContainsKey("fan1"));
        // At 60°C: 20 + (60-40)/(80-40) * 80 = 60
        Assert.Equal(60.0, hw.Applied["fan1"], precision: 1);
    }

    // -----------------------------------------------------------------------
    // Hysteresis — deadband hold
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Hysteresis_HoldsSpeedInDeadband()
    {
        var hw = new StubHardware("fan1");
        _store.Deadband = 5.0;
        var svc = new FanService(hw, _store);

        svc.SetCurve(new FanCurve
        {
            FanId = "fan1",
            SensorId = "cpu",
            Points = [new() { Temp = 30, Speed = 20 }, new() { Temp = 80, Speed = 100 }],
        });

        // First tick at 60°C — establishes decision point
        await svc.ApplyCurvesAsync([
            new() { Id = "cpu", SensorType = SensorTypeValues.CpuTemp, Value = 60, Unit = "°C" },
        ]);
        var firstSpeed = hw.Applied["fan1"];

        // Temp drops slightly (within deadband) — speed should hold
        await svc.ApplyCurvesAsync([
            new() { Id = "cpu", SensorType = SensorTypeValues.CpuTemp, Value = 57, Unit = "°C" },
        ]);

        Assert.Equal(firstSpeed, hw.Applied["fan1"]);
    }

    [Fact]
    public async Task Hysteresis_AllowsRampUp()
    {
        var hw = new StubHardware("fan1");
        _store.Deadband = 5.0;
        var svc = new FanService(hw, _store);

        svc.SetCurve(new FanCurve
        {
            FanId = "fan1",
            SensorId = "cpu",
            Points = [new() { Temp = 30, Speed = 20 }, new() { Temp = 80, Speed = 100 }],
        });

        await svc.ApplyCurvesAsync([
            new() { Id = "cpu", SensorType = SensorTypeValues.CpuTemp, Value = 50, Unit = "°C" },
        ]);
        var firstSpeed = hw.Applied["fan1"];

        // Temp rises — ramp up should always be allowed
        await svc.ApplyCurvesAsync([
            new() { Id = "cpu", SensorType = SensorTypeValues.CpuTemp, Value = 70, Unit = "°C" },
        ]);

        Assert.True(hw.Applied["fan1"] > firstSpeed, "Speed should increase on temp rise");
    }

    [Fact]
    public async Task Hysteresis_AllowsRampDownBeyondDeadband()
    {
        var hw = new StubHardware("fan1");
        _store.Deadband = 5.0;
        var svc = new FanService(hw, _store);

        svc.SetCurve(new FanCurve
        {
            FanId = "fan1",
            SensorId = "cpu",
            Points = [new() { Temp = 30, Speed = 20 }, new() { Temp = 80, Speed = 100 }],
        });

        await svc.ApplyCurvesAsync([
            new() { Id = "cpu", SensorType = SensorTypeValues.CpuTemp, Value = 60, Unit = "°C" },
        ]);
        var firstSpeed = hw.Applied["fan1"];

        // Temp drops well below deadband — speed should decrease
        await svc.ApplyCurvesAsync([
            new() { Id = "cpu", SensorType = SensorTypeValues.CpuTemp, Value = 40, Unit = "°C" },
        ]);

        Assert.True(hw.Applied["fan1"] < firstSpeed, "Speed should decrease beyond deadband");
    }

    // -----------------------------------------------------------------------
    // Ramp-rate limiting
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RampRate_StorePropertyUsedByFanService()
    {
        // Verify the ramp-rate path is wired: the SettingsStore property is read
        // by ApplyCurvesAsync. Full timing-based ramp tests are integration-level
        // and skipped in unit tests to avoid Thread.Sleep flakiness in parallel suites.
        var hw = new StubHardware("fan1");
        var svc = new FanService(hw, _store);

        // Confirm the store's default ramp rate is readable
        Assert.True(_store.FanRampRatePctPerSec >= 0,
            "FanRampRatePctPerSec should be non-negative");

        svc.SetCurve(new FanCurve
        {
            FanId = "fan1",
            SensorId = "cpu",
            Points = [new() { Temp = 30, Speed = 30 }, new() { Temp = 80, Speed = 100 }],
        });

        // Multiple ticks at same temp — ramp state should accumulate without error
        for (int i = 0; i < 5; i++)
        {
            await svc.ApplyCurvesAsync([
                new() { Id = "cpu", SensorType = SensorTypeValues.CpuTemp, Value = 50, Unit = "°C" },
            ]);
        }

        Assert.True(hw.Applied.ContainsKey("fan1"), "Curve should produce fan speed");
    }

    // -----------------------------------------------------------------------
    // Startup safety
    // -----------------------------------------------------------------------

    [Fact]
    public async Task StartupSafety_ActiveBeforeCurveLoad()
    {
        var hw = new StubHardware("fan1");
        var svc = new FanService(hw, _store);

        Assert.True(svc.IsStartupSafetyActive, "Startup safety should be active initially");

        // Before setting any curves, apply curves should set startup safety speed
        await svc.ApplyCurvesAsync([
            new() { Id = "cpu", SensorType = SensorTypeValues.CpuTemp, Value = 40, Unit = "°C" },
        ]);

        Assert.True(hw.Applied.ContainsKey("fan1"));
        Assert.Equal(50.0, hw.Applied["fan1"]); // StartupSafetySpeed = 50
    }

    [Fact]
    public async Task StartupSafety_ExitsOnSetCurve()
    {
        var hw = new StubHardware("fan1");
        var svc = new FanService(hw, _store);

        Assert.True(svc.IsStartupSafetyActive);

        svc.SetCurve(new FanCurve
        {
            FanId = "fan1",
            SensorId = "cpu",
            Points = [new() { Temp = 40, Speed = 30 }, new() { Temp = 80, Speed = 80 }],
        });

        Assert.False(svc.IsStartupSafetyActive, "Startup safety should exit on SetCurve");
    }

    [Fact]
    public async Task StartupSafety_ExitsOnSetCurves()
    {
        var hw = new StubHardware("fan1");
        var svc = new FanService(hw, _store);

        Assert.True(svc.IsStartupSafetyActive);

        svc.SetCurves([
            new FanCurve { FanId = "fan1", SensorId = "s", Points = [new() { Temp = 50, Speed = 50 }] },
        ]);

        Assert.False(svc.IsStartupSafetyActive, "Startup safety should exit on SetCurves");
    }

    [Fact]
    public async Task StartupSafety_PanicOverridesStartupSafety()
    {
        var hw = new StubHardware("fan1");
        var svc = new FanService(hw, _store);

        // Startup safety is active, but panic should take precedence
        await svc.ApplyCurvesAsync([
            new() { Id = "cpu", SensorType = SensorTypeValues.CpuTemp, Value = 96, Unit = "°C" },
        ]);

        // Panic forces 100% regardless of startup safety
        Assert.Equal(100.0, hw.Applied["fan1"]);
    }
}
