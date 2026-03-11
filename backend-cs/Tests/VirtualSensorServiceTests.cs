using System.Collections.Generic;
using DriveChill.Models;
using DriveChill.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DriveChill.Tests;

public sealed class VirtualSensorServiceTests
{
    private readonly VirtualSensorService _svc;

    public VirtualSensorServiceTests()
    {
        _svc = new VirtualSensorService(NullLogger<VirtualSensorService>.Instance);
    }

    private static VirtualSensor MakeSensor(string id, string type, List<string> sourceIds,
        List<double>? weights = null, double? windowSeconds = null, double offset = 0)
    {
        return new VirtualSensor
        {
            Id = id,
            Name = $"VS {id}",
            Type = type,
            SourceIds = sourceIds,
            Weights = weights,
            WindowSeconds = windowSeconds,
            Offset = offset,
            Enabled = true,
        };
    }

    // -----------------------------------------------------------------------
    // max
    // -----------------------------------------------------------------------

    [Fact]
    public void Max_ReturnsHighestSourceValue()
    {
        _svc.Load(new List<VirtualSensor>
        {
            MakeSensor("vs_max", "max", new List<string> { "s1", "s2", "s3" }),
        });

        var result = _svc.ResolveAll(new Dictionary<string, double>
        {
            ["s1"] = 30.0,
            ["s2"] = 55.0,
            ["s3"] = 42.0,
        });

        Assert.Equal(55.0, result["vs_max"]);
    }

    // -----------------------------------------------------------------------
    // min
    // -----------------------------------------------------------------------

    [Fact]
    public void Min_ReturnsLowestSourceValue()
    {
        _svc.Load(new List<VirtualSensor>
        {
            MakeSensor("vs_min", "min", new List<string> { "s1", "s2", "s3" }),
        });

        var result = _svc.ResolveAll(new Dictionary<string, double>
        {
            ["s1"] = 30.0,
            ["s2"] = 55.0,
            ["s3"] = 42.0,
        });

        Assert.Equal(30.0, result["vs_min"]);
    }

    // -----------------------------------------------------------------------
    // avg
    // -----------------------------------------------------------------------

    [Fact]
    public void Avg_ReturnsAverageOfSources()
    {
        _svc.Load(new List<VirtualSensor>
        {
            MakeSensor("vs_avg", "avg", new List<string> { "s1", "s2" }),
        });

        var result = _svc.ResolveAll(new Dictionary<string, double>
        {
            ["s1"] = 40.0,
            ["s2"] = 60.0,
        });

        Assert.Equal(50.0, result["vs_avg"]);
    }

    // -----------------------------------------------------------------------
    // delta
    // -----------------------------------------------------------------------

    [Fact]
    public void Delta_ReturnsDifferenceBetweenFirstTwoSources()
    {
        _svc.Load(new List<VirtualSensor>
        {
            MakeSensor("vs_delta", "delta", new List<string> { "s1", "s2" }),
        });

        var result = _svc.ResolveAll(new Dictionary<string, double>
        {
            ["s1"] = 80.0,
            ["s2"] = 30.0,
        });

        Assert.Equal(50.0, result["vs_delta"]);
    }

    [Fact]
    public void Delta_ReturnsNull_WhenLessThanTwoSources()
    {
        _svc.Load(new List<VirtualSensor>
        {
            MakeSensor("vs_delta1", "delta", new List<string> { "s1" }),
        });

        var result = _svc.ResolveAll(new Dictionary<string, double>
        {
            ["s1"] = 80.0,
        });

        Assert.False(result.ContainsKey("vs_delta1"));
    }

    // -----------------------------------------------------------------------
    // moving_avg (EMA)
    // -----------------------------------------------------------------------

    [Fact]
    public void MovingAvg_ReturnsInstantOnFirstCall()
    {
        _svc.Load(new List<VirtualSensor>
        {
            MakeSensor("vs_ema", "moving_avg", new List<string> { "s1" }, windowSeconds: 30),
        });

        var result = _svc.ResolveAll(new Dictionary<string, double>
        {
            ["s1"] = 42.0,
        });

        Assert.Equal(42.0, result["vs_ema"]);
    }

