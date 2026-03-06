using DriveChill.Models;
using Microsoft.Data.Sqlite;

namespace DriveChill.Services;

/// <summary>
/// Discovers and monitors local drives via an <see cref="IDriveProvider"/>.
/// Publishes hdd_temp sensor readings into SensorService.
/// Polling schedule mirrors the Python backend.
/// </summary>
public sealed class DriveMonitorService : IDisposable
{
    private readonly IDriveProvider _provider;
    private readonly AppSettings _settings;
    private readonly ILogger<DriveMonitorService> _log;
    private readonly SensorService? _sensorService;
    private readonly SmartTrendService? _smartTrend;
    private readonly AlertService? _alertService;
    private DriveHealthNormalizer? _normalizer;

    private readonly Dictionary<string, DriveRawData> _drives = new();
    private readonly object _drivesLock = new();

    private CancellationTokenSource? _cts;
    private Task? _tempTask;
    private Task? _healthTask;
    private Task? _rescanTask;

    private bool _providerAvailable;
    private string _connStr = "";

    public DriveMonitorService(
        IDriveProvider provider,
        AppSettings settings,
        ILogger<DriveMonitorService> log,
        SensorService sensorService,
        SmartTrendService? smartTrend = null,
        AlertService? alertService = null)
    {
        _provider      = provider;
        _settings      = settings;
        _log           = log;
        _sensorService = sensorService;
        _smartTrend    = smartTrend;
        _alertService  = alertService;
    }

    // ── Public accessors ──────────────────────────────────────────────────────

    public IReadOnlyList<DriveRawData> GetAllDrives()
    {
        lock (_drivesLock) return [.. _drives.Values];
    }

    public DriveRawData? GetDrive(string driveId)
    {
        lock (_drivesLock) return _drives.GetValueOrDefault(driveId);
    }

    /// <summary>True when the underlying provider (e.g. smartctl) is available.</summary>
    public bool SmartctlAvailable => _providerAvailable;

    // ── Startup / shutdown ────────────────────────────────────────────────────

    public async Task StartAsync(DriveSettings s, CancellationToken ct = default)
    {
        if (!s.Enabled) return;

        _connStr           = $"Data Source={_settings.DbPath}";
        _normalizer        = new DriveHealthNormalizer(s);
        _providerAvailable = await _provider.CheckAvailableAsync(s, ct);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;

        // Initial scan (blocking — must complete before loops start)
        try { await RescanAsync(s, token); }
        catch (Exception ex) { _log.LogWarning(ex, "Drive initial scan failed"); }

        _tempTask   = RunLoop(() => PollTempsAsync(s, token),   s.FastPollSeconds,   token);
        _healthTask = RunLoop(() => PollHealthAsync(s, token),  s.HealthPollSeconds, token);
        _rescanTask = RunLoop(() => RescanAsync(s, token),      s.RescanPollSeconds, token);
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        var tasks = new[] { _tempTask, _healthTask, _rescanTask }
            .Where(t => t is not null).Cast<Task>();
        try { await Task.WhenAll(tasks); }
        catch (OperationCanceledException) { }
        catch (AggregateException aex) when (aex.InnerExceptions.All(e => e is OperationCanceledException)) { }
    }

    public void Dispose() { _cts?.Dispose(); }

    // ── Polling loops ─────────────────────────────────────────────────────────

