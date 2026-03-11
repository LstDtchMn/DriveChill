using System.Diagnostics;
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
    private readonly TemperatureTargetService? _tempTargetSvc;
    private readonly VirtualSensorService? _virtualSensorSvc;

    private readonly ReaderWriterLockSlim _rwl = new();
    // fanId → curve (null means auto/manual)
    private readonly Dictionary<string, FanCurve?> _curves = new();
    // fanId → last applied speed percent (used to avoid redundant hardware calls)
    private readonly Dictionary<string, double> _lastApplied = new();
    // fanId → (minSpeedPct, zeroRpmCapable) — loaded from DB at startup
    private Dictionary<string, FanSettingsModel> _fanSettings = new();

    // Test locking — prevent curve engine from overriding speeds during a benchmark sweep
    private readonly HashSet<string> _testLocked   = [];
    private readonly object          _testLockObj   = new();

    private volatile bool _curvesSynced;

    // Hysteresis state keyed by (fanId, sensorId)
    private readonly Dictionary<(string fanId, string sensorId), HysteresisState> _hyst = new();

    // Ramp-rate limiting state
    private readonly Dictionary<string, double> _rampState = new();
    private readonly Stopwatch _rampStopwatch = Stopwatch.StartNew();
    private long _lastTickMs;

    // Safe mode state
    private volatile bool _released;      // user released fan control to BIOS
    private volatile bool _sensorPanic;   // sensor read failure → all fans full
    private volatile bool _tempPanic;     // temperature above panic threshold

    /// <summary>True if the system is in any panic mode (sensor failure or temperature).</summary>
    public bool IsInPanic => _sensorPanic || _tempPanic;

    // Startup safety: run fans at a safe fixed speed until curves are loaded
    // or the safety window expires (15 seconds).
    private volatile bool _startupSafetyActive = true;
    private const double StartupSafetySpeed = 50.0;    // percent
    private const double StartupSafetyDurationSec = 15.0;
    private readonly Stopwatch _startupStopwatch = Stopwatch.StartNew();

    // Control transparency (B3): per-fan source of the last applied speed.
    // Sources: "startup_safety" | "panic_sensor" | "panic_temp" | "released"
    //          | "profile" | "temperature_target" | "manual"
    private Dictionary<string, string> _controlSources = new();

    private readonly double PanicCpuTemp;
    private readonly double PanicGpuTemp;
    private readonly double PanicHysteresis;

    public FanService(IHardwareBackend hw, SettingsStore store, AppSettings? settings = null,
        TemperatureTargetService? tempTargetSvc = null,
        VirtualSensorService? virtualSensorSvc = null)
    {
        _hw    = hw;
        _store = store;
        _tempTargetSvc = tempTargetSvc;
        _virtualSensorSvc = virtualSensorSvc;
        settings ??= new AppSettings();
        PanicCpuTemp    = settings.PanicCpuTemp;
        PanicGpuTemp    = settings.PanicGpuTemp;
        PanicHysteresis = settings.PanicHysteresis;
        _lastTickMs = _rampStopwatch.ElapsedMilliseconds;
        // NOTE: GetFanIds() returns empty before Initialize() — fan IDs are
        // populated lazily on the first ApplyCurvesAsync tick (see SyncCurveSlots).
    }

    // -----------------------------------------------------------------------
    // Fan settings (min speed floor, zero-RPM capability)
    // -----------------------------------------------------------------------

    /// <summary>Load per-fan settings from DB on startup.</summary>
    public async Task LoadFanSettingsAsync(DbService db, CancellationToken ct = default)
    {
        _fanSettings = await db.GetAllFanSettingsAsync(ct);
    }

    /// <summary>Update cached fan settings (caller persists to DB).</summary>
    public void UpdateFanSettings(string fanId, double minSpeedPct, bool zeroRpmCapable)
    {
        _rwl.EnterWriteLock();
        try
        {
            _fanSettings[fanId] = new FanSettingsModel
            {
                FanId          = fanId,
                MinSpeedPct    = minSpeedPct,
                ZeroRpmCapable = zeroRpmCapable,
            };
        }
        finally { _rwl.ExitWriteLock(); }
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
        ExitStartupSafety();
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

        Dictionary<string, string> sources;
        lock (_controlSources) sources = new Dictionary<string, string>(_controlSources);

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
            control_sources = sources,
            startup_safety_active = _startupSafetyActive,
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
                _rwl.EnterWriteLock();
                try
                {
                    foreach (var fanId in _hw.GetFanIds())
                    {
                        _hw.SetFanSpeed(fanId, 100);
                        _lastApplied[fanId] = 100;
                    }
                }
                finally { _rwl.ExitWriteLock(); }
                lock (_controlSources)
                {
                    _controlSources.Clear();
                    foreach (var fanId in _hw.GetFanIds())
                        _controlSources[fanId] = "panic_sensor";
                }
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
            _rwl.EnterWriteLock();
            try
            {
                foreach (var fanId in _hw.GetFanIds())
                {
                    _hw.SetFanSpeed(fanId, 100);
                    _lastApplied[fanId] = 100;
                }
            }
            finally { _rwl.ExitWriteLock(); }
            lock (_controlSources)
            {
                _controlSources.Clear();
                foreach (var fanId in _hw.GetFanIds())
                    _controlSources[fanId] = "panic_temp";
            }
        }
        else if (shouldClear && _tempPanic)
        {
            _tempPanic = false;
        }
    }

    public bool IsReleased => _released;
    public bool IsStartupSafetyActive => _startupSafetyActive;

    /// <summary>Per-fan source of the last applied speed (B3 control transparency).</summary>
    public IReadOnlyDictionary<string, string> ControlSources
    {
        get { lock (_controlSources) return new Dictionary<string, string>(_controlSources); }
    }

    /// <summary>Exit startup safety mode (curves are now loaded).</summary>
    private void ExitStartupSafety()
    {
        if (_startupSafetyActive)
        {
            _startupSafetyActive = false;
            Debug.WriteLine("Exiting startup safety mode — normal fan control active");
        }
    }

    // -----------------------------------------------------------------------
    // Manual control
    // -----------------------------------------------------------------------

    public bool SetSpeed(string fanId, double speedPercent)
    {
        speedPercent = Math.Clamp(speedPercent, 0, 100);
        _rwl.EnterWriteLock();
        try
        {
            _curves[fanId] = null; // disable any active curve
            _lastApplied[fanId] = speedPercent;
        }
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
        ExitStartupSafety();
        _rwl.EnterWriteLock();
        try
        {
            // Clear hysteresis for affected fan so the new curve takes effect immediately
            if (_curves.TryGetValue(curve.FanId, out var old) && old != null)
                _hyst.Remove((old.FanId, old.SensorId));
            _hyst.Remove((curve.FanId, curve.SensorId));
            _curves[curve.FanId] = curve;
        }
        finally { _rwl.ExitWriteLock(); }
        _store.SaveCurves(GetCurves());
    }

    /// <summary>
    /// Replace ALL active curves atomically (used when activating a profile).
    /// Clears orphaned curves from the previously active profile before applying
    /// the new set, so stale curves never persist across profile switches.
    /// </summary>
    public void SetCurves(IEnumerable<FanCurve> curves)
    {
        ExitStartupSafety();
        _rwl.EnterWriteLock();
        try
        {
            foreach (var key in _curves.Keys.ToList())
                _curves[key] = null;
            _hyst.Clear(); // Clear all hysteresis state on full curve replacement
            foreach (var curve in curves)
                _curves[curve.FanId] = curve;
        }
        finally { _rwl.ExitWriteLock(); }
        _store.SaveCurves(GetCurves());
    }

    public void DeleteCurve(string fanId)
    {
        _rwl.EnterWriteLock();
        try
        {
            if (_curves.TryGetValue(fanId, out var old) && old != null)
                _hyst.Remove((old.FanId, old.SensorId));
            _curves[fanId] = null;
        }
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

        // Skip curve application when released to BIOS or in panic mode.
        // Panic sources are set by CheckPanicThresholds; only clear on release.
        if (_released || _tempPanic || _sensorPanic)
        {
            if (_released)
                lock (_controlSources) _controlSources.Clear();
            return Task.CompletedTask;
        }

        // Startup safety: hold fans at a safe fixed speed until curves are
        // loaded or the safety window expires.
        if (_startupSafetyActive)
        {
            if (_startupStopwatch.Elapsed.TotalSeconds >= StartupSafetyDurationSec)
                ExitStartupSafety();
            else
            {
                var startupFanIds = _hw.GetFanIds();
                foreach (var fanId in startupFanIds)
                    _hw.SetFanSpeed(fanId, StartupSafetySpeed);
                lock (_controlSources)
                {
                    _controlSources.Clear();
                    foreach (var fanId in startupFanIds)
                        _controlSources[fanId] = "startup_safety";
                }
                return Task.CompletedTask;
            }
        }

        // Build a sensor value lookup for composite temp resolution
        var sensorValues = new Dictionary<string, double>();
        foreach (var r in readings)
            sensorValues[r.Id] = r.Value;

        // Resolve virtual sensors so curves/targets can reference them
        if (_virtualSensorSvc != null)
            sensorValues = _virtualSensorSvc.ResolveAll(sensorValues);

        // 1. Build curve-based speeds
        var curveSpeeds = new Dictionary<string, double>();
        _rwl.EnterWriteLock(); // write lock needed for hysteresis state mutation
        try
        {
            foreach (var (fanId, curve) in _curves)
            {
                // Skip fans locked by FanTestService — benchmark sweep controls them directly
                lock (_testLockObj)
                    if (_testLocked.Contains(fanId)) continue;

                if (curve == null || !curve.Enabled) continue;

                // Resolve composite temperature (MAX of sensor_ids), fall back to single sensor_id
                double? temp = ResolveCompositeTemp(curve, sensorValues);
                if (temp is null) continue;

                var rawSpeed = Interpolate(curve.Points, temp.Value);

                // Apply hysteresis to prevent oscillation
                var speed = ApplyHysteresis(fanId, curve.SensorId, temp.Value, rawSpeed);

                if (curveSpeeds.TryGetValue(fanId, out var existing))
                    curveSpeeds[fanId] = Math.Max(existing, speed);
                else
                    curveSpeeds[fanId] = speed;
            }
        }
        finally { _rwl.ExitWriteLock(); }

        // 2. Build temperature-target speeds (use resolved values so virtual sensors work)
        var ttSpeeds = new Dictionary<string, double>();
        if (_tempTargetSvc is not null)
        {
            ttSpeeds = _tempTargetSvc.Evaluate(sensorValues);
        }

        // 3. Merge over union of fan IDs — hottest source wins
        var allFanIds = new HashSet<string>(curveSpeeds.Keys);
        allFanIds.UnionWith(ttSpeeds.Keys);

        var allSpeeds = new Dictionary<string, double>();
        var newSources = new Dictionary<string, string>();
        foreach (var fanId in allFanIds)
        {
            curveSpeeds.TryGetValue(fanId, out var curveSpeed);
            ttSpeeds.TryGetValue(fanId, out var ttSpeed);
            var finalSpeed = Math.Max(curveSpeed, ttSpeed);
            // Dominant source: temperature_target wins when its speed is higher
            newSources[fanId] = (ttSpeed > 0.0 && ttSpeed >= curveSpeed)
                ? "temperature_target"
                : "profile";

            // Enforce per-fan minimum speed floor (zero-RPM fans are exempt at 0%)
            _fanSettings.TryGetValue(fanId, out var fs);
            if (fs is { MinSpeedPct: > 0 })
            {
                if (finalSpeed == 0 && fs.ZeroRpmCapable)
                {
                    // Allow true 0% on zero-RPM capable fans
                }
                else
                {
                    finalSpeed = Math.Max(finalSpeed, fs.MinSpeedPct);
                }
            }

            allSpeeds[fanId] = finalSpeed;
        }

        // Apply ramp-rate limiting: clamp speed change per tick (panic bypasses this)
        var rampRate = _store.FanRampRatePctPerSec;
        var nowMs = _rampStopwatch.ElapsedMilliseconds;
        var elapsedSec = (_lastTickMs > 0) ? (nowMs - _lastTickMs) / 1000.0 : 0.0;
        _lastTickMs = nowMs;

        if (rampRate > 0 && elapsedSec > 0)
        {
            var maxDelta = rampRate * elapsedSec;
            foreach (var fanId in allSpeeds.Keys.ToList())
            {
                if (_rampState.TryGetValue(fanId, out var prev))
                {
                    var target = allSpeeds[fanId];
                    if (target > prev)
                        allSpeeds[fanId] = Math.Min(target, prev + maxDelta);
                    else if (target < prev)
                        allSpeeds[fanId] = Math.Max(target, prev - maxDelta);
                }
            }
        }

        // Track ramp state for next tick
        foreach (var (fanId, speed) in allSpeeds)
            _rampState[fanId] = speed;

        // Determine which fans actually need a hardware update (delta check)
        List<(string fanId, double speed)> toApply = [];
        foreach (var (fanId, finalSpeed) in allSpeeds)
        {
            _rwl.EnterReadLock();
            double last;
            try { _lastApplied.TryGetValue(fanId, out last); }
            finally { _rwl.ExitReadLock(); }

            if (Math.Abs(finalSpeed - last) >= 0.5)
                toApply.Add((fanId, finalSpeed));
        }

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

        // Persist control sources
        lock (_controlSources) { _controlSources = newSources; }

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
    // Composite sensor resolution (MAX of multiple sensors)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Resolve the effective temperature for a curve.
    /// For composite curves (SensorIds non-empty), returns the MAX of all matching sensor temps.
    /// Falls back to SensorId (single sensor) if no composite temps found.
    /// Returns null when no temperature can be determined.
    /// </summary>
    private static double? ResolveCompositeTemp(FanCurve curve, Dictionary<string, double> sensorValues)
    {
        if (curve.SensorIds is { Count: > 0 })
        {
            var temps = new List<double>();
            foreach (var sid in curve.SensorIds)
            {
                if (sensorValues.TryGetValue(sid, out var val) && double.IsFinite(val))
                    temps.Add(val);
            }
            if (temps.Count > 0)
                return temps.Max();
        }

        // Fallback to single primary sensor
        if (sensorValues.TryGetValue(curve.SensorId, out var singleVal))
            return double.IsFinite(singleVal) ? singleVal : null;
        return null;
    }

    // -----------------------------------------------------------------------
    // Hysteresis — prevent fan oscillation near curve thresholds
    // -----------------------------------------------------------------------

    /// <summary>
    /// Apply deadband hysteresis to prevent oscillation.
    /// Ramp up immediately. Ramp down only when temp drops below decision_temp minus deadband.
    /// Must be called under the write lock (mutates _hyst).
    /// </summary>
    private double ApplyHysteresis(string fanId, string sensorId,
        double currentTemp, double rawSpeed)
    {
        var deadband = _store.Deadband;
        if (deadband <= 0)
            return rawSpeed;

        var key = (fanId, sensorId);
        if (!_hyst.TryGetValue(key, out var st))
        {
            _hyst[key] = new HysteresisState { LastSpeed = rawSpeed, DecisionTemp = currentTemp };
            return rawSpeed;
        }

        // Ramp up: always allow
        if (rawSpeed >= st.LastSpeed)
        {
            st.LastSpeed = rawSpeed;
            st.DecisionTemp = currentTemp;
            return rawSpeed;
        }

        // Ramp down: only allow if temp dropped below decision_temp - deadband
        if (currentTemp <= st.DecisionTemp - deadband)
        {
            st.LastSpeed = rawSpeed;
            st.DecisionTemp = currentTemp;
            return rawSpeed;
        }

        // Within deadband — hold previous speed
        return st.LastSpeed;
    }

    /// <summary>Per-(fan, sensor) hysteresis tracking state.</summary>
    private sealed class HysteresisState
    {
        public double LastSpeed    = -1.0;
        public double DecisionTemp = 0.0;
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
