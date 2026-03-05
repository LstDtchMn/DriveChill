using System.Collections.Concurrent;
using DriveChill.Models;

namespace DriveChill.Services;

/// <summary>
/// Manages temperature targets and evaluates proportional/PID fan speeds.
/// Thread-safe via ReaderWriterLockSlim.
/// </summary>
public sealed class TemperatureTargetService
{
    private readonly DbService _db;
    private readonly ReaderWriterLockSlim _rwl = new();
    private List<TemperatureTarget> _targets = [];

    private sealed class PidState
    {
        public double  Integral  { get; set; }
        public double? PrevError { get; set; }
        public long    PrevTick  { get; set; } = -1;
    }

    private readonly ConcurrentDictionary<string, PidState> _pidStates = new();

    public TemperatureTargetService(DbService db)
    {
        _db = db;
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var targets = await _db.ListTemperatureTargetsAsync(ct);
        _rwl.EnterWriteLock();
        try { _targets = targets; }
        finally { _rwl.ExitWriteLock(); }
    }

    /// <summary>
    /// Evaluate all enabled targets and return {fanId: requiredSpeed}.
    /// Called from the sync fan-control loop.
    /// </summary>
    public Dictionary<string, double> Evaluate(IReadOnlyDictionary<string, double> sensorMap)
    {
        var result = new Dictionary<string, double>();
        var nowTick = System.Diagnostics.Stopwatch.GetTimestamp();

        _rwl.EnterReadLock();
        List<TemperatureTarget> snapshot;
        try { snapshot = new List<TemperatureTarget>(_targets); }
        finally { _rwl.ExitReadLock(); }

        foreach (var t in snapshot)
        {
            if (!t.Enabled) continue;
            if (!sensorMap.TryGetValue(t.SensorId, out var temp)) continue;

            double speed;
            if (t.PidMode)
            {
                var state = _pidStates.GetOrAdd(t.Id, _ => new PidState());
                speed = ComputePidSpeed(temp, t.TargetTempC, t.PidKp, t.PidKi, t.PidKd,
                    t.MinFanSpeed, state, nowTick);
            }
            else
            {
                speed = ComputeProportionalSpeed(temp, t.TargetTempC, t.ToleranceC, t.MinFanSpeed);
            }

            foreach (var fanId in t.FanIds)
            {
                if (!result.TryGetValue(fanId, out var existing) || speed > existing)
                    result[fanId] = speed;
            }
        }

        return result;
    }

    public IReadOnlyList<TemperatureTarget> Targets
    {
        get
        {
            _rwl.EnterReadLock();
            try { return new List<TemperatureTarget>(_targets); }
            finally { _rwl.ExitReadLock(); }
        }
    }

    public async Task<TemperatureTarget> AddAsync(TemperatureTarget target, CancellationToken ct = default)
    {
        var created = await _db.CreateTemperatureTargetAsync(target, ct);
        _rwl.EnterWriteLock();
        try { _targets.Add(created); }
        finally { _rwl.ExitWriteLock(); }
        return created;
    }

    public async Task<TemperatureTarget?> UpdateAsync(string targetId,
        string name, string? driveId, string sensorId, string[] fanIds,
        double targetTempC, double toleranceC, double minFanSpeed,
        bool pidMode, double pidKp, double pidKi, double pidKd,
        CancellationToken ct = default)
    {
        var updated = await _db.UpdateTemperatureTargetAsync(
            targetId, name, driveId, sensorId, fanIds, targetTempC, toleranceC, minFanSpeed,
            pidMode, pidKp, pidKi, pidKd, ct);
        if (updated is null) return null;

        // Reset PID state whenever parameters change
        _pidStates.TryRemove(targetId, out _);

        _rwl.EnterWriteLock();
        try
        {
            for (int i = 0; i < _targets.Count; i++)
            {
                if (_targets[i].Id == targetId)
                {
                    _targets[i] = updated;
                    break;
                }
            }
        }
        finally { _rwl.ExitWriteLock(); }
        return updated;
    }

    public async Task<bool> RemoveAsync(string targetId, CancellationToken ct = default)
    {
        var deleted = await _db.DeleteTemperatureTargetAsync(targetId, ct);
        if (!deleted) return false;

        _pidStates.TryRemove(targetId, out _);
        _rwl.EnterWriteLock();
        try { _targets.RemoveAll(t => t.Id == targetId); }
        finally { _rwl.ExitWriteLock(); }
        return true;
    }

    public async Task<TemperatureTarget?> SetEnabledAsync(string targetId, bool enabled,
        CancellationToken ct = default)
    {
        if (!enabled) _pidStates.TryRemove(targetId, out _);
        var updated = await _db.SetTemperatureTargetEnabledAsync(targetId, enabled, ct);
        if (updated is null) return null;

        _rwl.EnterWriteLock();
        try
        {
            for (int i = 0; i < _targets.Count; i++)
            {
                if (_targets[i].Id == targetId)
                {
                    _targets[i] = updated;
                    break;
                }
            }
        }
        finally { _rwl.ExitWriteLock(); }
        return updated;
    }

    /// <summary>Compute fan speed using proportional control within the tolerance band.</summary>
    public static double ComputeProportionalSpeed(
        double temp, double targetTempC, double toleranceC, double minFanSpeed)
    {
        if (toleranceC <= 0.0)
            return 100.0;

        var low  = targetTempC - toleranceC;
        var high = targetTempC + toleranceC;

        if (temp <= low)  return minFanSpeed;
        if (temp >= high) return 100.0;

        var t = (temp - low) / (2.0 * toleranceC);
        return minFanSpeed + t * (100.0 - minFanSpeed);
    }

    /// <summary>
    /// Compute fan speed using a PID controller.
    /// error = temp − target (positive → too hot → raise fan speed).
    /// </summary>
    private static double ComputePidSpeed(
        double temp, double targetTempC,
        double kp, double ki, double kd,
        double minFanSpeed,
        PidState state, long nowTick)
    {
        var freq = (double)System.Diagnostics.Stopwatch.Frequency;
        var error = temp - targetTempC;
        var dt    = state.PrevTick >= 0
            ? (nowTick - state.PrevTick) / freq
            : 0.0;

        if (dt > 0.0)
            state.Integral = Math.Clamp(state.Integral + error * dt, -200.0, 200.0);
        else
            state.Integral = 0.0;

        var derivative = (state.PrevError.HasValue && dt > 0.0)
            ? (error - state.PrevError.Value) / dt
            : 0.0;

        state.PrevError = error;
        state.PrevTick  = nowTick;

        var output = kp * error + ki * state.Integral + kd * derivative;
        var speed  = minFanSpeed + Math.Clamp(output, 0.0, 100.0 - minFanSpeed);
        return Math.Clamp(speed, minFanSpeed, 100.0);
    }
}
