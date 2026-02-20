using DriveChill.Hardware;
using DriveChill.Models;

namespace DriveChill.Services;

/// <summary>
/// Manages manual fan speed overrides and fan-curve definitions.
/// Thread-safe via a ReaderWriterLockSlim (curves are rarely written, often read).
/// </summary>
public sealed class FanService
{
    private readonly IHardwareBackend _hw;
    private readonly SettingsStore    _store;

    private readonly ReaderWriterLockSlim _rwl = new();
    // fanId → curve (null means auto/manual)
    private readonly Dictionary<string, FanCurve?> _curves = new();
    // fanId → last applied speed percent (used to avoid redundant hardware calls)
    private readonly Dictionary<string, double> _lastApplied = new();

    // Test locking — prevent curve engine from overriding speeds during a benchmark sweep
    private readonly HashSet<string> _testLocked   = [];
    private readonly object          _testLockObj   = new();

    public FanService(IHardwareBackend hw, SettingsStore store)
    {
        _hw    = hw;
        _store = store;

        // Initialise curve slots for all known fans (null = no curve = auto)
        foreach (var id in hw.GetFanIds())
            _curves[id] = null;
    }

    // -----------------------------------------------------------------------
    // Fan status
    // -----------------------------------------------------------------------

    public IReadOnlyList<FanStatus> GetAll(SensorSnapshot snapshot)
    {
        var result = new List<FanStatus>();
        _rwl.EnterReadLock();
        try
        {
            foreach (var fanId in _hw.GetFanIds())
            {
                _curves.TryGetValue(fanId, out var curve);
                _lastApplied.TryGetValue(fanId, out var pct);

                // Try to find current speed from sensor readings
                var rpmReading = snapshot.Readings.FirstOrDefault(r =>
                    r.Id == fanId + "_rpm" || r.Id.StartsWith(fanId));
                var pctReading = snapshot.Readings.FirstOrDefault(r =>
                    r.SensorType == SensorTypeValues.FanPercent &&
                    (r.Id == fanId + "_pct" || r.Id.StartsWith(fanId)));

                result.Add(new FanStatus
                {
                    FanId        = fanId,
                    Name         = fanId.Replace("_", " ").ToUpperInvariant(),
                    SpeedPercent = pctReading?.Value ?? pct,
                    Rpm          = rpmReading?.Value,
                    Mode         = curve != null && curve.Enabled ? "curve" : "auto",
                    Curve        = curve,
                });
            }
        }
        finally { _rwl.ExitReadLock(); }
        return result;
    }

    // -----------------------------------------------------------------------
    // Test locking (called by FanTestService)
    // -----------------------------------------------------------------------

    public void LockForTest(string fanId)
    {
        lock (_testLockObj) _testLocked.Add(fanId);
    }

    public void UnlockFromTest(string fanId)
    {
        lock (_testLockObj) _testLocked.Remove(fanId);
    }

    // -----------------------------------------------------------------------
    // Manual control
    // -----------------------------------------------------------------------

    public bool SetSpeed(string fanId, double speedPercent)
    {
        speedPercent = Math.Clamp(speedPercent, 0, 100);
        _rwl.EnterWriteLock();
        try { _curves[fanId] = null; } // disable any active curve
        finally { _rwl.ExitWriteLock(); }
        return _hw.SetFanSpeed(fanId, speedPercent);
    }

    public bool SetAuto(string fanId)
    {
        _rwl.EnterWriteLock();
        try { _curves[fanId] = null; }
        finally { _rwl.ExitWriteLock(); }
        return _hw.SetFanAuto(fanId);
    }

    // -----------------------------------------------------------------------
    // Curves
    // -----------------------------------------------------------------------

    public IReadOnlyList<FanCurve> GetCurves()
    {
        _rwl.EnterReadLock();
        try { return _curves.Values.Where(c => c != null).Select(c => c!).ToList(); }
        finally { _rwl.ExitReadLock(); }
    }

    public void SetCurve(FanCurve curve)
    {
        _rwl.EnterWriteLock();
        try { _curves[curve.FanId] = curve; }
        finally { _rwl.ExitWriteLock(); }
        _store.SaveCurves(GetCurves());
    }

    public void DeleteCurve(string fanId)
    {
        _rwl.EnterWriteLock();
        try { _curves[fanId] = null; }
        finally { _rwl.ExitWriteLock(); }
        _store.SaveCurves(GetCurves());
    }

    // -----------------------------------------------------------------------
    // Called by SensorWorker on every tick
    // -----------------------------------------------------------------------

    public Task ApplyCurvesAsync(IReadOnlyList<SensorReading> readings,
        CancellationToken ct = default)
    {
        _rwl.EnterReadLock();
        List<(string fanId, double speed)> toApply = [];
        try
        {
            foreach (var (fanId, curve) in _curves)
            {
                // Skip fans locked by FanTestService — benchmark sweep controls them directly
                lock (_testLockObj)
                    if (_testLocked.Contains(fanId)) continue;

                if (curve == null || !curve.Enabled) continue;
                var sensor = readings.FirstOrDefault(r => r.Id == curve.SensorId);
                if (sensor == null) continue;
                var target = Interpolate(curve.Points, sensor.Value);
                _lastApplied.TryGetValue(fanId, out var last);
                if (Math.Abs(target - last) >= 0.5) // only update on meaningful change
                    toApply.Add((fanId, target));
            }
        }
        finally { _rwl.ExitReadLock(); }

        foreach (var (fanId, speed) in toApply)
        {
            _hw.SetFanSpeed(fanId, speed);
            _lastApplied[fanId] = speed;
        }

        return Task.CompletedTask;
    }

    // -----------------------------------------------------------------------
    // Linear interpolation between curve control points
    // -----------------------------------------------------------------------

    private static double Interpolate(List<FanCurvePoint> points, double temp)
    {
        if (points.Count == 0) return 50;
        var sorted = points.OrderBy(p => p.Temp).ToList();
        if (temp <= sorted[0].Temp) return sorted[0].Speed;
        if (temp >= sorted[^1].Temp) return sorted[^1].Speed;

        for (int i = 1; i < sorted.Count; i++)
        {
            if (temp <= sorted[i].Temp)
            {
                var lo = sorted[i - 1];
                var hi = sorted[i];
                var t  = (temp - lo.Temp) / (hi.Temp - lo.Temp);
                return lo.Speed + t * (hi.Speed - lo.Speed);
            }
        }
        return sorted[^1].Speed;
    }
}
