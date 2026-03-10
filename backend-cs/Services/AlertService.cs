using DriveChill.Models;
using Microsoft.Extensions.Logging;

namespace DriveChill.Services;

/// <summary>
/// Threshold-based alerting with hysteresis.
/// A rule fires once when condition becomes true, and clears once the condition becomes false.
/// Thread-safe: all state changes are serialised through a lock.
/// </summary>
public sealed class AlertService
{
    private readonly SettingsStore _store;
    private readonly ILogger<AlertService> _logger;
    private readonly object _lock = new();

    private readonly List<AlertRule>  _rules  = [];
    private readonly List<AlertEvent> _events = [];
    // Events injected from outside Evaluate() (e.g. SmartTrendService) pending fan-out dispatch
    private readonly List<AlertEvent> _pendingInjected = [];
    // ruleId → currently active (fired but not cleared)
    private readonly HashSet<string> _active = [];

    // Profile switching state
    private Func<string, Task>? _activateProfileFn;
    private string? _preAlertProfileId;
    private readonly List<string> _actionFiredOrder = []; // rule IDs in firing order
    private bool _suppressRevert; // true if any fired rule had RevertAfterClear=false

    public AlertService(SettingsStore store, ILogger<AlertService>? logger = null)
    {
        _store = store;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<AlertService>.Instance;
        _rules.AddRange(store.LoadAlerts());
    }

    /// <summary>Set the callback used to activate a profile by ID.</summary>
    public void SetActivateProfileFn(Func<string, Task> fn) => _activateProfileFn = fn;

    /// <summary>True if an alert-triggered profile switch is currently active.</summary>
    public bool HasActiveProfileSwitch => _preAlertProfileId != null;

    /// <summary>Record the current profile ID so alert-triggered switches can revert.</summary>
    public void SetPreAlertProfile(string profileId) => _preAlertProfileId = profileId;

    // -----------------------------------------------------------------------
    // Rules CRUD
    // -----------------------------------------------------------------------

    public IReadOnlyList<AlertRule> GetRules()
    {
        lock (_lock) return [.. _rules];
    }

    public AlertRule AddRule(CreateAlertRuleRequest req)
    {
        var rule = new AlertRule
        {
            SensorId   = req.SensorId,
            SensorName = req.SensorName,
            Threshold  = req.Threshold,
            Condition  = req.Condition,
            Message    = req.Message,
            Action     = req.Action,
        };
        lock (_lock) { _rules.Add(rule); _store.SaveAlerts(_rules); }
        return rule;
    }

    public bool DeleteRule(string ruleId)
    {
        bool wasInOrder;
        bool deletedRuleWasNoRevert;
        lock (_lock)
        {
            var rule = _rules.FirstOrDefault(r => r.RuleId == ruleId);
            if (rule == null) return false;
            deletedRuleWasNoRevert = rule.Action?.RevertAfterClear == false;
            _rules.Remove(rule);
            _active.Remove(ruleId);
            wasInOrder = _actionFiredOrder.Remove(ruleId);
            if (wasInOrder)
            {
                // Recompute suppress from remaining active rules + preference of deleted rule.
                bool remainingSuppress = _actionFiredOrder.Any(rid =>
                {
                    var r = _rules.FirstOrDefault(x => x.RuleId == rid);
                    return r?.Action?.RevertAfterClear == false;
                });
                _suppressRevert = remainingSuppress || deletedRuleWasNoRevert;
            }
            _store.SaveAlerts(_rules);
        }
        if (wasInOrder) HandleActionReeval();
        return true;
    }

    // -----------------------------------------------------------------------
    // Events
    // -----------------------------------------------------------------------

    public IReadOnlyList<AlertEvent> GetEvents(int limit = 100)
    {
        lock (_lock) return _events.TakeLast(limit).ToList();
    }

    public IReadOnlyList<AlertEvent> GetActiveEvents()
    {
        lock (_lock) return _events.Where(e => _active.Contains(e.RuleId) && !e.Cleared).ToList();
    }

    public void ClearEvents()
    {
        lock (_lock) { _events.Clear(); _active.Clear(); _suppressRevert = false; }
    }

    /// <summary>
    /// Inject a synthetic event (e.g. from SmartTrendService) into the event log.
    /// Mirrors the Python pattern: deduplication is the caller's responsibility.
    /// Does not trigger profile-switching actions.
    /// </summary>
    public AlertEvent InjectEvent(string ruleId, string sensorId, string sensorName,
        double actualValue, double threshold, string condition, string message)
    {
        var ev = new AlertEvent
        {
            RuleId      = ruleId,
            SensorId    = sensorId,
            SensorName  = sensorName,
            ActualValue = actualValue,
            Threshold   = threshold,
            Condition   = condition,
            Message     = message,
        };
        lock (_lock)
        {
            _events.Add(ev);
            while (_events.Count > 500) _events.RemoveAt(0);
            _pendingInjected.Add(ev);
        }
        return ev;
    }

    /// <summary>
    /// Returns and clears all events that were injected via <see cref="InjectEvent"/> since the
    /// last drain. SensorWorker calls this after Evaluate() to include injected SMART-trend events
    /// in the same delivery fan-out as threshold-crossing events.
    /// </summary>
    public List<AlertEvent> DrainInjectedEvents()
    {
        lock (_lock)
        {
            var result = new List<AlertEvent>(_pendingInjected);
            _pendingInjected.Clear();
            return result;
        }
    }

    // -----------------------------------------------------------------------
    // Profile switching helpers
    // -----------------------------------------------------------------------

