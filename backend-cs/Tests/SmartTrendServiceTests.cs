using System.Collections.Generic;
using DriveChill.Services;
using Xunit;

namespace DriveChill.Tests;

/// <summary>
/// Unit tests for <see cref="SmartTrendService"/> — condition detection,
/// deduplication, and alert event injection into <see cref="AlertService"/>.
/// </summary>
public sealed class SmartTrendServiceTests
{
    private static SmartTrendService MakeSvc() => new();

    // -----------------------------------------------------------------------
    // Reallocated-sector increase
    // -----------------------------------------------------------------------

    [Fact]
    public void ReallocatedIncrease_Fires_WhenDeltaMeetsThreshold()
    {
        var svc = MakeSvc();
        // First call establishes baseline (no previous snapshot)
        svc.CheckDrive("d1", "Drive A", reallocatedSectors: 5, wearPercentUsed: null, powerOnHours: null);

        // Second call — sectors increased by 2 (>= threshold of 1)
        var alerts = svc.CheckDrive("d1", "Drive A", reallocatedSectors: 7, wearPercentUsed: null, powerOnHours: null);

        Assert.Single(alerts);
        Assert.Equal("reallocated_increase", alerts[0].Condition);
        Assert.Equal("critical", alerts[0].Severity);
        Assert.Equal(7.0, alerts[0].ActualValue);
        Assert.Equal(1.0, alerts[0].Threshold); // default ReallocatedSectorDeltaThreshold
        Assert.Contains("Drive A", alerts[0].Message);
    }

    [Fact]
    public void ReallocatedIncrease_DoesNotFire_WhenSectorsDontChange()
    {
        var svc = MakeSvc();
        svc.CheckDrive("d1", "Drive A", reallocatedSectors: 5, wearPercentUsed: null, powerOnHours: null);

        var alerts = svc.CheckDrive("d1", "Drive A", reallocatedSectors: 5, wearPercentUsed: null, powerOnHours: null);

        Assert.Empty(alerts);
    }

    [Fact]
    public void ReallocatedIncrease_DoesNotFire_WhenNoPreviousSnapshot()
    {
        var svc = MakeSvc();
        // No previous snapshot → no delta can be computed
        var alerts = svc.CheckDrive("d1", "Drive A", reallocatedSectors: 10, wearPercentUsed: null, powerOnHours: null);

        Assert.Empty(alerts);
    }

    [Fact]
    public void ReallocatedIncrease_Deduplicates_WhileConditionActive()
    {
        var svc = MakeSvc();
        svc.CheckDrive("d1", "Drive A", reallocatedSectors: 5, wearPercentUsed: null, powerOnHours: null);

        // First crossing — fires
        var first = svc.CheckDrive("d1", "Drive A", reallocatedSectors: 7, wearPercentUsed: null, powerOnHours: null);
        // Second crossing with same active condition — should NOT fire again
        var second = svc.CheckDrive("d1", "Drive A", reallocatedSectors: 9, wearPercentUsed: null, powerOnHours: null);

        Assert.Single(first);
        Assert.Empty(second);
    }

    // -----------------------------------------------------------------------
    // Wear percentage
    // -----------------------------------------------------------------------

    [Fact]
    public void WearCritical_Fires_WhenAboveThreshold()
    {
        var svc = MakeSvc();
        var alerts = svc.CheckDrive("d1", "SSD", reallocatedSectors: null, wearPercentUsed: 92.0, powerOnHours: null);

        Assert.Single(alerts);
        Assert.Equal("wear_critical", alerts[0].Condition);
        Assert.Equal("critical", alerts[0].Severity);
        Assert.Equal(92.0, alerts[0].ActualValue);
        Assert.Equal(svc.WearCriticalPct, alerts[0].Threshold);
    }

    [Fact]
    public void WearWarning_Fires_WhenBetweenWarningAndCritical()
    {
        var svc = MakeSvc();
        var alerts = svc.CheckDrive("d1", "SSD", reallocatedSectors: null, wearPercentUsed: 85.0, powerOnHours: null);

        Assert.Single(alerts);
        Assert.Equal("wear_warning", alerts[0].Condition);
        Assert.Equal("warning", alerts[0].Severity);
        Assert.Equal(85.0, alerts[0].ActualValue);
        Assert.Equal(svc.WearWarningPct, alerts[0].Threshold);
    }