    // -----------------------------------------------------------------------
    // weighted_avg
    // -----------------------------------------------------------------------

    [Fact]
    public void WeightedAvg_UsesProvidedWeights()
    {
        _svc.Load(new List<VirtualSensor>
        {
            MakeSensor("vs_w", "weighted",
                new List<string> { "s1", "s2" },
                weights: new List<double> { 3.0, 1.0 }),
        });

        var result = _svc.ResolveAll(new Dictionary<string, double>
        {
            ["s1"] = 100.0,
            ["s2"] = 0.0,
        });

        // weighted = (100*3 + 0*1) / (3+1) = 75
        Assert.Equal(75.0, result["vs_w"]);
    }

    [Fact]
    public void WeightedAvg_FallsBackToEqualWeights_WhenWeightsMismatch()
    {
        _svc.Load(new List<VirtualSensor>
        {
            MakeSensor("vs_w2", "weighted",
                new List<string> { "s1", "s2" },
                weights: new List<double> { 1.0 }), // wrong count
        });

        var result = _svc.ResolveAll(new Dictionary<string, double>
        {
            ["s1"] = 40.0,
            ["s2"] = 60.0,
        });

        // Falls back to simple average
        Assert.Equal(50.0, result["vs_w2"]);
    }

    // -----------------------------------------------------------------------
    // Forward reference resolution (two-pass)
    // -----------------------------------------------------------------------

    [Fact]
    public void ForwardReference_ResolvesVirtualDependingOnVirtual()
    {
        _svc.Load(new List<VirtualSensor>
        {
            // vs_inner depends on hardware sensors -> pass 1
            MakeSensor("vs_inner", "avg", new List<string> { "s1", "s2" }),
            // vs_outer depends on vs_inner (a virtual sensor) -> pass 2
            MakeSensor("vs_outer", "max", new List<string> { "vs_inner", "s3" }),
        });

        var result = _svc.ResolveAll(new Dictionary<string, double>
        {
            ["s1"] = 40.0,
            ["s2"] = 60.0,
            ["s3"] = 45.0,
        });

        // vs_inner = avg(40,60) = 50
        Assert.Equal(50.0, result["vs_inner"]);
        // vs_outer = max(50, 45) = 50
        Assert.Equal(50.0, result["vs_outer"]);
    }

    // -----------------------------------------------------------------------
    // Missing source sensors
    // -----------------------------------------------------------------------

    [Fact]
    public void MissingSources_ProducesNoEntry()
    {
        _svc.Load(new List<VirtualSensor>
        {
            MakeSensor("vs_miss", "avg", new List<string> { "nonexistent1", "nonexistent2" }),
        });

        var result = _svc.ResolveAll(new Dictionary<string, double>
        {
            ["s1"] = 42.0,
        });

        Assert.False(result.ContainsKey("vs_miss"));
    }

    // -----------------------------------------------------------------------
    // Empty source list
    // -----------------------------------------------------------------------

    [Fact]
    public void EmptySourceList_ProducesNoEntry()
    {
        _svc.Load(new List<VirtualSensor>
        {
            MakeSensor("vs_empty", "max", new List<string>()),
        });

        var result = _svc.ResolveAll(new Dictionary<string, double>
        {
            ["s1"] = 42.0,
        });

        Assert.False(result.ContainsKey("vs_empty"));
    }

    // -----------------------------------------------------------------------
    // Offset applied
    // -----------------------------------------------------------------------

    [Fact]
    public void Offset_IsAddedToResult()
    {
        _svc.Load(new List<VirtualSensor>
        {
            MakeSensor("vs_off", "avg", new List<string> { "s1" }, offset: 5.0),
        });

        var result = _svc.ResolveAll(new Dictionary<string, double>
        {
            ["s1"] = 40.0,
        });

        Assert.Equal(45.0, result["vs_off"]);
    }
}
