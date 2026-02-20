using DriveChill.Hardware;
using DriveChill.Models;

namespace DriveChill.Services;

/// <summary>
/// Manages fan benchmark tests — one test per fan, concurrent across different fans.
///
/// Each test sweeps speed from 0% → 100% in configurable steps, waits for RPM to
/// settle at each step, then records the result. Progress is streamed to WebSocket
/// clients via FanTestProgress objects in WsMessage.fan_test.
/// </summary>
public sealed class FanTestService
{
    private readonly FanService           _fans;
    private readonly SensorService        _sensors;
    private readonly IHardwareBackend     _hw;
    private readonly ILogger<FanTestService> _log;

    private readonly Dictionary<string, TestRun> _runs = new();
    private readonly object _lock = new();

    public FanTestService(FanService fans, SensorService sensors,
        IHardwareBackend hw, ILogger<FanTestService> log)
    {
        _fans    = fans;
        _sensors = sensors;
        _hw      = hw;
        _log     = log;
    }

    // -----------------------------------------------------------------------
    // Public API
    // -----------------------------------------------------------------------

    /// <summary>
    /// Starts a benchmark sweep for <paramref name="fanId"/>.
    /// Returns false (with a reason in <paramref name="error"/>) if the fan is not
    /// found or a test is already running for that fan.
    /// </summary>
    public bool TryStart(string fanId, FanTestOptions options, out string error)
    {
        if (!_hw.GetFanIds().Contains(fanId))
        {
            error = $"Fan '{fanId}' not found";
            return false;
        }

        lock (_lock)
        {
            if (_runs.TryGetValue(fanId, out var existing) && existing.Result.Status == "running")
            {
                error = $"A test is already running for fan '{fanId}'";
                return false;
            }

            // Capture current fan mode so we can restore it after the test
            var snapshot       = _sensors.Latest;
            var fanStatuses    = _fans.GetAll(snapshot);
            var currentStatus  = fanStatuses.FirstOrDefault(f => f.FanId == fanId);
            var previousMode   = currentStatus?.Mode ?? "auto";
            var previousCurve  = _fans.GetCurves().FirstOrDefault(c => c.FanId == fanId);

            var result = new FanTestResult
            {
                FanId   = fanId,
                Options = options,
            };

            var run = new TestRun(result);
            _fans.LockForTest(fanId);
            run.Task = Task.Run(() =>
                RunSweepAsync(fanId, run, previousMode, previousCurve, run.Cts.Token));

            _runs[fanId] = run;
        }

        error = "";
        return true;
    }

    /// <summary>Returns the most recent result for <paramref name="fanId"/>, or null.</summary>
    public FanTestResult? GetResult(string fanId)
    {
        lock (_lock)
            return _runs.TryGetValue(fanId, out var run) ? run.Result : null;
    }

    /// <summary>Cancels a running test. Returns false if no test is running for this fan.</summary>
    public bool Cancel(string fanId)
    {
        lock (_lock)
        {
            if (!_runs.TryGetValue(fanId, out var run) || run.Result.Status != "running")
                return false;
            run.Cts.Cancel();
            return true;
        }
    }

    /// <summary>
    /// Returns slim progress snapshots for all currently-running tests.
    /// Called by WebSocketHub on every WS tick. Empty list when idle.
    /// </summary>
    public IReadOnlyList<FanTestProgress> GetActiveProgress()
    {
        lock (_lock)
        {
            return _runs.Values
                .Where(r => r.Result.Status == "running")
                .Select(r =>
                {
                    var last = r.Result.Steps.LastOrDefault();
                    return new FanTestProgress
                    {
                        FanId            = r.Result.FanId,
                        Status           = r.Result.Status,
                        StepsDone        = r.Result.Steps.Count,
                        StepsTotal       = r.Result.Options.Steps + 1, // steps + the 0% step
                        CurrentPct       = r.CurrentSpeedPct,
                        CurrentRpm       = last?.Rpm,
                        Steps            = r.Result.Steps.ToList(),
                        MinOperationalPct = r.Result.MinOperationalPct,
                    };
                })
                .ToList();
        }
    }