    [Fact]
    public void WearWarning_DoesNotFire_BelowThreshold()
    {
        var svc = MakeSvc();
        var alerts = svc.CheckDrive("d1", "SSD", reallocatedSectors: null, wearPercentUsed: 50.0, powerOnHours: null);

        Assert.Empty(alerts);
    }

    [Fact]
    public void WearWarning_Deduplicates_WhileConditionActive()
    {
        var svc = MakeSvc();
        var first  = svc.CheckDrive("d1", "SSD", reallocatedSectors: null, wearPercentUsed: 85.0, powerOnHours: null);
        var second = svc.CheckDrive("d1", "SSD", reallocatedSectors: null, wearPercentUsed: 87.0, powerOnHours: null);

        Assert.Single(first);
        Assert.Empty(second);
    }

    // -----------------------------------------------------------------------
    // Power-on hours
    // -----------------------------------------------------------------------

    [Fact]
    public void PohCritical_Fires_WhenAboveThreshold()
    {
        var svc = MakeSvc();
        var alerts = svc.CheckDrive("d1", "HDD", reallocatedSectors: null, wearPercentUsed: null, powerOnHours: 51_000);

        Assert.Single(alerts);
        Assert.Equal("poh_critical", alerts[0].Condition);
        Assert.Equal(51_000.0, alerts[0].ActualValue);
        Assert.Equal(svc.PowerOnHoursCritical, alerts[0].Threshold);
    }

    [Fact]
    public void PohWarning_Fires_WhenBetweenWarningAndCritical()
    {
        var svc = MakeSvc();
        var alerts = svc.CheckDrive("d1", "HDD", reallocatedSectors: null, wearPercentUsed: null, powerOnHours: 40_000);

        Assert.Single(alerts);
        Assert.Equal("poh_warning", alerts[0].Condition);
        Assert.Equal(40_000.0, alerts[0].ActualValue);
    }

    [Fact]
    public void PohWarning_DoesNotFire_BelowThreshold()
    {
        var svc = MakeSvc();
        var alerts = svc.CheckDrive("d1", "HDD", reallocatedSectors: null, wearPercentUsed: null, powerOnHours: 10_000);

        Assert.Empty(alerts);
    }

    // -----------------------------------------------------------------------
    // Multiple conditions in one call
    // -----------------------------------------------------------------------

    [Fact]
    public void MultipleTriggers_FireInSameCall()
    {
        var svc = MakeSvc();
        // Establish wear baseline
        svc.CheckDrive("d1", "SSD", reallocatedSectors: 5, wearPercentUsed: 50.0, powerOnHours: 1_000);

        // Trigger both reallocated increase and wear critical simultaneously
        var alerts = svc.CheckDrive("d1", "SSD", reallocatedSectors: 8, wearPercentUsed: 95.0, powerOnHours: 1_000);

        Assert.Equal(2, alerts.Count);
        var conditions = new HashSet<string>(System.Linq.Enumerable.Select(alerts, a => a.Condition));
        Assert.Contains("reallocated_increase", conditions);
        Assert.Contains("wear_critical", conditions);
    }

    // -----------------------------------------------------------------------
    // AlertService injection
    // -----------------------------------------------------------------------

    [Fact]
    public void InjectEvent_AddsToAlertService()
    {
        var db     = MakeDb();
        var alerts = new AlertService(db);

        alerts.InjectEvent(
            ruleId:      "smart_d1_wear_critical",
            sensorId:    "hdd_temp_d1",
            sensorName:  "My SSD",
            actualValue: 95.0,
            threshold:   90.0,
            condition:   "above",
            message:     "My SSD: wear level critical (95.0% used)");

        var events = alerts.GetEvents(100);
        Assert.Single(events);
        Assert.Equal("smart_d1_wear_critical", events[0].RuleId);
        Assert.Equal(95.0, events[0].ActualValue);
        Assert.Equal(90.0, events[0].Threshold);
    }

    [Fact]
    public void InjectEvent_Trims_WhenOver500()
    {
        var db     = MakeDb();
        var alerts = new AlertService(db);

        for (int i = 0; i < 510; i++)
            alerts.InjectEvent($"r{i}", "s", "S", i, 0, "above", "");

        // Should be capped at 500
        Assert.Equal(500, alerts.GetEvents(1000).Count);
    }

    private static DbService MakeDb()
    {
        var settings = new AppSettings();
        return new DbService(settings, Microsoft.Extensions.Logging.Abstractions.NullLogger<DbService>.Instance);
    }
}
