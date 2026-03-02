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

    private volatile bool _curvesSynced;

    // Safe mode state
    private volatile bool _released;      // user released fan control to BIOS
    private volatile bool _sensorPanic;   // sensor read failure → all fans full
    private volatile bool _tempPanic;     // temperature above panic threshold

    private const double PanicCpuTemp = 95.0;
    private const double PanicGpuTemp = 90.0;
    private const double PanicHysteresis = 5.0;

    public FanService(IHardwareBackend hw, SettingsStore store)
    {
        _hw    = hw;
        _store = store;
        // NOTE: GetFanIds() returns empty before Initialize() — fan IDs are
        // populated lazily on the first ApplyCurvesAsync tick (see SyncCurveSlots).
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
    // Safe mode — release / resume / panic
    // -----------------------------------------------------------------------

    /// <summary>Release all fans to BIOS/auto control. Suspends curve engine.</summary>
    public void ReleaseFanControl()
    {
        _released = true;
        foreach (var fanId in _hw.GetFanIds())
            _hw.SetFanAuto(fanId);
    }

    /// <summary>Resume software control after release.</summary>
    public bool Resume(out Profile? activeProfile)
    {
        var profiles = _store.LoadProfiles();
        activeProfile = profiles.FirstOrDefault(p => p.IsActive);
        if (activeProfile == null) return false;

        _released = false;
        // Re-apply curves from active profile
        _rwl.EnterWriteLock();
        try
        {
            foreach (var curve in activeProfile.Curves)
                _curves[curve.FanId] = curve;
        }
        finally { _rwl.ExitWriteLock(); }
        return true;
    }

    public object GetSafeModeStatus()
    {
        _rwl.EnterReadLock();
        Dictionary<string, double> speeds;
        int activeCurves;
        try
        {
            speeds = new Dictionary<string, double>(_lastApplied);
            activeCurves = _curves.Values.Count(c => c is { Enabled: true });
        }
        finally { _rwl.ExitReadLock(); }

        return new
        {
            safe_mode = new
            {
                released = _released,
                sensor_panic = _sensorPanic,
                temp_panic = _tempPanic,
                panic_cpu_temp = PanicCpuTemp,
                panic_gpu_temp = PanicGpuTemp,
            },
            curves_active = activeCurves,
            applied_speeds = speeds,
        };
    }

    /// <summary>Check panic thresholds. Called by SensorWorker each tick.</summary>
    public void CheckPanicThresholds(IReadOnlyList<SensorReading> readings)
    {
        // Sensor panic: no temp readings at all → force all fans to 100%
        var hasTempReadings = readings.Any(r =>
            r.SensorType is SensorTypeValues.CpuTemp or SensorTypeValues.GpuTemp);
        if (!hasTempReadings && _curvesSynced)
        {
            if (!_sensorPanic)
            {
                _sensorPanic = true;
                foreach (var fanId in _hw.GetFanIds())
                    _hw.SetFanSpeed(fanId, 100);
            }
            return;
        }
        _sensorPanic = false;

        var cpuMax = readings
            .Where(r => r.SensorType == SensorTypeValues.CpuTemp)
            .Select(r => r.Value)
            .DefaultIfEmpty(0)
            .Max();
        var gpuMax = readings
            .Where(r => r.SensorType == SensorTypeValues.GpuTemp)
            .Select(r => r.Value)
            .DefaultIfEmpty(0)
            .Max();

        bool shouldPanic = cpuMax >= PanicCpuTemp || gpuMax >= PanicGpuTemp;
        bool shouldClear = cpuMax < (PanicCpuTemp - PanicHysteresis)
                        && gpuMax < (PanicGpuTemp - PanicHysteresis);

        if (shouldPanic && !_tempPanic)
        {
            _tempPanic = true;
            // Force all fans to 100%
            foreach (var fanId in _hw.GetFanIds())
                _hw.SetFanSpeed(fanId, 100);
        }
        else if (shouldClear && _tempPanic)
        {
            _tempPanic = false;
        }
    }

    public bool IsReleased => _released;

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
        // Lazy-sync: populate curve slots once the hardware backend has fan IDs
        if (!_curvesSynced)
            SyncCurveSlots();

        // Check panic thresholds every tick
        CheckPanicThresholds(readings);

        // Skip curve application when released to BIOS or in panic mode
        if (_released || _tempPanic)
            return Task.CompletedTask;

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

        // Apply speeds and record under write lock to avoid concurrent dictionary mutation
        if (toApply.Count > 0)
        {
            foreach (var (fanId, speed) in toApply)
                _hw.SetFanSpeed(fanId, speed);

            _rwl.EnterWriteLock();
            try
            {
                foreach (var (fanId, speed) in toApply)
                    _lastApplied[fanId] = speed;
            }
            finally { _rwl.ExitWriteLock(); }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Populate curve slots from the hardware backend's fan IDs.
    /// Called lazily because GetFanIds() is empty before Initialize().
    /// </summary>
    private void SyncCurveSlots()
    {
        var fanIds = _hw.GetFanIds();
        if (fanIds.Count == 0) return; // not ready yet

        _rwl.EnterWriteLock();
        try
        {
            foreach (var id in fanIds)
            {
                if (!_curves.ContainsKey(id))
                    _curves[id] = null;
            }
            _curvesSynced = true;
        }
        finally { _rwl.ExitWriteLock(); }
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
