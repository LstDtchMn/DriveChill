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
        _svc.Dispose();
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

    /// <summary>
    /// Creates a profile-switch callback that signals a semaphore on each invocation,
    /// replacing Thread.Sleep with deterministic waiting.
    /// </summary>
    private static (List<string> Activated, SemaphoreSlim Signal, Func<string, Task> Fn) MakeSignalingCallback()
    {
        var activated = new List<string>();
        var signal = new SemaphoreSlim(0);
        return (activated, signal, id => { activated.Add(id); signal.Release(); return Task.CompletedTask; });
    }

    private static void WaitForCallback(SemaphoreSlim signal, int timeoutMs = 2000)
    {
        Assert.True(signal.Wait(timeoutMs), "Profile callback was not invoked within timeout");
    }

    [Fact]
    public void ProfileSwitch_Fires_WhenAlertTriggered()
    {
        var (activated, signal, fn) = MakeSignalingCallback();
        _svc.SetActivateProfileFn(fn);
        _svc.SetPreAlertProfile("default");

        AddActionRule(70, "perf");

        _svc.Evaluate([Reading("cpu", 75)]);
        WaitForCallback(signal);

        Assert.Contains("perf", activated);
    }

    [Fact]
    public void ProfileSwitch_Reverts_WhenRevertAfterClearTrue()
    {
        var (activated, signal, fn) = MakeSignalingCallback();
        _svc.SetActivateProfileFn(fn);
        _svc.SetPreAlertProfile("default");

        AddActionRule(70, "perf", revertAfterClear: true);

        _svc.Evaluate([Reading("cpu", 75)]); // fire
        WaitForCallback(signal);
        activated.Clear();

        _svc.Evaluate([Reading("cpu", 60)]); // clear
        WaitForCallback(signal);

        Assert.Contains("default", activated);
    }

    [Fact]
    public void ProfileSwitch_DoesNotRevert_WhenRevertAfterClearFalse()
    {
        var (activated, signal, fn) = MakeSignalingCallback();
        _svc.SetActivateProfileFn(fn);
        _svc.SetPreAlertProfile("default");

        AddActionRule(70, "perf", revertAfterClear: false);

        _svc.Evaluate([Reading("cpu", 75)]); // fire
        WaitForCallback(signal);
        activated.Clear();

        _svc.Evaluate([Reading("cpu", 60)]); // clear
        // No callback expected — wait briefly to confirm no activation
        Assert.False(signal.Wait(100), "Unexpected profile callback");

        Assert.DoesNotContain("default", activated);
    }

    [Fact]
    public void ProfileSwitch_NoRevertWhenNoPreAlertProfile()
    {
        var (activated, signal, fn) = MakeSignalingCallback();
        _svc.SetActivateProfileFn(fn);

        AddActionRule(70, "perf", revertAfterClear: true);

        _svc.Evaluate([Reading("cpu", 75)]); // fire
        WaitForCallback(signal);
        activated.Clear();

        _svc.Evaluate([Reading("cpu", 60)]); // clear
        // No callback expected
        Assert.False(signal.Wait(100), "Unexpected profile callback");

        Assert.Empty(activated);
    }

    [Fact]
    public void ProfileSwitch_MixedRevertAfterClear_NoRevertWins()
    {
        var (activated, signal, fn) = MakeSignalingCallback();
        _svc.SetActivateProfileFn(fn);
        _svc.SetPreAlertProfile("default");

        AddActionRule(70,  "profile_A", revertAfterClear: true,  sensorId: "cpu");
        AddActionRule(80,  "profile_B", revertAfterClear: false, sensorId: "cpu");

        _svc.Evaluate([Reading("cpu", 85)]);
        WaitForCallback(signal); // profile_A fires first
        WaitForCallback(signal); // profile_B fires second
        activated.Clear();

        _svc.Evaluate([Reading("cpu", 60)]);
        // No revert expected
        Assert.False(signal.Wait(100), "Unexpected revert callback");

        Assert.DoesNotContain("default", activated);
    }

    // -----------------------------------------------------------------------
    // Rule management
    // -----------------------------------------------------------------------

    [Fact]
    public async Task AddRule_PersistsInDb()
    {
        var rule = AddAboveRule(70);
        Assert.NotNull(rule.RuleId);

        // Create a fresh AlertService from same DB — rule should reload
        var svc2 = new AlertService(_db);
        await svc2.InitializeAsync();
        var rules = svc2.GetRules();
        Assert.Single(rules);
        Assert.Equal(70.0, rules[0].Threshold);
        svc2.Dispose();
    }

    [Fact]
    public async Task DeleteRule_RemovesFromDb()
    {
        var rule = AddAboveRule(70);
        await _svc.DeleteRuleAsync(rule.RuleId);

        var svc2 = new AlertService(_db);
        await svc2.InitializeAsync();
        Assert.Empty(svc2.GetRules());
        svc2.Dispose();
    }

    [Fact]
    public async Task DeleteRule_ReturnsFalse_ForUnknownId()
    {
        var result = await _svc.DeleteRuleAsync("nonexistent-rule-id");
        Assert.False(result);
    }

    [Fact]
    public void Evaluate_MultiSensor_FiresIndependently()
    {
        AddAboveRule(70, "cpu");
        AddAboveRule(50, "gpu");

        var fired = _svc.Evaluate([
            Reading("cpu", 75),
            new SensorReading { Id = "gpu", SensorType = SensorTypeValues.GpuTemp, Value = 55, Unit = "C" },
        ]);

        Assert.Equal(2, fired.Count);
    }

    [Fact]
    public void Evaluate_IgnoresUnmonitoredSensors()
    {
        AddAboveRule(70, "cpu");

        var fired = _svc.Evaluate([
            Reading("unmonitored", 999),
        ]);

        Assert.Empty(fired);
    }

    [Fact]
    public void InjectEvent_AppearsInEvents_ButDoesNotTriggerActions()
    {
        var (activated, signal, fn) = MakeSignalingCallback();
        _svc.SetActivateProfileFn(fn);
        _svc.SetPreAlertProfile("default");

        _svc.InjectEvent("synthetic", "hdd_temp", "HDD", 55, 50, "above", "SMART trend warning");

        var events = _svc.GetEvents(100);
        Assert.Single(events);
        Assert.Equal("synthetic", events[0].RuleId);

        // Should NOT trigger profile callback
        Assert.False(signal.Wait(100), "InjectEvent should not trigger actions");
    }

    [Fact]
    public void DrainInjectedEvents_ReturnsAndClears()
    {
        _svc.InjectEvent("s1", "x", "X", 2, 1, "above", "test1");
        _svc.InjectEvent("s2", "y", "Y", 1, 2, "below", "test2");

        var drained = _svc.DrainInjectedEvents();
        Assert.Equal(2, drained.Count);

        // Second drain should be empty
        Assert.Empty(_svc.DrainInjectedEvents());
    }

    [Fact]
    public void HasActiveProfileSwitch_ReflectsState()
    {
        Assert.False(_svc.HasActiveProfileSwitch);
        _svc.SetPreAlertProfile("p1");
        Assert.True(_svc.HasActiveProfileSwitch);
    }

    // -----------------------------------------------------------------------
    // Concurrency
    // -----------------------------------------------------------------------

    [Fact]
    public void ConcurrentEvaluateAndMutate_NoDeadlockOrLostUpdates()
    {
        // Seed 5 rules
        for (int i = 0; i < 5; i++)
            AddAboveRule(50 + i, $"sensor_{i}");

        var readings = new List<SensorReading>();
        for (int i = 0; i < 5; i++)
            readings.Add(Reading($"sensor_{i}", 80));

        var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5));
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        // Thread 1: rapid Evaluate calls (hot path)
        var evalTask = Task.Run(() =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try { _svc.Evaluate(readings); }
                catch (Exception ex) { exceptions.Add(ex); }
            }
        });

        // Thread 2: concurrent reads
        var readTask = Task.Run(() =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    _ = _svc.GetRules();
                    _ = _svc.GetEvents();
                    _ = _svc.GetActiveEvents();
                }
                catch (Exception ex) { exceptions.Add(ex); }
            }
        });

        // Thread 3: add and delete rules
        var mutateTask = Task.Run(async () =>
        {
            int n = 0;
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    var rule = await _svc.AddRuleAsync(new CreateAlertRuleRequest
                    {
                        SensorId = $"dynamic_{n++}",
                        SensorName = "Dynamic",
                        Threshold = 99,
                        Condition = "above",
                    });
                    await _svc.DeleteRuleAsync(rule.RuleId);
                }
                catch (Exception ex) { exceptions.Add(ex); }
            }
        });

        // Let all three threads hammer the service for 2 seconds
        Task.Delay(2000).Wait();
        cts.Cancel();

        // Must not deadlock (timeout is 5s) and must not throw
        Assert.True(Task.WaitAll([evalTask, readTask, mutateTask], TimeSpan.FromSeconds(5)),
            "Concurrent tasks did not complete — possible deadlock");
        Assert.Empty(exceptions);

        // Verify structural integrity: rules and events are still accessible
        var rules = _svc.GetRules();
        Assert.True(rules.Count >= 5); // original 5 still present
        var events = _svc.GetEvents();
        Assert.NotNull(events);
    }
}
