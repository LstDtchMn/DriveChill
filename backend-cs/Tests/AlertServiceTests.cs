using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using DriveChill.Models;
using DriveChill.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DriveChill.Tests;

public sealed class AlertServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DbService _db;
    private readonly AlertService _svc;

    public AlertServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
        Environment.SetEnvironmentVariable("DRIVECHILL_DATA_DIR", _tempDir);

        var settings = new AppSettings();
        _db  = new DbService(settings, NullLogger<DbService>.Instance);
        _svc = new AlertService(_db);
        _svc.InitializeAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _db.Dispose();
        Environment.SetEnvironmentVariable("DRIVECHILL_DATA_DIR", null);
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private AlertRule AddAboveRule(double threshold, string sensorId = "cpu")
    {
        return _svc.AddRuleAsync(new CreateAlertRuleRequest
        {
            SensorId   = sensorId,
            SensorName = "CPU Temp",
            Threshold  = threshold,
            Condition  = "above",
        }).GetAwaiter().GetResult();
    }

    private AlertRule AddBelowRule(double threshold, string sensorId = "drive")
    {
        return _svc.AddRuleAsync(new CreateAlertRuleRequest
        {
            SensorId   = sensorId,
            SensorName = "Drive Temp",
            Threshold  = threshold,
            Condition  = "below",
        }).GetAwaiter().GetResult();
    }

    private static SensorReading Reading(string id, double value) =>
        new() { Id = id, SensorType = SensorTypeValues.CpuTemp, Value = value, Unit = "C" };

    // -----------------------------------------------------------------------

    [Fact]
    public void Evaluate_FiresEvent_WhenAboveThreshold()
    {
        AddAboveRule(70);

        var fired = _svc.Evaluate([Reading("cpu", 75)]);

        Assert.Single(fired);
        Assert.Equal(75.0, fired[0].ActualValue);
        Assert.Equal("above", fired[0].Condition);
    }

    [Fact]
    public void Evaluate_DoesNotFire_BelowThreshold()
    {
        AddAboveRule(70);

        var fired = _svc.Evaluate([Reading("cpu", 65)]);

        Assert.Empty(fired);
    }

    [Fact]
    public void Evaluate_DoesNotFireAgain_WhileAlreadyActive()
    {
        AddAboveRule(70);

        _svc.Evaluate([Reading("cpu", 75)]); // fires first time
        var second = _svc.Evaluate([Reading("cpu", 80)]); // still above, should not re-fire

        Assert.Empty(second);
    }

    [Fact]
    public void Evaluate_ClearsActiveAlert_WhenConditionNoLongerMet()
    {
        AddAboveRule(70);

        _svc.Evaluate([Reading("cpu", 75)]); // fire
        _svc.Evaluate([Reading("cpu", 65)]); // clear

        // Re-trigger -- should fire again (was cleared)
        var refired = _svc.Evaluate([Reading("cpu", 80)]);
        Assert.Single(refired);
    }

    [Fact]
    public void Evaluate_SetsCleared_OnEventWhenConditionDrops()
    {
        AddAboveRule(70);

        _svc.Evaluate([Reading("cpu", 75)]);
        _svc.Evaluate([Reading("cpu", 65)]);

        var events = _svc.GetEvents(100);
        Assert.Single(events);
        Assert.True(events[0].Cleared);
    }

    [Fact]
    public void Evaluate_FiresEvent_WhenBelowThreshold()
    {
        AddBelowRule(20);

        var fired = _svc.Evaluate([new SensorReading
        {
            Id = "drive", SensorType = SensorTypeValues.CpuTemp, Value = 15, Unit = "C",
        }]);

        Assert.Single(fired);
        Assert.Equal("below", fired[0].Condition);
        Assert.Equal(15.0, fired[0].ActualValue);
    }

    [Fact]
    public void Evaluate_DoesNotFire_AtExactThreshold_AboveCondition()
    {
        AddAboveRule(70);

        var fired = _svc.Evaluate([Reading("cpu", 70)]);

        Assert.Empty(fired);
    }

    [Fact]
    public async Task Evaluate_DoesNotFire_WhenRuleDisabled()
    {
        var rule = AddAboveRule(70);
        await _svc.DeleteRuleAsync(rule.RuleId);
        await _svc.AddRuleAsync(new CreateAlertRuleRequest
        {
            SensorId  = "cpu",
            Threshold = 70,
            Condition = "above",
        });
        var fired = _svc.Evaluate([Reading("cpu", 80)]);
        Assert.Single(fired);
    }

    [Fact]
    public async Task DeleteRule_ClearsActiveAlertState()
    {
        var rule = AddAboveRule(70);

        _svc.Evaluate([Reading("cpu", 75)]); // fire -> active
        await _svc.DeleteRuleAsync(rule.RuleId);

        // After delete, active events should be gone
        var active = _svc.GetActiveEvents();
        Assert.Empty(active);
    }

    [Fact]
    public void GetActiveEvents_ReturnsOnlyUnclearedActiveEvents()
    {
        AddAboveRule(70, "cpu");
        AddAboveRule(60, "gpu");

        _svc.Evaluate([
            Reading("cpu", 75),
            new SensorReading { Id = "gpu", SensorType = SensorTypeValues.GpuTemp, Value = 65, Unit = "C" },
        ]);

        // Clear cpu alert
        _svc.Evaluate([
            Reading("cpu", 50),
            new SensorReading { Id = "gpu", SensorType = SensorTypeValues.GpuTemp, Value = 65, Unit = "C" },
        ]);

        var active = _svc.GetActiveEvents();
        Assert.Single(active);
        Assert.Equal("gpu", active[0].SensorId);
    }

    [Fact]
    public void ClearEvents_EmptiesAllState()
    {
        AddAboveRule(70);
        _svc.Evaluate([Reading("cpu", 80)]);

        _svc.ClearEvents();

        Assert.Empty(_svc.GetEvents(100));
        Assert.Empty(_svc.GetActiveEvents());
    }

    // -----------------------------------------------------------------------
    // Profile switching -- revert_after_clear semantics
    // -----------------------------------------------------------------------

    private AlertRule AddActionRule(double threshold, string profileId,
        bool revertAfterClear = true, string sensorId = "cpu")
    {
        return _svc.AddRuleAsync(new CreateAlertRuleRequest
        {
            SensorId   = sensorId,
            SensorName = "CPU Temp",
            Threshold  = threshold,
            Condition  = "above",
            Action     = new AlertAction
            {
                Type             = "switch_profile",
                ProfileId        = profileId,
                RevertAfterClear = revertAfterClear,
            },
        }).GetAwaiter().GetResult();
    }

    [Fact]
    public void ProfileSwitch_Fires_WhenAlertTriggered()
    {
        var activated = new List<string>();
        _svc.SetActivateProfileFn(id => { activated.Add(id); return Task.CompletedTask; });
        _svc.SetPreAlertProfile("default");

        AddActionRule(70, "perf");

        _svc.Evaluate([Reading("cpu", 75)]);
        System.Threading.Thread.Sleep(50);

        Assert.Contains("perf", activated);
    }

    [Fact]
    public void ProfileSwitch_Reverts_WhenRevertAfterClearTrue()
    {
        var activated = new List<string>();
        _svc.SetActivateProfileFn(id => { activated.Add(id); return Task.CompletedTask; });
        _svc.SetPreAlertProfile("default");

        AddActionRule(70, "perf", revertAfterClear: true);

        _svc.Evaluate([Reading("cpu", 75)]); // fire
        System.Threading.Thread.Sleep(50);
        activated.Clear();

        _svc.Evaluate([Reading("cpu", 60)]); // clear
        System.Threading.Thread.Sleep(50);

        Assert.Contains("default", activated);
    }

    [Fact]
    public void ProfileSwitch_DoesNotRevert_WhenRevertAfterClearFalse()
    {
        var activated = new List<string>();
        _svc.SetActivateProfileFn(id => { activated.Add(id); return Task.CompletedTask; });
        _svc.SetPreAlertProfile("default");

        AddActionRule(70, "perf", revertAfterClear: false);

        _svc.Evaluate([Reading("cpu", 75)]); // fire
        System.Threading.Thread.Sleep(50);
        activated.Clear();

        _svc.Evaluate([Reading("cpu", 60)]); // clear
        System.Threading.Thread.Sleep(50);

        Assert.DoesNotContain("default", activated);
    }

    [Fact]
    public void ProfileSwitch_NoRevertWhenNoPreAlertProfile()
    {
        var activated = new List<string>();
        _svc.SetActivateProfileFn(id => { activated.Add(id); return Task.CompletedTask; });

        AddActionRule(70, "perf", revertAfterClear: true);

        _svc.Evaluate([Reading("cpu", 75)]); // fire
        System.Threading.Thread.Sleep(50);
        activated.Clear();

        _svc.Evaluate([Reading("cpu", 60)]); // clear
        System.Threading.Thread.Sleep(50);

        Assert.Empty(activated);
    }

    [Fact]
    public void ProfileSwitch_MixedRevertAfterClear_NoRevertWins()
    {
        var activated = new List<string>();
        _svc.SetActivateProfileFn(id => { activated.Add(id); return Task.CompletedTask; });
        _svc.SetPreAlertProfile("default");

        AddActionRule(70,  "profile_A", revertAfterClear: true,  sensorId: "cpu");
        AddActionRule(80,  "profile_B", revertAfterClear: false, sensorId: "cpu");

        _svc.Evaluate([Reading("cpu", 85)]);
        System.Threading.Thread.Sleep(50);
        activated.Clear();

        _svc.Evaluate([Reading("cpu", 60)]);
        System.Threading.Thread.Sleep(50);

        Assert.DoesNotContain("default", activated);
    }
}