    private async Task RunLoop(Func<Task> work, int intervalSeconds, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await work(); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _log.LogWarning(ex, "Drive monitor loop iteration failed"); }
            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), ct);
        }
    }

    // ── Scan / poll implementations ───────────────────────────────────────────

    private async Task RescanAsync(DriveSettings s, CancellationToken ct)
    {
        _providerAvailable = await _provider.CheckAvailableAsync(s, ct);
        if (!_providerAvailable) return;

        var discovered = await _provider.DiscoverDrivesAsync(s, ct);
        var newDrives  = discovered.ToDictionary(d => d.Id);

        lock (_drivesLock)
        {
            var toRemove = _drives.Keys.Except(newDrives.Keys).ToList();
            foreach (var id in toRemove) _drives.Remove(id);
            foreach (var (id, raw) in newDrives)
                _drives[id] = raw;
        }

        await UpsertDrivesAsync(newDrives.Values, ct);
        await PublishDriveSensorsAsync(s, ct);
        _log.LogDebug("Drive rescan complete: {Count} drives", newDrives.Count);
    }

    private async Task PollTempsAsync(DriveSettings s, CancellationToken ct)
    {
        bool updated = false;
        List<DriveRawData> snapshot;
        lock (_drivesLock) snapshot = [.. _drives.Values];

        foreach (var raw in snapshot)
        {
            var refreshed = await _provider.GetDriveDataAsync(raw.DevicePath, s, ct);
            if (refreshed is not null)
            {
                lock (_drivesLock) _drives[raw.Id] = refreshed;
                if (refreshed.TemperatureC.HasValue) updated = true;
            }
        }
        if (updated) await PublishDriveSensorsAsync(s, ct);
    }

    private async Task PollHealthAsync(DriveSettings s, CancellationToken ct)
    {
        List<DriveRawData> snapshot;
        lock (_drivesLock) snapshot = [.. _drives.Values];

        foreach (var raw in snapshot)
        {
            var refreshed = await _provider.GetDriveDataAsync(raw.DevicePath, s, ct);
            if (refreshed is null) continue;

            lock (_drivesLock) _drives[raw.Id] = refreshed;
            if (_normalizer is not null)
                await InsertHealthSnapshotAsync(refreshed, _normalizer, ct);

            // SMART trend detection — inject any new alerts into AlertService
            if (_smartTrend is not null)
            {
                var trendAlerts = _smartTrend.CheckDrive(
                    raw.Id, refreshed.Name,
                    refreshed.ReallocatedSectors,
                    refreshed.WearPercentUsed,
                    refreshed.PowerOnHours);

                if (_alertService is not null)
                {
                    foreach (var ta in trendAlerts)
                    {
                        var syntheticRuleId = $"smart_{raw.Id}_{ta.Condition}";
                        _alertService.InjectEvent(
                            ruleId:      syntheticRuleId,
                            sensorId:    $"hdd_temp_{raw.Id}",
                            sensorName:  refreshed.Name,
                            actualValue: ta.ActualValue,
                            threshold:   ta.Threshold,
                            condition:   "above",
                            message:     ta.Message);
                    }
                }
            }
        }
        await PublishDriveSensorsAsync(s, ct);
    }

    // ── Sensor injection ──────────────────────────────────────────────────────

    private Task PublishDriveSensorsAsync(DriveSettings s, CancellationToken _)
    {
        if (_sensorService is null || _normalizer is null) return Task.CompletedTask;
        List<DriveRawData> drives;
        lock (_drivesLock) drives = [.. _drives.Values];

        var readings = drives
            .Where(d => d.TemperatureC.HasValue)
            .Select(d => new SensorReading
            {
                Id         = $"hdd_temp_{d.Id}",
                Name       = d.Name,
                SensorType = "hdd_temp",
                Value      = d.TemperatureC!.Value,
                MinValue   = 0,
                MaxValue   = _normalizer.TempCriticalC(d) + 20.0,
                Unit       = "°C",
                DriveId    = d.Id,
                EntityName = d.Name,
                SourceKind = d.Capabilities.TemperatureSource == "smartctl" ? "smartctl" : "native",
            })
            .ToList();

        _sensorService.UpdateDriveReadings(readings);
        return Task.CompletedTask;
    }

    // ── Public manual actions ─────────────────────────────────────────────────

    public async Task<int> RescanNowAsync(DriveSettings s, CancellationToken ct = default)
    {
        await RescanAsync(s, ct);
        lock (_drivesLock) return _drives.Count;
    }

    public async Task<DriveRawData?> RefreshDriveAsync(
        string driveId, DriveSettings s, CancellationToken ct = default)
    {
        DriveRawData? raw;
        lock (_drivesLock) raw = _drives.GetValueOrDefault(driveId);
        if (raw is null) return null;

        var refreshed = await _provider.GetDriveDataAsync(raw.DevicePath, s, ct);
        if (refreshed is not null)
        {
            lock (_drivesLock) _drives[driveId] = refreshed;
            await PublishDriveSensorsAsync(s, ct);
        }
        return refreshed ?? raw;
    }

    public async Task<string?> StartSelfTestAsync(
        string driveId, SelfTestType testType, DriveSettings s, CancellationToken ct = default)
    {
        DriveRawData? raw;
        lock (_drivesLock) raw = _drives.GetValueOrDefault(driveId);
        if (raw is null) return null;

        var typeFlag = testType switch
        {
            SelfTestType.Short      => "short",
            SelfTestType.Extended   => "long",
            SelfTestType.Conveyance => "conveyance",
            _ => "short",
        };
        return await _provider.StartSelfTestAsync(raw.DevicePath, typeFlag, s, ct);
    }

    public async Task<bool> AbortSelfTestAsync(
        string driveId, DriveSettings s, CancellationToken ct = default)
    {
        DriveRawData? raw;
        lock (_drivesLock) raw = _drives.GetValueOrDefault(driveId);
        if (raw is null) return false;
        return await _provider.AbortSelfTestAsync(raw.DevicePath, s, ct);
    }

    // ── DB helpers ────────────────────────────────────────────────────────────

    private async Task UpsertDrivesAsync(
        IEnumerable<DriveRawData> drives, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        var now = DateTimeOffset.UtcNow.ToString("o");
        await using var tx = await conn.BeginTransactionAsync(ct);
        foreach (var d in drives)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = (SqliteTransaction)tx;
            cmd.CommandText = """
                INSERT INTO drives
                    (id, name, model, serial_full, device_path, bus_type, media_type,
                     capacity_bytes, firmware_version, smart_available, native_available,
                     supports_self_test, supports_abort, last_seen_at, last_updated_at)
                VALUES ($id,$name,$model,$serial,$devpath,$bus,$media,
                        $cap,$fw,$smart,$native,$selftest,$abort,$now,$now)
                ON CONFLICT(id) DO UPDATE SET
                    name=excluded.name, model=excluded.model,
                    serial_full=excluded.serial_full,
                    device_path=excluded.device_path, bus_type=excluded.bus_type,
                    media_type=excluded.media_type, capacity_bytes=excluded.capacity_bytes,
                    firmware_version=excluded.firmware_version,
                    smart_available=excluded.smart_available,
                    native_available=excluded.native_available,
                    supports_self_test=excluded.supports_self_test,
                    supports_abort=excluded.supports_abort,
                    last_seen_at=excluded.last_seen_at,
                    last_updated_at=excluded.last_updated_at
                """;
            cmd.Parameters.AddWithValue("$id",       d.Id);
            cmd.Parameters.AddWithValue("$name",     d.Name);
            cmd.Parameters.AddWithValue("$model",    d.Model);
            cmd.Parameters.AddWithValue("$serial",   d.Serial);
            cmd.Parameters.AddWithValue("$devpath",  d.DevicePath);
            cmd.Parameters.AddWithValue("$bus",      d.BusType);
            cmd.Parameters.AddWithValue("$media",    d.MediaType);
            cmd.Parameters.AddWithValue("$cap",      d.CapacityBytes);
            cmd.Parameters.AddWithValue("$fw",       d.FirmwareVersion);
            cmd.Parameters.AddWithValue("$smart",    d.Capabilities.SmartRead ? 1 : 0);
            cmd.Parameters.AddWithValue("$native",   0);
            cmd.Parameters.AddWithValue("$selftest", d.Capabilities.SmartSelfTestShort ? 1 : 0);
            cmd.Parameters.AddWithValue("$abort",    d.Capabilities.SmartSelfTestAbort ? 1 : 0);
            cmd.Parameters.AddWithValue("$now",      now);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    private async Task InsertHealthSnapshotAsync(
        DriveRawData d, DriveHealthNormalizer normalizer, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO drive_health_snapshots
                (drive_id, recorded_at, temperature_c, health_status, health_percent,
                 predicted_failure, wear_percent_used, available_spare_percent,
                 reallocated_sectors, pending_sectors, uncorrectable_errors,
                 media_errors, power_on_hours, unsafe_shutdowns)
            VALUES ($did,$now,$temp,$health,$healthpct,$pf,$wear,$spare,
                    $realloc,$pending,$uncorr,$media,$poh,$unsafe)
            """;
        var now = DateTimeOffset.UtcNow.ToString("o");
        cmd.Parameters.AddWithValue("$did",       d.Id);
        cmd.Parameters.AddWithValue("$now",       now);
        cmd.Parameters.AddWithValue("$temp",      (object?)d.TemperatureC ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$health",    normalizer.HealthStatus(d));
        cmd.Parameters.AddWithValue("$healthpct", (object?)normalizer.HealthPercent(d) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pf",        d.PredictedFailure ? 1 : 0);
        cmd.Parameters.AddWithValue("$wear",      (object?)d.WearPercentUsed ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$spare",     (object?)d.AvailableSparePercent ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$realloc",   (object?)d.ReallocatedSectors ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pending",   (object?)d.PendingSectors ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$uncorr",    (object?)d.UncorrectableErrors ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$media",     (object?)d.MediaErrors ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$poh",       (object?)d.PowerOnHours ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$unsafe",    (object?)d.UnsafeShutdowns ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Helper to build summary/detail shapes ─────────────────────────────────

    public DriveSummary ToSummary(DriveRawData d)
    {
        var health    = _normalizer?.HealthStatus(d) ?? "unknown";
        var healthPct = _normalizer?.HealthPercent(d);
        return new DriveSummary
        {
            Id = d.Id, Name = d.Name, Model = d.Model,
            SerialMasked     = MaskSerial(d.Serial),
            DevicePathMasked = MaskDevicePath(d.DevicePath),
            BusType = d.BusType, MediaType = d.MediaType,
            CapacityBytes    = d.CapacityBytes,
            TemperatureC     = d.TemperatureC,
            HealthStatus     = health, HealthPercent = healthPct,
            SmartAvailable   = d.Capabilities.SmartRead,
            NativeAvailable  = d.Capabilities.HealthSource != "none",
            SupportsSelfTest = d.Capabilities.SmartSelfTestShort,
            SupportsAbort    = d.Capabilities.SmartSelfTestAbort,
        };
    }

    public DriveDetail ToDetail(DriveRawData d, DriveSelfTestRun? lastTest = null)
    {
        var summary = ToSummary(d);
        var warnC = _normalizer?.TempWarningC(d)  ?? 45.0;
        var critC = _normalizer?.TempCriticalC(d) ?? 50.0;
        return new DriveDetail
        {
            Id = summary.Id, Name = summary.Name, Model = summary.Model,
            SerialMasked     = summary.SerialMasked,
            DevicePathMasked = summary.DevicePathMasked,
            BusType = summary.BusType, MediaType = summary.MediaType,
            CapacityBytes    = summary.CapacityBytes,
            TemperatureC     = summary.TemperatureC,
            HealthStatus     = summary.HealthStatus, HealthPercent = summary.HealthPercent,
            SmartAvailable   = summary.SmartAvailable,
            NativeAvailable  = summary.NativeAvailable,
            SupportsSelfTest = summary.SupportsSelfTest,
            SupportsAbort    = summary.SupportsAbort,
            SerialFull            = d.Serial, DevicePath = d.DevicePath,
            FirmwareVersion       = d.FirmwareVersion,
            InterfaceSpeed        = d.InterfaceSpeed,
            RotationRateRpm       = d.RotationRateRpm,
            PowerOnHours          = d.PowerOnHours,
            PowerCycleCount       = d.PowerCycleCount,
            UnsafeShutdowns       = d.UnsafeShutdowns,
            WearPercentUsed       = d.WearPercentUsed,
            AvailableSparePercent = d.AvailableSparePercent,
            ReallocatedSectors    = d.ReallocatedSectors,
            PendingSectors        = d.PendingSectors,
            UncorrectableErrors   = d.UncorrectableErrors,
            MediaErrors           = d.MediaErrors,
            PredictedFailure      = d.PredictedFailure,
            TemperatureWarningC   = warnC,
            TemperatureCriticalC  = critC,
            Capabilities  = d.Capabilities,
            LastSelfTest  = lastTest,
            RawAttributes = d.RawAttributes,
        };
    }

    private static string MaskSerial(string serial) =>
        string.IsNullOrEmpty(serial) ? "****" : "****" + serial[^Math.Min(4, serial.Length)..];

    private static string MaskDevicePath(string path)
    {
        var idx = path.LastIndexOf('/');
        return idx >= 0 ? path[(idx + 1)..] : path;
    }
}
