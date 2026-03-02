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
    private readonly IHardwareBackend            _hw;
    private readonly SensorService               _sensors;
    private readonly FanService                  _fans;
    private readonly AlertService                _alerts;
    private readonly WebhookService              _webhooks;
    private readonly EmailNotificationService    _email;
    private readonly PushNotificationService     _push;
    private readonly DbService                   _db;
    private readonly SettingsStore               _store;
    private readonly AppSettings                 _settings;
    private readonly ILogger<SensorWorker>       _log;

    private DateTimeOffset _lastDbWrite = DateTimeOffset.MinValue;
    private const int DbIntervalSeconds = 10;

    public SensorWorker(IHardwareBackend hw, SensorService sensors, FanService fans,
        AlertService alerts, WebhookService webhooks,
        EmailNotificationService email, PushNotificationService push,
        DbService db, SettingsStore store, AppSettings settings, ILogger<SensorWorker> log)
    {
        _hw       = hw;
        _sensors  = sensors;
        _fans     = fans;
        _alerts   = alerts;
        _webhooks = webhooks;
        _email    = email;
        _push     = push;
        _db       = db;
        _store    = store;
        _settings = settings;
        _log      = log;
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
                var newEvents = _alerts.Evaluate(readings);
                if (newEvents.Count > 0)
                {
                    // Dispatch all delivery channels concurrently (fire-and-forget).
                    // Errors inside each service are logged and swallowed so a delivery
                    // failure never crashes the polling loop.
                    var capturedEvents = newEvents;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var tasks = new List<Task>
                            {
                                _webhooks.DispatchAlertEventsAsync(capturedEvents, CancellationToken.None),
                            };
                            foreach (var evt in capturedEvents)
                            {
                                tasks.Add(_email.SendAlertAsync(evt, CancellationToken.None));
                                tasks.Add(_push.SendAlertAsync(evt, CancellationToken.None));
                            }
                            await Task.WhenAll(tasks);
                        }
                        catch (Exception ex) { _log.LogWarning(ex, "Alert delivery error"); }
                    }, CancellationToken.None);
                }

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

            // Read poll interval from SettingsStore (user-editable at runtime) instead
            // of AppSettings (immutable after startup).
            await Task.Delay(_store.PollIntervalMs, stoppingToken).ConfigureAwait(false);
        }

        _log.LogInformation("SensorWorker stopped");
    }
}
