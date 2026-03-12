using DriveChill.Models;
using Microsoft.Extensions.Logging;

namespace DriveChill.Services;

/// <summary>
/// Threshold-based alerting with hysteresis.
/// A rule fires once when condition becomes true, and clears once the condition becomes false.
/// Thread-safe: reads use a shared reader lock, mutations use an exclusive writer lock.
/// </summary>
public sealed class AlertService : IDisposable
{
    private readonly DbService _db;
    private readonly ILogger<AlertService> _logger;
    private readonly ReaderWriterLockSlim _rwLock = new();

    private readonly List<AlertRule>  _rules  = [];
    private readonly List<AlertEvent> _events = [];
    // Events injected from outside Evaluate() (e.g. SmartTrendService) pending fan-out dispatch
    private readonly List<AlertEvent> _pendingInjected = [];
    // ruleId -> currently active (fired but not cleared)
    private readonly HashSet<string> _active = [];

    // Profile switching state
    private Func<string, Task>? _activateProfileFn;
    private string? _preAlertProfileId;
    private readonly List<string> _actionFiredOrder = []; // rule IDs in firing order
    private volatile bool _suppressRevert; // true if any fired rule had RevertAfterClear=false

    public AlertService(DbService db, ILogger<AlertService>? logger = null)
    {
        _db = db;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<AlertService>.Instance;
    }

    public void Dispose() => _rwLock.Dispose();

    /// <summary>Load rules from DB. Must be called after construction.</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var rules = await _db.ListAlertRulesAsync(ct);
        _rwLock.EnterWriteLock();
        try
        {
            _rules.Clear();
            _rules.AddRange(rules);
        }
        finally { _rwLock.ExitWriteLock(); }
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

    /// <summary>Reload rules from the DB (e.g. after config import).</summary>
    public async Task ReloadRulesAsync(CancellationToken ct = default)
    {
        var rules = await _db.ListAlertRulesAsync(ct);
        _rwLock.EnterWriteLock();
        try
        {
            _rules.Clear();
            _rules.AddRange(rules);
        }
        finally { _rwLock.ExitWriteLock(); }
    }

    public IReadOnlyList<AlertRule> GetRules()
    {
        _rwLock.EnterReadLock();
        try { return [.. _rules]; }
        finally { _rwLock.ExitReadLock(); }
    }

