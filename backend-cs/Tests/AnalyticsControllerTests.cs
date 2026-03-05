using DriveChill.Api;
using Xunit;

namespace DriveChill.Tests;

public sealed class AnalyticsControllerTests
{
    [Fact]
    public void ResolveRange_BothStartEnd_UsesCustomRange()
    {
        var start = "2026-01-01T00:00:00Z";
        var end   = "2026-01-02T00:00:00Z";

        var (s, e) = AnalyticsController.ResolveRange(24.0, start, end);

        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), s);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), e);
    }

    [Fact]
    public void ResolveRange_OnlyStart_FallsBackToHours()
    {
        var before = DateTimeOffset.UtcNow;
        var (s, e) = AnalyticsController.ResolveRange(6.0, "2026-01-01T00:00:00Z", null);
        var after  = DateTimeOffset.UtcNow;

        // Should fall back to a 6-hour window ending at ~now, NOT use 2026-01-01.
        Assert.True(e >= before && e <= after.AddSeconds(1));
        Assert.True((e - s).TotalHours is > 5.9 and < 6.1);
    }

    [Fact]
    public void ResolveRange_OnlyEnd_FallsBackToHours()
    {
        var before = DateTimeOffset.UtcNow;
        var (s, e) = AnalyticsController.ResolveRange(12.0, null, "2026-06-01T00:00:00Z");
        var after  = DateTimeOffset.UtcNow;

        Assert.True(e >= before && e <= after.AddSeconds(1));
        Assert.True((e - s).TotalHours is > 11.9 and < 12.1);
    }

    [Fact]
    public void ResolveRange_Neither_UsesDefaultHours()
    {
        var before = DateTimeOffset.UtcNow;
        var (s, e) = AnalyticsController.ResolveRange(24.0, null, null);
        var after  = DateTimeOffset.UtcNow;

        Assert.True(e >= before && e <= after.AddSeconds(1));
        Assert.True((e - s).TotalHours is > 23.9 and < 24.1);
    }

    [Fact]
    public void ResolveRange_InvalidDates_FallsBackToHours()
    {
        var before = DateTimeOffset.UtcNow;
        var (s, e) = AnalyticsController.ResolveRange(8.0, "not-a-date", "also-not-a-date");
        var after  = DateTimeOffset.UtcNow;

        Assert.True(e >= before && e <= after.AddSeconds(1));
        Assert.True((e - s).TotalHours is > 7.9 and < 8.1);
    }

    [Fact]
    public void ResolveRange_StartAfterEnd_FallsBackToHours()
    {
        var before = DateTimeOffset.UtcNow;
        // start > end should be rejected
        var (s, e) = AnalyticsController.ResolveRange(4.0, "2026-02-01T00:00:00Z", "2026-01-01T00:00:00Z");
        var after  = DateTimeOffset.UtcNow;

        Assert.True(e >= before && e <= after.AddSeconds(1));
        Assert.True((e - s).TotalHours is > 3.9 and < 4.1);
    }
}
