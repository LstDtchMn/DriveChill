using DriveChill.Hardware;
using DriveChill.Models;

namespace DriveChill.Services;

/// <summary>
/// Background service that polls hardware every <see cref="AppSettings.PollIntervalMs"/> ms,
/// publishes snapshots to <see cref="SensorService"/>, and persists readings to DB every 10 s.
///
/// Hardware I/O is offloaded to the thread pool via Task.Run() to avoid blocking Kestrel.
/// </summary>
public sealed class SensorWorker : BackgroundService
{
    private readonly IHardwareBackend _hw;
    private readonly SensorService    _sensors;
    private readonly FanService       _fans;
    private readonly AlertService     _alerts;
    private readonly DbService        _db;
    private readonly AppSettings      _settings;
    private readonly ILogger<SensorWorker> _log;

    private DateTimeOffset _lastDbWrite = DateTimeOffset.MinValue;
    private const int DbIntervalSeconds = 10;

    public SensorWorker(IHardwareBackend hw, SensorService sensors, FanService fans,
        AlertService alerts, DbService db, AppSettings settings, ILogger<SensorWorker> log)
    {
        _hw      = hw;
        _sensors = sensors;
        _fans    = fans;
        _alerts  = alerts;
        _db      = db;
        _settings = settings;
        _log     = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _hw.Initialize();
        _log.LogInformation("SensorWorker started — backend: {Backend}", _hw.GetBackendName());

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Offload blocking hardware read to thread pool
                var readings = await Task.Run(() => _hw.GetSensorReadings(), stoppingToken);

                var snapshot = new SensorSnapshot
                {
                    Readings  = readings,
                    Backend   = _hw.GetBackendName(),
                    Timestamp = DateTimeOffset.UtcNow,
                };

                _sensors.Update(snapshot);

                // Apply active fan curve (non-blocking — fan speeds are cached)
                await _fans.ApplyCurvesAsync(readings, stoppingToken);

                // Evaluate alert thresholds
                _alerts.Evaluate(readings);

                // Persist to DB on a slower cadence
                if ((DateTimeOffset.UtcNow - _lastDbWrite).TotalSeconds >= DbIntervalSeconds)
                {
                    await _db.LogReadingsAsync(readings, stoppingToken);
                    _lastDbWrite = DateTimeOffset.UtcNow;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Sensor poll error — will retry");
            }

            await Task.Delay(_settings.PollIntervalMs, stoppingToken).ConfigureAwait(false);
        }

        _log.LogInformation("SensorWorker stopped");
    }
}