    public async Task<AlertRule> AddRuleAsync(CreateAlertRuleRequest req, CancellationToken ct = default)
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
        await _db.CreateAlertRuleAsync(rule, ct);
        _rwLock.EnterWriteLock();
        try { _rules.Add(rule); }
        finally { _rwLock.ExitWriteLock(); }
        return rule;
    }

    public async Task<bool> DeleteRuleAsync(string ruleId, CancellationToken ct = default)
    {
        bool wasInOrder;
        _rwLock.EnterWriteLock();
        try
        {
            var rule = _rules.FirstOrDefault(r => r.RuleId == ruleId);
            if (rule == null) return false;
            _rules.Remove(rule);
            _active.Remove(ruleId);
            wasInOrder = _actionFiredOrder.Remove(ruleId);
            if (wasInOrder)
            {
                bool remainingSuppress = _actionFiredOrder.Any(rid =>
                {
                    var r = _rules.FirstOrDefault(x => x.RuleId == rid);
                    return r?.Action?.RevertAfterClear == false;
                });
                _suppressRevert = remainingSuppress;
            }
        }
        finally { _rwLock.ExitWriteLock(); }
        await _db.DeleteAlertRuleAsync(ruleId, ct);
        if (wasInOrder) HandleActionReeval();
        return true;
    }

    // -----------------------------------------------------------------------
    // Events
    // -----------------------------------------------------------------------

    public IReadOnlyList<AlertEvent> GetEvents(int limit = 100)
    {
        _rwLock.EnterReadLock();
        try { return _events.TakeLast(limit).ToList(); }
        finally { _rwLock.ExitReadLock(); }
    }

    public IReadOnlyList<AlertEvent> GetActiveEvents()
    {
        _rwLock.EnterReadLock();
        try { return _events.Where(e => _active.Contains(e.RuleId) && !e.Cleared).ToList(); }
        finally { _rwLock.ExitReadLock(); }
    }

    public void ClearEvents()
    {
        _rwLock.EnterWriteLock();
        try { _events.Clear(); _active.Clear(); _suppressRevert = false; }
        finally { _rwLock.ExitWriteLock(); }
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
        _rwLock.EnterWriteLock();
        try
        {
            _events.Add(ev);
            while (_events.Count > 500) _events.RemoveAt(0);
            _pendingInjected.Add(ev);
        }
        finally { _rwLock.ExitWriteLock(); }
        return ev;
    }

    /// <summary>
    /// Returns and clears all events that were injected via <see cref="InjectEvent"/> since the
    /// last drain. SensorWorker calls this after Evaluate to include injected SMART-trend events
    /// in the same delivery fan-out as threshold-crossing events.
    /// </summary>
    public List<AlertEvent> DrainInjectedEvents()
    {
        _rwLock.EnterWriteLock();
        try
        {
            var result = new List<AlertEvent>(_pendingInjected);
            _pendingInjected.Clear();
            return result;
        }
        finally { _rwLock.ExitWriteLock(); }
    }

    // -----------------------------------------------------------------------
    // Profile switching helpers
    // -----------------------------------------------------------------------

    private void HandleActionFire(AlertRule rule)
    {
        if (rule.Action == null || rule.Action.Type != "switch_profile") return;
        if (string.IsNullOrEmpty(rule.Action.ProfileId)) return;

        _rwLock.EnterWriteLock();
        try
        {
            _actionFiredOrder.Remove(rule.RuleId);
            _actionFiredOrder.Add(rule.RuleId);
            _suppressRevert = _actionFiredOrder.Any(rid =>
            {
                var r = _rules.FirstOrDefault(x => x.RuleId == rid);
                return r?.Action?.RevertAfterClear == false;
            });
        }
        finally { _rwLock.ExitWriteLock(); }

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
    /// </summary>
    private void HandleBatchActionClear(List<AlertRule> clearedRules)
    {
        bool batchSuppress = clearedRules.Any(r => r.Action?.RevertAfterClear == false);
        _rwLock.EnterWriteLock();
        try
        {
            foreach (var rule in clearedRules)
                _actionFiredOrder.Remove(rule.RuleId);
            bool remainingSuppress = _actionFiredOrder.Any(rid =>
            {
                var r = _rules.FirstOrDefault(x => x.RuleId == rid);
                return r?.Action?.RevertAfterClear == false;
            });
            _suppressRevert = remainingSuppress || batchSuppress;
        }
        finally { _rwLock.ExitWriteLock(); }
        HandleActionReeval();
    }

    private void HandleActionReeval()
    {
        _rwLock.EnterWriteLock();
        try
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
        finally { _rwLock.ExitWriteLock(); }

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
    // Evaluation -- called by SensorWorker on every tick
    // -----------------------------------------------------------------------

    public IReadOnlyList<AlertEvent> Evaluate(IReadOnlyList<SensorReading> readings)
    {
        var fired = new List<AlertEvent>();
        var firedRules = new List<AlertRule>();
        var clearedActionRules = new List<AlertRule>();

        _rwLock.EnterWriteLock();
        try
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
                    firedRules.Add(rule);
                    _active.Add(rule.RuleId);

                    while (_events.Count > 500) _events.RemoveAt(0);
                }
                else if (!conditionMet && isActive)
                {
                    var last = _events.LastOrDefault(e => e.RuleId == rule.RuleId);
                    if (last != null) last.Cleared = true;
                    _active.Remove(rule.RuleId);

                    if (rule.Action is { Type: "switch_profile" })
                        clearedActionRules.Add(rule);
                }
            }
        }
        finally { _rwLock.ExitWriteLock(); }

        foreach (var rule in firedRules)
        {
            HandleActionFire(rule);
        }
        if (clearedActionRules.Count > 0)
            HandleBatchActionClear(clearedActionRules);

        return fired;
    }
}
