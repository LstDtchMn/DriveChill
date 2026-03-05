using DriveChill.Services;
using Xunit;

namespace DriveChill.Tests;

public class TemperatureTargetServiceTests
{
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
}