    private void HandleActionFire(AlertRule rule)
    {
        if (rule.Action == null || rule.Action.Type != "switch_profile") return;
        if (string.IsNullOrEmpty(rule.Action.ProfileId)) return;

        lock (_lock)
        {
            _actionFiredOrder.Remove(rule.RuleId);
            _actionFiredOrder.Add(rule.RuleId);
            // Recompute from the full active set rather than accumulating monotonically.
            _suppressRevert = _actionFiredOrder.Any(rid =>
            {
                var r = _rules.FirstOrDefault(x => x.RuleId == rid);
                return r?.Action?.RevertAfterClear == false;
            });
        }

        if (_activateProfileFn != null)
        {
            _logger.LogInformation("Alert rule {RuleId} firing profile switch to {ProfileId}",
                rule.RuleId, rule.Action.ProfileId);
            _ = Task.Run(() => _activateProfileFn(rule.Action.ProfileId));
        }
    }

    /// <summary>
    /// Processes a batch of simultaneously-clearing action rules.
    /// Computes suppress from the batch AND the remaining active rules, then re-evaluates once.
    /// Prevents a historical no-revert rule from silently suppressing a later revert-wanting rule.
    /// </summary>
    private void HandleBatchActionClear(List<AlertRule> clearedRules)
    {
        bool batchSuppress = clearedRules.Any(r => r.Action?.RevertAfterClear == false);
        lock (_lock)
        {
            foreach (var rule in clearedRules)
                _actionFiredOrder.Remove(rule.RuleId);
            // Recompute suppress from remaining active rules + what just cleared.
            bool remainingSuppress = _actionFiredOrder.Any(rid =>
            {
                var r = _rules.FirstOrDefault(x => x.RuleId == rid);
                return r?.Action?.RevertAfterClear == false;
            });
            _suppressRevert = remainingSuppress || batchSuppress;
        }
        HandleActionReeval();
    }

    private void HandleActionReeval()
    {
        // Find the most recently fired action rule that's still active
        lock (_lock)
        {
            for (int i = _actionFiredOrder.Count - 1; i >= 0; i--)
            {
                var rid = _actionFiredOrder[i];
                var rule = _rules.FirstOrDefault(r => r.RuleId == rid);
                if (rule?.Action is { Type: "switch_profile" } action
                    && !string.IsNullOrEmpty(action.ProfileId))
                {
                    if (_activateProfileFn != null)
                    {
                        _logger.LogInformation(
                            "Re-evaluating: switching to profile {ProfileId} (rule {RuleId} still active)",
                            action.ProfileId, rid);
                        _ = Task.Run(() => _activateProfileFn(action.ProfileId));
                    }
                    return;
                }
            }
        }

        // No action rules active — revert unless suppressed by revert_after_clear=false
        if (_preAlertProfileId != null)
        {
            if (_suppressRevert)
            {
                _logger.LogInformation(
                    "All alert actions cleared but revert suppressed by RevertAfterClear=false");
                _suppressRevert = false;
                _preAlertProfileId = null;
            }
            else if (_activateProfileFn != null)
            {
                var revertId = _preAlertProfileId;
                _logger.LogInformation("All alert actions cleared, reverting to profile {ProfileId}", revertId);
                _preAlertProfileId = null;
                _ = Task.Run(() => _activateProfileFn(revertId));
            }
        }
    }

    // -----------------------------------------------------------------------
    // Evaluation — called by SensorWorker on every tick
    // -----------------------------------------------------------------------

    public IReadOnlyList<AlertEvent> Evaluate(IReadOnlyList<SensorReading> readings)
    {
        var fired = new List<AlertEvent>();
        var clearedActionRules = new List<AlertRule>(); // full rule objects for batch suppress

        lock (_lock)
        {
            foreach (var rule in _rules)
            {
                if (!rule.Enabled) continue;
                var sensor = readings.FirstOrDefault(r => r.Id == rule.SensorId);
                if (sensor == null) continue;

                bool conditionMet = rule.Condition == "above"
                    ? sensor.Value > rule.Threshold
                    : sensor.Value < rule.Threshold;

                bool isActive = _active.Contains(rule.RuleId);

                if (conditionMet && !isActive)
                {
                    // Fire
                    var ev = new AlertEvent
                    {
                        RuleId      = rule.RuleId,
                        SensorId    = rule.SensorId,
                        SensorName  = rule.SensorName,
                        ActualValue = sensor.Value,
                        Threshold   = rule.Threshold,
                        Condition   = rule.Condition,
                        Message     = rule.Message,
                    };
                    _events.Add(ev);
                    fired.Add(ev);
                    _active.Add(rule.RuleId);

                    // Trim history to 500 events
                    while (_events.Count > 500) _events.RemoveAt(0);
                }
                else if (!conditionMet && isActive)
                {
                    // Clear — mark the last event for this rule as cleared
                    var last = _events.LastOrDefault(e => e.RuleId == rule.RuleId);
                    if (last != null) last.Cleared = true;
                    _active.Remove(rule.RuleId);

                    if (rule.Action is { Type: "switch_profile" })
                        clearedActionRules.Add(rule);
                }
            }
        }

        // Handle profile switching outside the lock
        foreach (var ev in fired)
        {
            var rule = _rules.FirstOrDefault(r => r.RuleId == ev.RuleId);
            if (rule != null) HandleActionFire(rule);
        }
        // Process all simultaneously-clearing action rules as a single batch so that
        // _suppressRevert is computed from the whole clearing set, not rule-by-rule.
        if (clearedActionRules.Count > 0)
            HandleBatchActionClear(clearedActionRules);

        return fired;
    }
}
