using System.Diagnostics;
using DriveChill.Hardware;
using DriveChill.Models;
using Prometheus;

namespace DriveChill.Services;

/// <summary>
/// Background service that polls hardware every <see cref="AppSettings.PollIntervalMs"/> ms,
/// publishes snapshots to <see cref="SensorService"/>, and persists readings to DB every 10 s.
///
/// Hardware I/O is offloaded to the thread pool via Task.Run() to avoid blocking Kestrel.
/// </summary>
public sealed class SensorWorker : BackgroundService
{
    private readonly IHardwareBackend               _hw;
    private readonly SensorService                  _sensors;
    private readonly FanService                     _fans;
    private readonly AlertService                   _alerts;
    private readonly AlertDeliveryService           _alertDelivery;
    private readonly NotificationChannelService     _notifChannels;
    private readonly TemperatureTargetService       _tempTargets;
    private readonly VirtualSensorService           _virtualSensors;
    private readonly DbService                      _db;
    private readonly SettingsStore                  _store;
    private readonly AppSettings                    _settings;
    private readonly ILogger<SensorWorker>          _log;

    private DateTimeOffset _lastDbWrite = DateTimeOffset.MinValue;
    private DateTimeOffset _lastPrune   = DateTimeOffset.MinValue;
    private const int DbIntervalSeconds = 10;
    private const int PruneIntervalSeconds = 3600; // prune once per hour
    private int _telemetryPublishingFlag; // 0 = idle, 1 = publishing (atomic via Interlocked)

    public SensorWorker(IHardwareBackend hw, SensorService sensors, FanService fans,
        AlertService alerts, AlertDeliveryService alertDelivery,
        NotificationChannelService notifChannels,
        TemperatureTargetService tempTargets, VirtualSensorService virtualSensors,
        DbService db, SettingsStore store, AppSettings settings, ILogger<SensorWorker> log)
    {
        _hw              = hw;
        _sensors         = sensors;
        _fans            = fans;
        _alerts          = alerts;
        _alertDelivery   = alertDelivery;
        _notifChannels   = notifChannels;
        _tempTargets     = tempTargets;
        _virtualSensors  = virtualSensors;
        _db              = db;
        _store           = store;
        _settings        = settings;
        _log             = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _hw.Initialize();
        await _tempTargets.LoadAsync(stoppingToken);
        await _fans.LoadFanSettingsAsync(_db, stoppingToken);

        // Load virtual sensor definitions from DB
        var vsDefs = await _db.GetVirtualSensorsAsync(stoppingToken);
        _virtualSensors.Load(vsDefs);

        // Initialize alert rules from DB
        await _alerts.InitializeAsync(stoppingToken);

        // Wire alert-triggered profile switching
        _alerts.SetActivateProfileFn(async profileId =>
        {
            var profile = await _db.GetProfileAsync(profileId);
            if (profile == null) return;
            await _db.ActivateProfileAsync(profileId);
            _fans.SetCurves(profile.Curves);
        });
        // Record current active profile for revert-after-clear
        var allProfiles = await _db.ListProfilesAsync(stoppingToken);
        var activeProfile = allProfiles.FirstOrDefault(p => p.IsActive);
        if (activeProfile != null)
            _alerts.SetPreAlertProfile(activeProfile.Id);

        _log.LogInformation("SensorWorker started — backend: {Backend}", _hw.GetBackendName());

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Offload blocking hardware read to thread pool; time the call for Prometheus.
                var backendName = _hw.GetBackendName();
                var sw = Stopwatch.StartNew();
                var readings = await Task.Run(() => _hw.GetSensorReadings(), stoppingToken);
                sw.Stop();
                DriveChillMetrics.SensorPollDuration
                    .WithLabels(backendName)
                    .Observe(sw.Elapsed.TotalSeconds);

                var snapshot = new SensorSnapshot
                {
                    Readings  = readings,
                    Backend   = backendName,
                    Timestamp = DateTimeOffset.UtcNow,
                };

                _sensors.Update(snapshot);

                // MQTT telemetry — single-flight: skip if previous publish still running
                if (Interlocked.CompareExchange(ref _telemetryPublishingFlag, 1, 0) == 0)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var telemetryReadings = readings.Select(r => new TelemetryReading
                            {
                                SensorId   = r.Id,
                                SensorName = r.Name,
                                SensorType = r.SensorType,
                                Value      = r.Value,
                                Unit       = r.Unit,
                            }).ToList();
                            await _notifChannels.PublishTelemetryAsync(telemetryReadings, stoppingToken);
                        }
                        catch (Exception ex)
                        {
                            _log.LogDebug(ex, "MQTT telemetry publish failed");
                        }
                        finally
                        {
                            Interlocked.Exchange(ref _telemetryPublishingFlag, 0);
                        }
                    }, stoppingToken);
                }

                // Count readings by sensor_type
                foreach (var r in readings)
                    if (!string.IsNullOrEmpty(r.SensorType))
                        DriveChillMetrics.SensorReadingsTotal.WithLabels(r.SensorType).Inc();

                // Apply active fan curve (non-blocking — fan speeds are cached)
                await _fans.ApplyCurvesAsync(readings, stoppingToken);

                // Update fan speed gauges from latest applied speeds
                foreach (var fan in _fans.GetAll(snapshot))
                    DriveChillMetrics.FanSpeedPct.WithLabels(fan.FanId).Set(fan.SpeedPercent);

                // Update drive temperature gauges for hdd_temp sensors with a drive_id
                foreach (var r in readings)
                    if (r.SensorType == SensorTypeValues.HddTemp && !string.IsNullOrEmpty(r.DriveId))
                        DriveChillMetrics.DriveTempCelsius.WithLabels(r.DriveId).Set(r.Value);

                // Evaluate alert thresholds, then drain any synthetically injected events
                // (e.g. SMART trend alerts from DriveMonitorService) so they reach the same
                // delivery fan-out as threshold-crossing events.
                var newEvents = _alerts.Evaluate(readings);
                var injectedEvents = _alerts.DrainInjectedEvents();
                IReadOnlyList<AlertEvent> allEvents = injectedEvents.Count > 0
                    ? [.. newEvents, .. injectedEvents]
                    : newEvents;

                if (allEvents.Count > 0)
                {
                    // Increment alert counter for each fired event
                    foreach (var evt in allEvents)
                        DriveChillMetrics.AlertEventsTotal.WithLabels(evt.RuleId, evt.Condition).Inc();

                    // Dispatch all delivery channels concurrently (fire-and-forget).
                    _alertDelivery.DispatchAsync(allEvents);
                }

                // Persist to DB on a slower cadence
                if ((DateTimeOffset.UtcNow - _lastDbWrite).TotalSeconds >= DbIntervalSeconds)
                {
                    await _db.LogReadingsAsync(readings, stoppingToken);
                    _lastDbWrite = DateTimeOffset.UtcNow;
                }

                // Prune old sensor_log + drive_health_snapshots + auth_log once per hour
                if ((DateTimeOffset.UtcNow - _lastPrune).TotalSeconds >= PruneIntervalSeconds)
                {
                    try
                    {
                        var retentionDays = Math.Max(_store.RetentionDays, 1);
                        await _db.PruneAsync(retentionDays, stoppingToken);
                        await _db.CleanupOldAuthLogsAsync(90, stoppingToken);
                        _lastPrune = DateTimeOffset.UtcNow;
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "Retention prune failed — will retry next cycle");
                    }
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