    // -----------------------------------------------------------------------
    // Sweep loop
    // -----------------------------------------------------------------------

    private async Task RunSweepAsync(string fanId, TestRun run,
        string previousMode, FanCurve? previousCurve,
        CancellationToken ct)
    {
        _log.LogInformation("Fan benchmark started: {FanId} ({Steps} steps, {SettleMs}ms settle)",
            fanId, run.Result.Options.Steps, run.Result.Options.SettleMs);
        try
        {
            var opts = run.Result.Options;
            // Build speed list: 0%, step%, 2*step%, …, 100%
            double stepSize = 100.0 / opts.Steps;
            var speeds = Enumerable.Range(0, opts.Steps + 1)
                .Select(i => Math.Min(100.0, Math.Round(i * stepSize, 1)))
                .ToList();

            foreach (var speedPct in speeds)
            {
                ct.ThrowIfCancellationRequested();

                // Set speed directly on hardware (bypasses FanService state)
                _hw.SetFanSpeed(fanId, speedPct);
                lock (_lock) run.CurrentSpeedPct = speedPct;

                // Wait for RPM to settle
                await Task.Delay(opts.SettleMs, ct);

                // Read RPM from the latest sensor snapshot (SensorWorker keeps it fresh)
                var rpm = ReadRpm(fanId, _sensors.Latest.Readings);
                var spinning = rpm.HasValue && rpm.Value >= opts.MinRpmThreshold;

                var step = new FanTestStep
                {
                    SpeedPct = speedPct,
                    Rpm      = rpm,
                    Spinning = spinning,
                };

                lock (_lock)
                {
                    run.Result.Steps.Add(step);

                    // Stall detection: first spinning step
                    if (spinning && run.Result.MinOperationalPct == null)
                        run.Result.MinOperationalPct = speedPct;

                    // Max RPM at full speed
                    if (speedPct >= 100 && rpm.HasValue)
                        run.Result.MaxRpm = rpm;
                }
            }

            lock (_lock)
            {
                run.Result.Status      = "completed";
                run.Result.CompletedAt = DateTimeOffset.UtcNow;
            }

            _log.LogInformation(
                "Fan benchmark completed: {FanId} — stall={MinOp}% maxRpm={MaxRpm}",
                fanId, run.Result.MinOperationalPct, run.Result.MaxRpm);
        }
        catch (OperationCanceledException)
        {
            lock (_lock)
            {
                run.Result.Status      = "cancelled";
                run.Result.CompletedAt = DateTimeOffset.UtcNow;
            }
            _log.LogInformation("Fan benchmark cancelled: {FanId}", fanId);
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                run.Result.Status      = "failed";
                run.Result.Error       = ex.Message;
                run.Result.CompletedAt = DateTimeOffset.UtcNow;
            }
            _log.LogWarning(ex, "Fan benchmark failed: {FanId}", fanId);
        }
        finally
        {
            _fans.UnlockFromTest(fanId);
            RestorePreviousMode(fanId, previousMode, previousCurve);
        }
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static double? ReadRpm(string fanId, IReadOnlyList<SensorReading> readings)
        => readings.FirstOrDefault(r =>
                r.SensorType == SensorTypeValues.FanRpm &&
                (r.Id == $"{fanId}_rpm" || r.Id.StartsWith(fanId)))
            ?.Value;

    private void RestorePreviousMode(string fanId, string previousMode, FanCurve? previousCurve)
    {
        try
        {
            switch (previousMode)
            {
                case "curve" when previousCurve != null:
                    _fans.SetCurve(previousCurve);
                    break;
                case "auto":
                    _fans.SetAuto(fanId);
                    break;
                // "manual" — leave at the last speed the test set; user can adjust
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to restore fan mode after benchmark: {FanId}", fanId);
        }
    }

    // -----------------------------------------------------------------------
    // Internal state
    // -----------------------------------------------------------------------

    private sealed class TestRun
    {
        public FanTestResult Result { get; }
        public CancellationTokenSource Cts { get; } = new();
        public Task Task { get; set; } = Task.CompletedTask;
        public double CurrentSpeedPct { get; set; }

        public TestRun(FanTestResult result) => Result = result;
    }
}
