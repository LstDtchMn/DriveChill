using DriveChill.Models;

namespace DriveChill.Services;

/// <summary>
/// Threshold-based alerting with hysteresis.
/// A rule fires once when condition becomes true, and clears once the condition becomes false.
/// Thread-safe: all state changes are serialised through a lock.
/// </summary>
public sealed class AlertService
{
    private readonly SettingsStore _store;
    private readonly object _lock = new();

    private readonly List<AlertRule>  _rules  = [];
    private readonly List<AlertEvent> _events = [];
    // ruleId → currently active (fired but not cleared)
    private readonly HashSet<string> _active = [];

    public AlertService(SettingsStore store)
    {
        _store = store;
        _rules.AddRange(store.LoadAlerts());
    }

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
        };
        lock (_lock) { _rules.Add(rule); _store.SaveAlerts(_rules); }
        return rule;
    }

    public bool DeleteRule(string ruleId)
    {
        lock (_lock)
        {
            var rule = _rules.FirstOrDefault(r => r.RuleId == ruleId);
            if (rule == null) return false;
            _rules.Remove(rule);
            _active.Remove(ruleId);
            _store.SaveAlerts(_rules);
            return true;
        }
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

    // -----------------------------------------------------------------------
    // Evaluation — called by SensorWorker on every tick
    // -----------------------------------------------------------------------

    public void Evaluate(IReadOnlyList<SensorReading> readings)
    {
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
                }
            }
        }
    }
}
