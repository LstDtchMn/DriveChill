using Microsoft.Data.Sqlite;
using DriveChill.Models;
using DriveChill.Utils;
using System.Text;

namespace DriveChill.Services;

/// <summary>
/// Persists sensor readings to a local SQLite database for history and export.
/// Schema is auto-created on first use.
/// </summary>
public sealed class DbService : IDisposable
{
    private readonly string _connStr;
    private readonly AppSettings _settings;
    private readonly ILogger<DbService> _log;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile bool _initialised;

    public DbService(AppSettings settings, ILogger<DbService> log)
    {
        _settings = settings;
        _log = log;
        Directory.CreateDirectory(settings.DataDir);
        _connStr = $"Data Source={settings.DbPath};Foreign Keys=True";
    }

    // -----------------------------------------------------------------------
    // Schema init (called lazily, thread-safe double-check locking)
    // -----------------------------------------------------------------------

    private async Task EnsureInitialisedAsync(CancellationToken ct = default)
    {
        if (_initialised) return;
        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialised) return; // double-check after acquiring lock
            await using var conn = new SqliteConnection(_connStr);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS sensor_log (
                    id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    timestamp   TEXT    NOT NULL,
                    sensor_id   TEXT    NOT NULL,
                    sensor_name TEXT    NOT NULL,
                    sensor_type TEXT    NOT NULL,
                    value       REAL    NOT NULL,
                    unit        TEXT    NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_sensor_log_timestamp ON sensor_log(timestamp);
                CREATE INDEX IF NOT EXISTS idx_sensor_log_sensor_id ON sensor_log(sensor_id);

                CREATE TABLE IF NOT EXISTS sensor_labels (
                    sensor_id   TEXT PRIMARY KEY,
                    label       TEXT NOT NULL,
                    updated_at  TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS quiet_hours (
                    id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    day_of_week INTEGER NOT NULL,
                    start_time  TEXT    NOT NULL,
                    end_time    TEXT    NOT NULL,
                    profile_id  TEXT    NOT NULL,
                    enabled     INTEGER NOT NULL DEFAULT 1
                );

                CREATE TABLE IF NOT EXISTS users (
                    id            INTEGER PRIMARY KEY AUTOINCREMENT,
                    username      TEXT    NOT NULL UNIQUE,
                    password_hash TEXT    NOT NULL,
                    role          TEXT    NOT NULL DEFAULT 'admin',
                    created_at    TEXT    NOT NULL
                );

                CREATE TABLE IF NOT EXISTS sessions (
                    token      TEXT PRIMARY KEY,
                    csrf_token TEXT    NOT NULL,
                    username   TEXT    NOT NULL,
                    ip         TEXT,
                    user_agent TEXT,
                    created_at TEXT    NOT NULL,
                    expires_at TEXT    NOT NULL
                );

                CREATE TABLE IF NOT EXISTS machines (
                    id                   TEXT PRIMARY KEY,
                    name                 TEXT NOT NULL,
                    base_url             TEXT NOT NULL UNIQUE,
                    api_key_hash         TEXT,
                    enabled              INTEGER NOT NULL DEFAULT 1,
                    poll_interval_seconds REAL NOT NULL DEFAULT 30.0,
                    timeout_ms           INTEGER NOT NULL DEFAULT 5000,
                    status               TEXT NOT NULL DEFAULT 'unknown',
                    last_seen_at         TEXT,
                    last_error           TEXT,
                    consecutive_failures INTEGER NOT NULL DEFAULT 0,
                    created_at           TEXT NOT NULL,
                    updated_at           TEXT NOT NULL,
                    snapshot_json        TEXT,
                    capabilities_json    TEXT NOT NULL DEFAULT '[]',
                    last_command_at      TEXT
                );

                CREATE TABLE IF NOT EXISTS push_subscriptions (
                    id           TEXT PRIMARY KEY,
                    endpoint     TEXT NOT NULL UNIQUE,
                    p256dh       TEXT NOT NULL,
                    auth_key     TEXT NOT NULL,
                    user_agent   TEXT,
                    created_at   TEXT NOT NULL,
                    last_used_at TEXT
                );

                CREATE TABLE IF NOT EXISTS email_notification_settings (
                    id              INTEGER PRIMARY KEY CHECK (id = 1),
                    enabled         INTEGER NOT NULL DEFAULT 0,
                    smtp_host       TEXT NOT NULL DEFAULT '',
                    smtp_port       INTEGER NOT NULL DEFAULT 587,
                    smtp_username   TEXT NOT NULL DEFAULT '',
                    smtp_password   TEXT NOT NULL DEFAULT '',
                    sender_address  TEXT NOT NULL DEFAULT '',
                    recipient_list  TEXT NOT NULL DEFAULT '[]',
                    use_tls         INTEGER NOT NULL DEFAULT 1,
                    use_ssl         INTEGER NOT NULL DEFAULT 0,
                    updated_at      TEXT NOT NULL DEFAULT (datetime('now'))
                );

                INSERT OR IGNORE INTO email_notification_settings (id) VALUES (1);

                CREATE TABLE IF NOT EXISTS drives (
                    id               TEXT PRIMARY KEY,
                    name             TEXT NOT NULL,
                    model            TEXT NOT NULL DEFAULT '',
                    serial_full      TEXT NOT NULL DEFAULT '',
                    device_path      TEXT NOT NULL DEFAULT '',
                    bus_type         TEXT NOT NULL DEFAULT 'unknown',
                    media_type       TEXT NOT NULL DEFAULT 'unknown',
                    capacity_bytes   INTEGER NOT NULL DEFAULT 0,
                    firmware_version TEXT NOT NULL DEFAULT '',
                    smart_available  INTEGER NOT NULL DEFAULT 0,
                    native_available INTEGER NOT NULL DEFAULT 0,
                    supports_self_test INTEGER NOT NULL DEFAULT 0,
                    supports_abort   INTEGER NOT NULL DEFAULT 0,
                    last_seen_at     TEXT NOT NULL DEFAULT (datetime('now')),
                    last_updated_at  TEXT NOT NULL DEFAULT (datetime('now'))
                );

                CREATE TABLE IF NOT EXISTS drive_health_snapshots (
                    id                      INTEGER PRIMARY KEY AUTOINCREMENT,
                    drive_id                TEXT NOT NULL REFERENCES drives(id) ON DELETE CASCADE,
                    recorded_at             TEXT NOT NULL DEFAULT (datetime('now')),
                    temperature_c           REAL,
                    health_status           TEXT NOT NULL DEFAULT 'unknown',
                    health_percent          REAL,
                    predicted_failure       INTEGER NOT NULL DEFAULT 0,
                    wear_percent_used       REAL,
                    available_spare_percent REAL,
                    reallocated_sectors     INTEGER,
                    pending_sectors         INTEGER,
                    uncorrectable_errors    INTEGER,
                    media_errors            INTEGER,
                    power_on_hours          INTEGER,
                    unsafe_shutdowns        INTEGER
                );
                CREATE INDEX IF NOT EXISTS idx_drive_snapshots_drive_time
                    ON drive_health_snapshots (drive_id, recorded_at);

                CREATE TABLE IF NOT EXISTS drive_attributes_latest (
                    drive_id        TEXT PRIMARY KEY REFERENCES drives(id) ON DELETE CASCADE,
                    captured_at     TEXT NOT NULL DEFAULT (datetime('now')),
                    attributes_json TEXT NOT NULL DEFAULT '[]'
                );

                CREATE TABLE IF NOT EXISTS drive_self_test_runs (
                    id               TEXT PRIMARY KEY,
                    drive_id         TEXT NOT NULL REFERENCES drives(id) ON DELETE CASCADE,
                    type             TEXT NOT NULL,
                    status           TEXT NOT NULL DEFAULT 'queued',
                    progress_percent REAL,
                    started_at       TEXT NOT NULL DEFAULT (datetime('now')),
                    finished_at      TEXT,
                    failure_message  TEXT,
                    provider_run_ref TEXT
                );
                CREATE INDEX IF NOT EXISTS idx_self_test_drive ON drive_self_test_runs (drive_id, started_at DESC);

                CREATE TABLE IF NOT EXISTS drive_settings_overrides (
                    drive_id               TEXT PRIMARY KEY REFERENCES drives(id) ON DELETE CASCADE,
                    temp_warning_c         REAL,
                    temp_critical_c        REAL,
                    alerts_enabled         INTEGER,
                    curve_picker_enabled   INTEGER,
                    updated_at             TEXT NOT NULL DEFAULT (datetime('now'))
                );

                CREATE TABLE IF NOT EXISTS fan_settings (
                    fan_id           TEXT PRIMARY KEY,
                    min_speed_pct    REAL    NOT NULL DEFAULT 0.0,
                    zero_rpm_capable INTEGER NOT NULL DEFAULT 0,
                    updated_at       TEXT    NOT NULL DEFAULT (datetime('now'))
                );

                CREATE TABLE IF NOT EXISTS auth_log (
                    id         INTEGER PRIMARY KEY AUTOINCREMENT,
                    timestamp  TEXT NOT NULL,
                    event_type TEXT NOT NULL,
                    ip         TEXT,
                    username   TEXT,
                    outcome    TEXT NOT NULL,
                    detail     TEXT
                );
                CREATE INDEX IF NOT EXISTS idx_auth_log_ts ON auth_log(timestamp);

                CREATE TABLE IF NOT EXISTS temperature_targets (
                    id            TEXT PRIMARY KEY,
                    name          TEXT NOT NULL DEFAULT '',
                    drive_id      TEXT,
                    sensor_id     TEXT NOT NULL,
                    fan_ids_json  TEXT NOT NULL DEFAULT '[]',
                    target_temp_c REAL NOT NULL,
                    tolerance_c   REAL NOT NULL DEFAULT 5.0,
                    min_fan_speed REAL NOT NULL DEFAULT 20.0,
                    enabled       INTEGER NOT NULL DEFAULT 1,
                    pid_mode      INTEGER NOT NULL DEFAULT 0,
                    pid_kp        REAL    NOT NULL DEFAULT 5.0,
                    pid_ki        REAL    NOT NULL DEFAULT 0.05,
                    pid_kd        REAL    NOT NULL DEFAULT 1.0,
                    created_at    TEXT NOT NULL DEFAULT (datetime('now')),
                    updated_at    TEXT NOT NULL DEFAULT (datetime('now'))
                );
                CREATE INDEX IF NOT EXISTS idx_temp_targets_sensor ON temperature_targets (sensor_id);

                CREATE TABLE IF NOT EXISTS virtual_sensors (
                    id               TEXT PRIMARY KEY,
                    name             TEXT NOT NULL,
                    type             TEXT NOT NULL DEFAULT 'max',
                    source_ids_json  TEXT NOT NULL DEFAULT '[]',
                    weights_json     TEXT,
                    window_seconds   REAL,
                    "offset"         REAL NOT NULL DEFAULT 0.0,
                    enabled          INTEGER NOT NULL DEFAULT 1,
                    created_at       TEXT NOT NULL DEFAULT (datetime('now')),
                    updated_at       TEXT NOT NULL DEFAULT (datetime('now'))
                );

                CREATE TABLE IF NOT EXISTS notification_channels (
                    id           TEXT PRIMARY KEY,
                    type         TEXT NOT NULL,
                    name         TEXT NOT NULL DEFAULT '',
                    enabled      INTEGER NOT NULL DEFAULT 1,
                    config_json  TEXT NOT NULL DEFAULT '{}',
                    created_at   TEXT NOT NULL DEFAULT (datetime('now')),
                    updated_at   TEXT NOT NULL DEFAULT (datetime('now'))
                );

                CREATE TABLE IF NOT EXISTS profile_schedules (
                    id           TEXT PRIMARY KEY,
                    profile_id   TEXT NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
                    start_time   TEXT NOT NULL,
                    end_time     TEXT NOT NULL,
                    days_of_week TEXT NOT NULL,
                    timezone     TEXT NOT NULL DEFAULT 'UTC',
                    enabled      INTEGER NOT NULL DEFAULT 1,
                    created_at   TEXT NOT NULL DEFAULT (datetime('now'))
                );

                CREATE TABLE IF NOT EXISTS noise_profiles (
                    id         TEXT PRIMARY KEY,
                    fan_id     TEXT NOT NULL,
                    mode       TEXT NOT NULL CHECK(mode IN ('quick', 'precise')),
                    data_json  TEXT NOT NULL,
                    created_at TEXT NOT NULL DEFAULT (datetime('now')),
                    updated_at TEXT NOT NULL DEFAULT (datetime('now'))
                );

                CREATE TABLE IF NOT EXISTS report_schedules (
                    id                    TEXT PRIMARY KEY,
                    frequency             TEXT NOT NULL CHECK(frequency IN ('daily', 'weekly')),
                    time_utc              TEXT NOT NULL,
                    timezone              TEXT NOT NULL DEFAULT 'UTC',
                    enabled               INTEGER NOT NULL DEFAULT 1,
                    last_sent_at          TEXT,
                    created_at            TEXT NOT NULL DEFAULT (datetime('now')),
                    last_error            TEXT,
                    last_attempted_at     TEXT,
                    consecutive_failures  INTEGER NOT NULL DEFAULT 0
                );

                CREATE TABLE IF NOT EXISTS event_log (
                    id            TEXT PRIMARY KEY,
                    event_type    TEXT NOT NULL,
                    timestamp_utc TEXT NOT NULL,
                    label         TEXT NOT NULL,
                    description   TEXT,
                    metadata_json TEXT,
                    created_at    TEXT NOT NULL DEFAULT (datetime('now'))
                );
                CREATE INDEX IF NOT EXISTS idx_event_log_type_ts ON event_log(event_type, timestamp_utc);

                CREATE TABLE IF NOT EXISTS settings (
                    key        TEXT PRIMARY KEY,
                    value      TEXT NOT NULL,
                    updated_at TEXT NOT NULL DEFAULT (datetime('now'))
                );
                INSERT OR IGNORE INTO settings (key, value) VALUES
                    ('drive_monitoring_enabled',        '1'),
                    ('drive_native_provider_enabled',   '1'),
                    ('drive_smartctl_provider_enabled', '1'),
                    ('drive_smartctl_path',             'smartctl'),
                    ('drive_fast_poll_seconds',         '15'),
                    ('drive_health_poll_seconds',       '300'),
                    ('drive_rescan_poll_seconds',       '900'),
                    ('drive_hdd_temp_warning_c',        '45'),
                    ('drive_hdd_temp_critical_c',       '50'),
                    ('drive_ssd_temp_warning_c',        '55'),
                    ('drive_ssd_temp_critical_c',       '65'),
                    ('drive_nvme_temp_warning_c',       '65'),
                    ('drive_nvme_temp_critical_c',      '75'),
                    ('drive_wear_warning_percent_used', '80'),
                    ('drive_wear_critical_percent_used','90');

                CREATE TABLE IF NOT EXISTS profiles (
                    id          TEXT PRIMARY KEY,
                    name        TEXT NOT NULL,
                    description TEXT NOT NULL DEFAULT '',
                    is_active   INTEGER NOT NULL DEFAULT 0,
                    created_at  TEXT NOT NULL DEFAULT (datetime('now')),
                    updated_at  TEXT NOT NULL DEFAULT (datetime('now'))
                );

                CREATE TABLE IF NOT EXISTS profile_curves (
                    id              TEXT PRIMARY KEY,
                    profile_id      TEXT NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
                    fan_id          TEXT NOT NULL DEFAULT '',
                    sensor_id       TEXT NOT NULL DEFAULT '',
                    enabled         INTEGER NOT NULL DEFAULT 1,
                    points_json     TEXT NOT NULL DEFAULT '[]',
                    sensor_ids_json TEXT NOT NULL DEFAULT '[]'
                );
                CREATE INDEX IF NOT EXISTS idx_profile_curves_profile ON profile_curves(profile_id);

                CREATE TABLE IF NOT EXISTS alert_rules (
                    id          TEXT PRIMARY KEY,
                    sensor_id   TEXT NOT NULL,
                    sensor_name TEXT NOT NULL DEFAULT '',
                    threshold   REAL NOT NULL,
                    condition   TEXT NOT NULL DEFAULT 'above',
                    message     TEXT NOT NULL DEFAULT '',
                    enabled     INTEGER NOT NULL DEFAULT 1,
                    action_json TEXT,
                    created_at  TEXT NOT NULL DEFAULT (datetime('now'))
                );
                """;
            await cmd.ExecuteNonQueryAsync(ct);

            // Run file-based migrations (replaces the old hardcoded ALTER TABLE loop).
            // MigrationRunner tracks applied versions in a schema_version table,
            // backs up before applying, and rolls back on failure.
            try
            {
                await MigrationRunner.RunAsync(_connStr, _settings.DbPath, ct);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Schema migration failed");
                throw;
            }

            _initialised = true;
        }
        finally { _initLock.Release(); }
    }

    // -----------------------------------------------------------------------
    // Write
    // -----------------------------------------------------------------------

    public async Task LogReadingsAsync(IReadOnlyList<SensorReading> readings,
        CancellationToken ct = default)
    {
        if (readings.Count == 0) return;
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var ts = DateTimeOffset.UtcNow.ToString("o");

        // Batch INSERT: build a single multi-row VALUES statement to avoid N round-trips.
        // SQLite supports up to 999 parameters; each row uses 6, so batch ≤166 rows.
        const int maxRowsPerBatch = 166;

        for (int offset = 0; offset < readings.Count; offset += maxRowsPerBatch)
        {
            var batch = readings.Skip(offset).Take(maxRowsPerBatch).ToList();
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)tx;

            var valueClauses = new List<string>(batch.Count);
            int paramIdx = 0;
            foreach (var r in batch)
            {
                var p = paramIdx;
                valueClauses.Add($"($p{p}_ts, $p{p}_sid, $p{p}_name, $p{p}_type, $p{p}_val, $p{p}_unit)");
                cmd.Parameters.AddWithValue($"$p{p}_ts",   ts);
                cmd.Parameters.AddWithValue($"$p{p}_sid",  r.Id);
                cmd.Parameters.AddWithValue($"$p{p}_name", r.Name);
                cmd.Parameters.AddWithValue($"$p{p}_type", r.SensorType);
                cmd.Parameters.AddWithValue($"$p{p}_val",  r.Value);
                cmd.Parameters.AddWithValue($"$p{p}_unit", r.Unit);
                paramIdx++;
            }

            cmd.CommandText = $"INSERT INTO sensor_log (timestamp, sensor_id, sensor_name, sensor_type, value, unit) VALUES {string.Join(", ", valueClauses)}";
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    // -----------------------------------------------------------------------
    // Read — history
    // -----------------------------------------------------------------------

    public async Task<List<SensorReading>> GetHistoryAsync(string sensorId,
        DateTimeOffset since, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT sensor_id, sensor_name, sensor_type, value, unit
            FROM sensor_log
            WHERE sensor_id = $sid AND timestamp >= $since
            ORDER BY timestamp ASC
            """;
        cmd.Parameters.AddWithValue("$sid",   sensorId);
        cmd.Parameters.AddWithValue("$since", since.ToString("o"));

        var results = new List<SensorReading>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new SensorReading
            {
                Id         = reader.GetString(0),
                Name       = reader.GetString(1),
                SensorType = reader.GetString(2),
                Value      = reader.GetDouble(3),
                Unit       = reader.GetString(4),
            });
        }
        return results;
    }

    // -----------------------------------------------------------------------
    // Export CSV
    // -----------------------------------------------------------------------

    public async Task<string> ExportCsvAsync(DateTimeOffset since, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT timestamp, sensor_id, sensor_name, sensor_type, value, unit
            FROM sensor_log
            WHERE timestamp >= $since
            ORDER BY timestamp ASC
            """;
        cmd.Parameters.AddWithValue("$since", since.ToString("o"));

        var sb = new StringBuilder("timestamp,sensor_id,sensor_name,sensor_type,value,unit\n");
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            sb.Append(CsvEscape(reader.GetString(0))).Append(',')
              .Append(CsvEscape(reader.GetString(1))).Append(',')
              .Append(CsvEscape(reader.GetString(2))).Append(',')
              .Append(CsvEscape(reader.GetString(3))).Append(',')
              .Append(reader.GetDouble(4)).Append(',')
              .Append(CsvEscape(reader.GetString(5))).Append('\n');
        }
        return sb.ToString();
    }

    // -----------------------------------------------------------------------
    // Fan settings
    // -----------------------------------------------------------------------

    public async Task<Dictionary<string, FanSettingsModel>> GetAllFanSettingsAsync(
        CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT fan_id, min_speed_pct, zero_rpm_capable FROM fan_settings";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var result = new Dictionary<string, FanSettingsModel>();
        while (await reader.ReadAsync(ct))
        {
            var fanId = reader.GetString(0);
            result[fanId] = new FanSettingsModel
            {
                FanId          = fanId,
                MinSpeedPct    = reader.GetDouble(1),
                ZeroRpmCapable = reader.GetInt32(2) != 0,
            };
        }
        return result;
    }

    public async Task<FanSettingsModel?> GetFanSettingAsync(string fanId,
        CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT min_speed_pct, zero_rpm_capable FROM fan_settings WHERE fan_id = $id";
        cmd.Parameters.AddWithValue("$id", fanId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new FanSettingsModel
        {
            FanId          = fanId,
            MinSpeedPct    = reader.GetDouble(0),
            ZeroRpmCapable = reader.GetInt32(1) != 0,
        };
    }

    public async Task SetFanSettingAsync(string fanId, double minSpeedPct, bool zeroRpmCapable,
        CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO fan_settings (fan_id, min_speed_pct, zero_rpm_capable, updated_at)
            VALUES ($id, $min, $zrpm, datetime('now'))
            ON CONFLICT(fan_id) DO UPDATE SET
                min_speed_pct    = excluded.min_speed_pct,
                zero_rpm_capable = excluded.zero_rpm_capable,
                updated_at       = excluded.updated_at
            """;
        cmd.Parameters.AddWithValue("$id",   fanId);
        cmd.Parameters.AddWithValue("$min",  minSpeedPct);
        cmd.Parameters.AddWithValue("$zrpm", zeroRpmCapable ? 1 : 0);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // -----------------------------------------------------------------------
    // Prune
    // -----------------------------------------------------------------------

    public async Task PruneAsync(int retentionDays, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays).ToString("o");
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM sensor_log WHERE timestamp < $cutoff";
        cmd.Parameters.AddWithValue("$cutoff", cutoff);
        await cmd.ExecuteNonQueryAsync(ct);

        // Also prune drive health snapshots
        await using var cmd2 = conn.CreateCommand();
        cmd2.CommandText = "DELETE FROM drive_health_snapshots WHERE recorded_at < $cutoff";
        cmd2.Parameters.AddWithValue("$cutoff", cutoff);
        await cmd2.ExecuteNonQueryAsync(ct);
    }

    // -----------------------------------------------------------------------
    // Analytics
    // -----------------------------------------------------------------------

    // Build a sensor_id filter condition and add the corresponding parameters.
    // Returns "1=1" (no filter) when sensorIds is null or empty.
    private static string BuildSensorCondition(string[]? sensorIds, SqliteCommand cmd)
    {
        if (sensorIds == null || sensorIds.Length == 0) return "1=1";
        var pnames = sensorIds.Select((_, i) => $"$sId{i}").ToArray();
        for (int i = 0; i < sensorIds.Length; i++)
            cmd.Parameters.AddWithValue($"$sId{i}", sensorIds[i]);
        return $"sensor_id IN ({string.Join(",", pnames)})";
    }

    public async Task<List<AnalyticsBucket>> GetAnalyticsHistoryAsync(
        DateTimeOffset start, DateTimeOffset end, string[]? sensorIds, int bucketSeconds,
        CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        bucketSeconds = Math.Clamp(bucketSeconds, 10, 86400);
        var startStr = start.ToString("o");
        var endStr   = end.ToString("o");

        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        var sensorCond = BuildSensorCondition(sensorIds, cmd);
        cmd.CommandText = $"""
            SELECT
              sensor_id,
              MAX(sensor_name) AS sensor_name,
              MAX(sensor_type) AS sensor_type,
              MAX(unit) AS unit,
              CAST(strftime('%s', timestamp) AS INTEGER) / $bucket AS bucket_epoch,
              AVG(CAST(value AS REAL)) AS avg_value,
              MIN(CAST(value AS REAL)) AS min_value,
              MAX(CAST(value AS REAL)) AS max_value,
              COUNT(*) AS sample_count
            FROM sensor_log
            WHERE timestamp >= $start AND timestamp <= $end
              AND {sensorCond}
            GROUP BY sensor_id, bucket_epoch
            ORDER BY bucket_epoch ASC
            """;
        cmd.Parameters.AddWithValue("$bucket", bucketSeconds);
        cmd.Parameters.AddWithValue("$start", startStr);
        cmd.Parameters.AddWithValue("$end",   endStr);

        var results = new List<AnalyticsBucket>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var bucketEpoch = reader.GetInt64(4);
            results.Add(new AnalyticsBucket
            {
                SensorId     = reader.GetString(0),
                SensorName   = reader.GetString(1),
                SensorType   = reader.GetString(2),
                Unit         = reader.GetString(3),
                TimestampUtc = DateTimeOffset.FromUnixTimeSeconds(bucketEpoch * bucketSeconds)
                                   .ToString("o"),
                AvgValue     = reader.GetDouble(5),
                MinValue     = reader.GetDouble(6),
                MaxValue     = reader.GetDouble(7),
                SampleCount  = reader.GetInt32(8),
            });
        }
        return results;
    }

    public async Task<(List<AnalyticsStat> Stats, DateTimeOffset? ActualStart, DateTimeOffset? ActualEnd)> GetAnalyticsStatsAsync(
        DateTimeOffset start, DateTimeOffset end, string[]? sensorIds,
        CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        var startStr = start.ToString("o");
        var endStr   = end.ToString("o");

        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);

        // Single-pass: fetch all values sorted by sensor_id + value, compute stats in memory.
        // This replaces two full table scans (aggregate + sorted values) with one.
        await using var cmd = conn.CreateCommand();
        var sensorCond = BuildSensorCondition(sensorIds, cmd);
        cmd.CommandText = $"""
            SELECT sensor_id, sensor_name, sensor_type, unit,
                   CAST(value AS REAL), timestamp
            FROM sensor_log
            WHERE timestamp >= $start AND timestamp <= $end AND {sensorCond}
            ORDER BY sensor_id, CAST(value AS REAL) ASC
            """;
        cmd.Parameters.AddWithValue("$start", startStr);
        cmd.Parameters.AddWithValue("$end",   endStr);

        // Accumulate per-sensor stats in a single pass over sorted results.
        var accum = new Dictionary<string, (string Name, string Type, string Unit,
            double Min, double Max, double Sum, int Count, List<double> Values,
            string TsMin, string TsMax)>();

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var sid  = reader.GetString(0);
            var val  = reader.GetDouble(4);
            var ts   = reader.GetString(5);

            if (!accum.TryGetValue(sid, out var acc))
            {
                acc = (reader.GetString(1), reader.GetString(2), reader.GetString(3),
                       val, val, 0.0, 0, new List<double>(), ts, ts);
            }

            acc.Min = Math.Min(acc.Min, val);
            acc.Max = Math.Max(acc.Max, val);
            acc.Sum += val;
            acc.Count++;
            acc.Values.Add(val); // already sorted by ORDER BY
            if (string.Compare(ts, acc.TsMin, StringComparison.Ordinal) < 0) acc.TsMin = ts;
            if (string.Compare(ts, acc.TsMax, StringComparison.Ordinal) > 0) acc.TsMax = ts;
            accum[sid] = acc;
        }

        var stats = new List<AnalyticsStat>();
        DateTimeOffset? actualStart = null, actualEnd = null;
        foreach (var (sid, acc) in accum)
        {
            var p95 = acc.Values.Count > 0
                ? acc.Values[Math.Min(acc.Values.Count - 1, (int)(acc.Values.Count * 0.95))]
                : acc.Sum / Math.Max(1, acc.Count);

            stats.Add(new AnalyticsStat
            {
                SensorId    = sid,
                SensorName  = acc.Name,
                SensorType  = acc.Type,
                Unit        = acc.Unit,
                MinValue    = acc.Min,
                MaxValue    = acc.Max,
                AvgValue    = acc.Sum / Math.Max(1, acc.Count),
                P95Value    = p95,
                SampleCount = acc.Count,
            });

            if (DateTimeOffset.TryParse(acc.TsMin, out var ts1) && (actualStart == null || ts1 < actualStart))
                actualStart = ts1;
            if (DateTimeOffset.TryParse(acc.TsMax, out var ts2) && (actualEnd == null || ts2 > actualEnd))
                actualEnd = ts2;
        }
        return (stats, actualStart, actualEnd);
    }

    public async Task<(List<AnalyticsAnomaly> Anomalies, DateTimeOffset? ActualStart, DateTimeOffset? ActualEnd)> GetAnalyticsAnomaliesAsync(
        DateTimeOffset start, DateTimeOffset end, string[]? sensorIds, double zScoreThreshold,
        CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        var startStr = start.ToString("o");
        var endStr   = end.ToString("o");

        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        var sensorCond = BuildSensorCondition(sensorIds, cmd);
        cmd.CommandText = $"""
            SELECT timestamp, sensor_id, MAX(sensor_name) AS sensor_name,
                   MAX(sensor_type), CAST(value AS REAL), MAX(unit)
            FROM sensor_log
            WHERE timestamp >= $start AND timestamp <= $end AND {sensorCond}
            GROUP BY timestamp, sensor_id
            ORDER BY timestamp ASC
            """;
        cmd.Parameters.AddWithValue("$start", startStr);
        cmd.Parameters.AddWithValue("$end",   endStr);

        var byId = new Dictionary<string, List<(string Ts, double V, string Name, string Type, string Unit)>>();
        string? tsMin = null, tsMax = null;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var ts  = reader.GetString(0);
            var sid = reader.GetString(1);
            if (!byId.TryGetValue(sid, out var list))
                byId[sid] = list = new();
            list.Add((ts, reader.GetDouble(4), reader.GetString(2), reader.GetString(3), reader.GetString(5)));
            if (tsMin == null || string.Compare(ts, tsMin, StringComparison.Ordinal) < 0) tsMin = ts;
            if (tsMax == null || string.Compare(ts, tsMax, StringComparison.Ordinal) > 0) tsMax = ts;
        }

        DateTimeOffset? actualStart = null, actualEnd = null;
        if (tsMin != null && DateTimeOffset.TryParse(tsMin, out var dtMin)) actualStart = dtMin;
        if (tsMax != null && DateTimeOffset.TryParse(tsMax, out var dtMax)) actualEnd   = dtMax;

        var anomalies = new List<AnalyticsAnomaly>();
        foreach (var (sid, rows) in byId)
        {
            if (rows.Count < 10) continue;
            double mean = rows.Average(r => r.V);
            double variance = rows.Average(r => (r.V - mean) * (r.V - mean));
            double stdev = Math.Sqrt(variance);
            if (stdev < 1e-9) continue;

            foreach (var row in rows)
            {
                var z = Math.Abs(row.V - mean) / stdev;
                if (z > zScoreThreshold)
                    anomalies.Add(new AnalyticsAnomaly
                    {
                        TimestampUtc = row.Ts,
                        SensorId     = sid,
                        SensorName   = row.Name,
                        Value        = row.V,
                        Unit         = row.Unit,
                        ZScore       = Math.Round(z, 2),
                        Mean         = Math.Round(mean, 2),
                        Stdev        = Math.Round(stdev, 2),
                        Severity     = z >= zScoreThreshold * 1.5 ? "critical" : "warning",
                    });
            }
        }
        anomalies.Sort((a, b) => b.ZScore.CompareTo(a.ZScore));
        return (anomalies, actualStart, actualEnd);
    }

    public async Task<(double Coefficient, int Samples, List<AnalyticsCorrelationSample> Points)>
        GetAnalyticsCorrelationAsync(
            string sensorX, string sensorY, DateTimeOffset start, DateTimeOffset end,
            CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        var startStr = start.ToString("o");
        var endStr   = end.ToString("o");

        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);

        async Task<Dictionary<long, double>> FetchMinuteBucketsAsync(string sid)
        {
            await using var c = conn.CreateCommand();
            c.CommandText = """
                SELECT CAST(strftime('%s', timestamp) AS INTEGER) / 60 AS bucket,
                       AVG(CAST(value AS REAL))
                FROM sensor_log
                WHERE timestamp >= $start AND timestamp <= $end AND sensor_id = $sid
                GROUP BY bucket
                """;
            c.Parameters.AddWithValue("$start", startStr);
            c.Parameters.AddWithValue("$end",   endStr);
            c.Parameters.AddWithValue("$sid",   sid);
            var d = new Dictionary<long, double>();
            await using var r = await c.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                d[r.GetInt64(0)] = r.GetDouble(1);
            return d;
        }

        var xBuckets = await FetchMinuteBucketsAsync(sensorX);
        var yBuckets = await FetchMinuteBucketsAsync(sensorY);

        var samples = new List<AnalyticsCorrelationSample>();
        foreach (var (epoch, xVal) in xBuckets)
            if (yBuckets.TryGetValue(epoch, out var yVal))
                samples.Add(new AnalyticsCorrelationSample { Epoch = epoch * 60, X = xVal, Y = yVal });
        samples.Sort((a, b) => a.Epoch.CompareTo(b.Epoch));

        if (samples.Count < 3)
            return (0.0, samples.Count, samples);

        double meanX = samples.Average(s => s.X);
        double meanY = samples.Average(s => s.Y);
        double num  = samples.Sum(s => (s.X - meanX) * (s.Y - meanY));
        double denX = Math.Sqrt(samples.Sum(s => (s.X - meanX) * (s.X - meanX)));
        double denY = Math.Sqrt(samples.Sum(s => (s.Y - meanY) * (s.Y - meanY)));
        double coeff = (denX < 1e-9 || denY < 1e-9) ? 0.0 : num / (denX * denY);

        return (Math.Round(coeff, 4), samples.Count, samples);
    }

    public async Task<(List<AnalyticsRegression> Regressions, bool LoadBandAware)> GetAnalyticsRegressionAsync(
        int baselineDays, double recentHours, double thresholdDelta,
        string[]? sensorIds = null,
        DateTimeOffset? recentSinceOverride = null,
        DateTimeOffset? baselineSinceOverride = null,
        DateTimeOffset? recentUntilOverride = null,
        CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        baselineDays   = Math.Clamp(baselineDays,   7,   90);
        recentHours    = Math.Clamp(recentHours,    1.0, 168.0);
        thresholdDelta = Math.Clamp(thresholdDelta, 1.0, 50.0);

        var baselineSince = (baselineSinceOverride ?? DateTimeOffset.UtcNow.AddDays(-baselineDays)).ToString("o");
        var recentSince   = (recentSinceOverride   ?? DateTimeOffset.UtcNow.AddHours(-recentHours)).ToString("o");
        var recentUntil   = recentUntilOverride?.ToString("o");  // null when no custom end
        var tempTypes     = new[] { "cpu_temp", "gpu_temp", "hdd_temp", "case_temp" };
        var inClause      = string.Join(",", tempTypes.Select((_, i) => $"$t{i}"));

        string sensorClause = "";
        if (sensorIds is { Length: > 0 })
            sensorClause = $"AND sensor_id IN ({string.Join(",", sensorIds.Select((_, i) => $"$s{i}"))})";

        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);

        // Check whether load sensors are present in the baseline period
        await using var loadCheckCmd = conn.CreateCommand();
        loadCheckCmd.CommandText = "SELECT COUNT(*) FROM sensor_log WHERE timestamp >= $since AND sensor_type IN ('cpu_load', 'gpu_load')";
        loadCheckCmd.Parameters.AddWithValue("$since", baselineSince);
        var loadCount = (long)(await loadCheckCmd.ExecuteScalarAsync(ct) ?? 0L);
        var loadBandAware = loadCount > 0;

        var regressions = new List<AnalyticsRegression>();

        if (loadBandAware)
        {
            // Per-minute temps bucketed by concurrent load band.
            // Both CTEs use the same $since so band assignment reflects actual
            // load during that minute.
            var bandSql = $"""
                WITH minute_load AS (
                    SELECT strftime('%Y-%m-%d %H:%M', timestamp) AS minute,
                           AVG(CAST(value AS REAL)) AS avg_load
                    FROM sensor_log
                    WHERE timestamp >= $since AND sensor_type IN ('cpu_load', 'gpu_load')
                    GROUP BY minute
                ),
                banded_temps AS (
                    SELECT sl.sensor_id,
                           MAX(sl.sensor_name) AS sensor_name,
                           strftime('%Y-%m-%d %H:%M', sl.timestamp) AS minute,
                           AVG(CAST(sl.value AS REAL)) AS avg_temp,
                           CASE
                               WHEN ml.avg_load < 25 THEN 'low'
                               WHEN ml.avg_load < 75 THEN 'medium'
                               ELSE 'high'
                           END AS load_band
                    FROM sensor_log sl
                    JOIN minute_load ml
                      ON strftime('%Y-%m-%d %H:%M', sl.timestamp) = ml.minute
                    WHERE sl.timestamp >= $since
                      AND sl.sensor_type IN ({inClause})
                      {sensorClause}
                    GROUP BY sl.sensor_id, minute
                )
                SELECT sensor_id, MAX(sensor_name), load_band,
                       AVG(avg_temp), COUNT(*)
                FROM banded_temps
                GROUP BY sensor_id, load_band
                """;

            async Task<Dictionary<(string SensorId, string Band), (string Name, double Avg, int Samples)>>
                FetchBandedAsync(string since, string? until = null)
            {
                await using var c = conn.CreateCommand();
                var untilClause = until != null ? " AND timestamp <= $until" : "";
                c.CommandText = bandSql.Replace("WHERE timestamp >= $since AND sensor_type",
                    $"WHERE timestamp >= $since{untilClause} AND sensor_type")
                    .Replace("WHERE sl.timestamp >= $since",
                    $"WHERE sl.timestamp >= $since{(until != null ? " AND sl.timestamp <= $until" : "")}");
                c.Parameters.AddWithValue("$since", since);
                if (until != null)
                    c.Parameters.AddWithValue("$until", until);
                for (int i = 0; i < tempTypes.Length; i++)
                    c.Parameters.AddWithValue($"$t{i}", tempTypes[i]);
                if (sensorIds is { Length: > 0 })
                    for (int i = 0; i < sensorIds.Length; i++)
                        c.Parameters.AddWithValue($"$s{i}", sensorIds[i]);
                var d = new Dictionary<(string, string), (string, double, int)>();
                await using var r = await c.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                    d[(r.GetString(0), r.GetString(2))] = (r.GetString(1), r.GetDouble(3), r.GetInt32(4));
                return d;
            }

            var baseline = await FetchBandedAsync(baselineSince);
            var recent   = await FetchBandedAsync(recentSince, recentUntil);

            foreach (var ((sid, band), r) in recent)
            {
                if (!baseline.TryGetValue((sid, band), out var b) || b.Samples < 10) continue;
                var delta = r.Avg - b.Avg;
                if (delta >= thresholdDelta)
                    regressions.Add(new AnalyticsRegression
                    {
                        SensorId    = sid,
                        SensorName  = r.Name,
                        BaselineAvg = Math.Round(b.Avg, 1),
                        RecentAvg   = Math.Round(r.Avg, 1),
                        Delta       = Math.Round(delta, 1),
                        Severity    = delta >= thresholdDelta * 2 ? "critical" : "warning",
                        LoadBand    = band,
                        Message     = $"{r.Name} is {delta:F1}°C hotter than its {baselineDays}-day {band}-load average",
                    });
            }
        }
        else
        {
            // Fallback: simple whole-period average comparison (no load data)
            async Task<Dictionary<string, (string Name, double Avg, int Samples)>> FetchAvgsAsync(string since, string? until = null)
            {
                await using var c = conn.CreateCommand();
                var untilClause = until != null ? " AND timestamp <= $until" : "";
                c.CommandText = $"""
                    SELECT sensor_id, MAX(sensor_name), AVG(CAST(value AS REAL)), COUNT(*)
                    FROM sensor_log
                    WHERE timestamp >= $since{untilClause} AND sensor_type IN ({inClause})
                      {sensorClause}
                    GROUP BY sensor_id
                    """;
                c.Parameters.AddWithValue("$since", since);
                if (until != null)
                    c.Parameters.AddWithValue("$until", until);
                for (int i = 0; i < tempTypes.Length; i++)
                    c.Parameters.AddWithValue($"$t{i}", tempTypes[i]);
                if (sensorIds is { Length: > 0 })
                    for (int i = 0; i < sensorIds.Length; i++)
                        c.Parameters.AddWithValue($"$s{i}", sensorIds[i]);
                var d = new Dictionary<string, (string, double, int)>();
                await using var r = await c.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                    d[r.GetString(0)] = (r.GetString(1), r.GetDouble(2), r.GetInt32(3));
                return d;
            }

            var baseline = await FetchAvgsAsync(baselineSince);
            var recent   = await FetchAvgsAsync(recentSince, recentUntil);

            foreach (var (sid, r) in recent)
            {
                if (!baseline.TryGetValue(sid, out var b) || b.Samples < 10) continue;
                var delta = r.Avg - b.Avg;
                if (delta >= thresholdDelta)
                    regressions.Add(new AnalyticsRegression
                    {
                        SensorId    = sid,
                        SensorName  = r.Name,
                        BaselineAvg = Math.Round(b.Avg, 1),
                        RecentAvg   = Math.Round(r.Avg, 1),
                        Delta       = Math.Round(delta, 1),
                        Severity    = delta >= thresholdDelta * 2 ? "critical" : "warning",
                        Message     = $"{r.Name} is {delta:F1}°C hotter than its {baselineDays}-day average",
                    });
            }
        }

        regressions.Sort((a, b) => b.Delta.CompareTo(a.Delta));
        return (regressions, loadBandAware);
    }

    // -----------------------------------------------------------------------
    // Sensor labels
    // -----------------------------------------------------------------------

    public async Task<Dictionary<string, string>> GetAllLabelsAsync(CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sensor_id, label FROM sensor_labels";
        var labels = new Dictionary<string, string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            labels[reader.GetString(0)] = reader.GetString(1);
        return labels;
    }

    public async Task SetLabelAsync(string sensorId, string label, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO sensor_labels (sensor_id, label, updated_at)
            VALUES ($sid, $label, $ts)
            """;
        cmd.Parameters.AddWithValue("$sid", sensorId);
        cmd.Parameters.AddWithValue("$label", label);
        cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToString("o"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> DeleteLabelAsync(string sensorId, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM sensor_labels WHERE sensor_id = $sid";
        cmd.Parameters.AddWithValue("$sid", sensorId);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    // -----------------------------------------------------------------------
    // -----------------------------------------------------------------------
    // Quiet hours
    // -----------------------------------------------------------------------

    public async Task<List<QuietHoursRule>> GetQuietHoursAsync(CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, day_of_week, start_time, end_time, profile_id, enabled FROM quiet_hours ORDER BY day_of_week, start_time";
        var rules = new List<QuietHoursRule>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rules.Add(new QuietHoursRule
            {
                Id = reader.GetInt32(0),
                DayOfWeek = reader.GetInt32(1),
                StartTime = reader.GetString(2),
                EndTime = reader.GetString(3),
                ProfileId = reader.GetString(4),
                Enabled = reader.GetInt32(5) != 0,
            });
        }
        return rules;
    }

    public async Task<int> CreateQuietHoursAsync(QuietHoursRule rule, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO quiet_hours (day_of_week, start_time, end_time, profile_id, enabled)
            VALUES ($dow, $start, $end, $pid, $en);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$dow", rule.DayOfWeek);
        cmd.Parameters.AddWithValue("$start", rule.StartTime);
        cmd.Parameters.AddWithValue("$end", rule.EndTime);
        cmd.Parameters.AddWithValue("$pid", rule.ProfileId);
        cmd.Parameters.AddWithValue("$en", rule.Enabled ? 1 : 0);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    public async Task<bool> UpdateQuietHoursAsync(int id, QuietHoursRule rule, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE quiet_hours SET day_of_week=$dow, start_time=$start, end_time=$end,
                profile_id=$pid, enabled=$en WHERE id=$id
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$dow", rule.DayOfWeek);
        cmd.Parameters.AddWithValue("$start", rule.StartTime);
        cmd.Parameters.AddWithValue("$end", rule.EndTime);
        cmd.Parameters.AddWithValue("$pid", rule.ProfileId);
        cmd.Parameters.AddWithValue("$en", rule.Enabled ? 1 : 0);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<bool> DeleteQuietHoursAsync(int id, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM quiet_hours WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    // -----------------------------------------------------------------------
    // Noise Profiles
    // -----------------------------------------------------------------------

    public async Task<List<NoiseProfile>> ListNoiseProfilesAsync(CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, fan_id, mode, data_json, created_at, updated_at
            FROM noise_profiles
            ORDER BY created_at DESC
            """;

        var profiles = new List<NoiseProfile>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            profiles.Add(ReadNoiseProfile(reader));
        return profiles;
    }

    public async Task<NoiseProfile?> GetNoiseProfileAsync(string id, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, fan_id, mode, data_json, created_at, updated_at
            FROM noise_profiles
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadNoiseProfile(reader) : null;
    }

    public async Task<NoiseProfile> CreateNoiseProfileAsync(NoiseProfile profile, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO noise_profiles (id, fan_id, mode, data_json, created_at, updated_at)
            VALUES ($id, $fanId, $mode, $dataJson, $createdAt, $updatedAt)
            """;
        cmd.Parameters.AddWithValue("$id", profile.Id);
        cmd.Parameters.AddWithValue("$fanId", profile.FanId);
        cmd.Parameters.AddWithValue("$mode", profile.Mode);
        cmd.Parameters.AddWithValue("$dataJson", System.Text.Json.JsonSerializer.Serialize(profile.Data));
        cmd.Parameters.AddWithValue("$createdAt", profile.CreatedAt);
        cmd.Parameters.AddWithValue("$updatedAt", profile.UpdatedAt);
        await cmd.ExecuteNonQueryAsync(ct);
        return profile;
    }

    public async Task<bool> DeleteNoiseProfileAsync(string id, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM noise_profiles WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    private static NoiseProfile ReadNoiseProfile(SqliteDataReader reader)
    {
        var dataJson = reader.IsDBNull(3) ? "[]" : reader.GetString(3);
        List<NoiseDataPoint> data;
        try
        {
            data = System.Text.Json.JsonSerializer.Deserialize<List<NoiseDataPoint>>(dataJson) ?? [];
        }
        catch
        {
            data = [];
        }

        return new NoiseProfile
        {
            Id = reader.GetString(0),
            FanId = reader.GetString(1),
            Mode = reader.GetString(2),
            Data = data,
            CreatedAt = reader.IsDBNull(4) ? "" : reader.GetString(4),
            UpdatedAt = reader.IsDBNull(5) ? "" : reader.GetString(5),
        };
    }

    // -----------------------------------------------------------------------
    // Profile Schedules
    // -----------------------------------------------------------------------

    public async Task<List<ProfileScheduleRecord>> GetProfileSchedulesAsync(CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, profile_id, start_time, end_time, days_of_week, timezone, enabled, created_at FROM profile_schedules ORDER BY created_at";
        var list = new List<ProfileScheduleRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new ProfileScheduleRecord
            {
                Id         = reader.GetString(0),
                ProfileId  = reader.GetString(1),
                StartTime  = reader.GetString(2),
                EndTime    = reader.GetString(3),
                DaysOfWeek = reader.GetString(4),
                Timezone   = reader.GetString(5),
                Enabled    = reader.GetInt32(6) != 0,
                CreatedAt  = reader.GetString(7),
            });
        }
        return list;
    }

    public async Task CreateProfileScheduleAsync(ProfileScheduleRecord schedule, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO profile_schedules (id, profile_id, start_time, end_time, days_of_week, timezone, enabled, created_at)
            VALUES ($id, $pid, $start, $end, $days, $tz, $en, $cat)
            """;
        cmd.Parameters.AddWithValue("$id", schedule.Id);
        cmd.Parameters.AddWithValue("$pid", schedule.ProfileId);
        cmd.Parameters.AddWithValue("$start", schedule.StartTime);
        cmd.Parameters.AddWithValue("$end", schedule.EndTime);
        cmd.Parameters.AddWithValue("$days", schedule.DaysOfWeek);
        cmd.Parameters.AddWithValue("$tz", schedule.Timezone);
        cmd.Parameters.AddWithValue("$en", schedule.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$cat", schedule.CreatedAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> UpdateProfileScheduleAsync(string id, ProfileScheduleRecord schedule, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE profile_schedules SET profile_id=$pid, start_time=$start, end_time=$end,
                days_of_week=$days, timezone=$tz, enabled=$en WHERE id=$id
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$pid", schedule.ProfileId);
        cmd.Parameters.AddWithValue("$start", schedule.StartTime);
        cmd.Parameters.AddWithValue("$end", schedule.EndTime);
        cmd.Parameters.AddWithValue("$days", schedule.DaysOfWeek);
        cmd.Parameters.AddWithValue("$tz", schedule.Timezone);
        cmd.Parameters.AddWithValue("$en", schedule.Enabled ? 1 : 0);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<bool> DeleteProfileScheduleAsync(string id, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM profile_schedules WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    // -----------------------------------------------------------------------
    // Report Schedules
    // -----------------------------------------------------------------------

    public async Task<List<ReportScheduleRecord>> ListReportSchedulesAsync(CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, frequency, time_utc, timezone, enabled, last_sent_at, created_at,
                   last_error, last_attempted_at, consecutive_failures
            FROM report_schedules
            ORDER BY created_at ASC
            """;

        var schedules = new List<ReportScheduleRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            schedules.Add(ReadReportSchedule(reader));
        return schedules;
    }

    public async Task<ReportScheduleRecord?> GetReportScheduleAsync(string id, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, frequency, time_utc, timezone, enabled, last_sent_at, created_at,
                   last_error, last_attempted_at, consecutive_failures
            FROM report_schedules
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadReportSchedule(reader) : null;
    }

    public async Task<ReportScheduleRecord> CreateReportScheduleAsync(ReportScheduleRecord schedule, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO report_schedules (id, frequency, time_utc, timezone, enabled, last_sent_at, created_at)
            VALUES ($id, $frequency, $timeUtc, $timezone, $enabled, $lastSentAt, $createdAt)
            """;
        cmd.Parameters.AddWithValue("$id", schedule.Id);
        cmd.Parameters.AddWithValue("$frequency", schedule.Frequency);
        cmd.Parameters.AddWithValue("$timeUtc", schedule.TimeUtc);
        cmd.Parameters.AddWithValue("$timezone", schedule.Timezone);
        cmd.Parameters.AddWithValue("$enabled", schedule.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$lastSentAt", (object?)schedule.LastSentAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$createdAt", schedule.CreatedAt);
        await cmd.ExecuteNonQueryAsync(ct);
        return schedule;
    }

    public async Task<ReportScheduleRecord?> UpdateReportScheduleAsync(
        string id,
        string? frequency = null,
        string? timeUtc = null,
        string? timezone = null,
        bool? enabled = null,
        CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        var parts = new List<string>();
        var parms = new List<SqliteParameter> { new("$id", id) };
        if (frequency is not null) { parts.Add("frequency = $frequency"); parms.Add(new("$frequency", frequency)); }
        if (timeUtc is not null) { parts.Add("time_utc = $timeUtc"); parms.Add(new("$timeUtc", timeUtc)); }
        if (timezone is not null) { parts.Add("timezone = $timezone"); parms.Add(new("$timezone", timezone)); }
        if (enabled.HasValue) { parts.Add("enabled = $enabled"); parms.Add(new("$enabled", enabled.Value ? 1 : 0)); }
        if (parts.Count == 0)
            return await GetReportScheduleAsync(id, ct); // no fields to update — return current state

        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"UPDATE report_schedules SET {string.Join(", ", parts)} WHERE id = $id";
        foreach (var parm in parms)
            cmd.Parameters.Add(parm);

        var rows = await cmd.ExecuteNonQueryAsync(ct);
        return rows > 0 ? await GetReportScheduleAsync(id, ct) : null;
    }

    public async Task<bool> DeleteReportScheduleAsync(string id, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM report_schedules WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<bool> UpdateReportScheduleLastSentAsync(string id, string lastSentAt, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE report_schedules SET last_sent_at = $lastSentAt WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$lastSentAt", lastSentAt);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<bool> UpdateReportScheduleStatusAsync(
        string id,
        string? lastSentAt,
        string lastAttemptedAt,
        string? lastError,
        int consecutiveFailures,
        CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE report_schedules
            SET last_sent_at = COALESCE($lastSentAt, last_sent_at),
                last_attempted_at = $lastAttemptedAt,
                last_error = $lastError,
                consecutive_failures = $consecutiveFailures
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$lastSentAt", (object?)lastSentAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$lastAttemptedAt", lastAttemptedAt);
        cmd.Parameters.AddWithValue("$lastError", (object?)lastError ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$consecutiveFailures", consecutiveFailures);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    private static ReportScheduleRecord ReadReportSchedule(SqliteDataReader reader)
    {
        return new ReportScheduleRecord
        {
            Id = reader.GetString(0),
            Frequency = reader.GetString(1),
            TimeUtc = reader.GetString(2),
            Timezone = reader.GetString(3),
            Enabled = reader.GetInt32(4) != 0,
            LastSentAt = reader.IsDBNull(5) ? null : reader.GetString(5),
            CreatedAt = reader.GetString(6),
            LastError = reader.IsDBNull(7) ? null : reader.GetString(7),
            LastAttemptedAt = reader.IsDBNull(8) ? null : reader.GetString(8),
            ConsecutiveFailures = reader.IsDBNull(9) ? 0 : reader.GetInt32(9),
        };
    }

    // -----------------------------------------------------------------------
    // Event Annotations
    // -----------------------------------------------------------------------

    public async Task<List<AnnotationRecord>> ListAnnotationsAsync(string? start = null, string? end = null, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();

        var clauses = new List<string> { "event_type = 'annotation'" };
        if (!string.IsNullOrWhiteSpace(start))
        {
            clauses.Add("timestamp_utc >= $start");
            cmd.Parameters.AddWithValue("$start", start);
        }
        if (!string.IsNullOrWhiteSpace(end))
        {
            clauses.Add("timestamp_utc <= $end");
            cmd.Parameters.AddWithValue("$end", end);
        }

        cmd.CommandText = $"""
            SELECT id, event_type, timestamp_utc, label, description, metadata_json, created_at
            FROM event_log
            WHERE {string.Join(" AND ", clauses)}
            ORDER BY timestamp_utc DESC
            """;

        var annotations = new List<AnnotationRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            annotations.Add(ReadAnnotation(reader));
        return annotations;
    }

    public async Task<AnnotationRecord> CreateAnnotationAsync(AnnotationRecord annotation, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO event_log (id, event_type, timestamp_utc, label, description, metadata_json, created_at)
            VALUES ($id, $eventType, $timestampUtc, $label, $description, $metadataJson, $createdAt)
            """;
        cmd.Parameters.AddWithValue("$id", annotation.Id);
        cmd.Parameters.AddWithValue("$eventType", annotation.EventType);
        cmd.Parameters.AddWithValue("$timestampUtc", annotation.TimestampUtc);
        cmd.Parameters.AddWithValue("$label", annotation.Label);
        cmd.Parameters.AddWithValue("$description", (object?)annotation.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$metadataJson", (object?)annotation.MetadataJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$createdAt", annotation.CreatedAt);
        await cmd.ExecuteNonQueryAsync(ct);
        return annotation;
    }

    public async Task<bool> DeleteAnnotationAsync(string id, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM event_log
            WHERE id = $id AND event_type = 'annotation'
            """;
        cmd.Parameters.AddWithValue("$id", id);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    private static AnnotationRecord ReadAnnotation(SqliteDataReader reader)
    {
        return new AnnotationRecord
        {
            Id = reader.GetString(0),
            EventType = reader.GetString(1),
            TimestampUtc = reader.GetString(2),
            Label = reader.GetString(3),
            Description = reader.IsDBNull(4) ? null : reader.GetString(4),
            MetadataJson = reader.IsDBNull(5) ? null : reader.GetString(5),
            CreatedAt = reader.GetString(6),
        };
    }

    // -----------------------------------------------------------------------
    // Machines
    // -----------------------------------------------------------------------

    public async Task<List<MachineRecord>> GetMachinesAsync(CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, base_url, api_key_hash, enabled, poll_interval_seconds, timeout_ms, status, last_seen_at, last_error, consecutive_failures, created_at, updated_at, snapshot_json, capabilities_json, last_command_at FROM machines ORDER BY created_at ASC";
        var results = new List<MachineRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(ReadMachine(reader));
        return results;
    }

    public async Task<MachineRecord?> GetMachineAsync(string id, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, base_url, api_key_hash, enabled, poll_interval_seconds, timeout_ms, status, last_seen_at, last_error, consecutive_failures, created_at, updated_at, snapshot_json, capabilities_json, last_command_at FROM machines WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return ReadMachine(reader);
    }

    public async Task<MachineRecord> CreateMachineAsync(MachineRecord machine, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        machine.Id = Guid.NewGuid().ToString();
        var now = DateTimeOffset.UtcNow.ToString("o");
        machine.CreatedAt = now;
        machine.UpdatedAt = now;

        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO machines (id, name, base_url, api_key_hash, enabled, poll_interval_seconds, timeout_ms, status, consecutive_failures, created_at, updated_at, capabilities_json)
            VALUES ($id, $name, $url, $key, $en, $poll, $timeout, $status, 0, $created, $updated, '[]')
            """;
        cmd.Parameters.AddWithValue("$id",      machine.Id);
        cmd.Parameters.AddWithValue("$name",    machine.Name);
        cmd.Parameters.AddWithValue("$url",     machine.BaseUrl);
        cmd.Parameters.AddWithValue("$key",     (object?)machine.ApiKeyHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$en",      machine.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$poll",    machine.PollIntervalSeconds);
        cmd.Parameters.AddWithValue("$timeout", machine.TimeoutMs);
        cmd.Parameters.AddWithValue("$status",  machine.Status);
        cmd.Parameters.AddWithValue("$created", now);
        cmd.Parameters.AddWithValue("$updated", now);
        await cmd.ExecuteNonQueryAsync(ct);
        return machine;
    }

    public async Task<bool> UpdateMachineAsync(string id, MachineRecord patch, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE machines SET name=$name, base_url=$url, api_key_hash=$key,
                enabled=$en, poll_interval_seconds=$poll, timeout_ms=$timeout,
                updated_at=$updated
            WHERE id=$id
            """;
        cmd.Parameters.AddWithValue("$id",      id);
        cmd.Parameters.AddWithValue("$name",    patch.Name);
        cmd.Parameters.AddWithValue("$url",     patch.BaseUrl);
        cmd.Parameters.AddWithValue("$key",     (object?)patch.ApiKeyHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$en",      patch.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$poll",    patch.PollIntervalSeconds);
        cmd.Parameters.AddWithValue("$timeout", patch.TimeoutMs);
        cmd.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("o"));
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<bool> DeleteMachineAsync(string id, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM machines WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task UpdateMachineStatusAsync(string id, string status, string? lastSeenAt,
        string? lastError, int consecutiveFailures, string? snapshotJson, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE machines SET status=$status, last_seen_at=$seen, last_error=$err,
                consecutive_failures=$fails, snapshot_json=$snap, updated_at=$updated
            WHERE id=$id
            """;
        cmd.Parameters.AddWithValue("$id",      id);
        cmd.Parameters.AddWithValue("$status",  status);
        cmd.Parameters.AddWithValue("$seen",    (object?)lastSeenAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$err",     (object?)lastError ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$fails",   consecutiveFailures);
        cmd.Parameters.AddWithValue("$snap",    (object?)snapshotJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("o"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static MachineRecord ReadMachine(Microsoft.Data.Sqlite.SqliteDataReader r)
    {
        return new MachineRecord
        {
            Id                  = r.GetString(0),
            Name                = r.GetString(1),
            BaseUrl             = r.GetString(2),
            ApiKeyHash          = r.IsDBNull(3) ? null : r.GetString(3),
            Enabled             = r.GetInt32(4) != 0,
            PollIntervalSeconds = r.GetDouble(5),
            TimeoutMs           = r.GetInt32(6),
            Status              = r.GetString(7),
            LastSeenAt          = r.IsDBNull(8) ? null : r.GetString(8),
            LastError           = r.IsDBNull(9) ? null : r.GetString(9),
            ConsecutiveFailures = r.GetInt32(10),
            CreatedAt           = r.GetString(11),
            UpdatedAt           = r.GetString(12),
            SnapshotJson        = r.IsDBNull(13) ? null : r.GetString(13),
            CapabilitiesJson    = r.GetString(14),
            LastCommandAt       = r.IsDBNull(15) ? null : r.GetString(15),
        };
    }

    // -----------------------------------------------------------------------
    // Email notification settings
    // -----------------------------------------------------------------------

    public async Task<EmailNotificationSettingsRecord> GetEmailSettingsAsync(CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT enabled, smtp_host, smtp_port, smtp_username, smtp_password, sender_address, recipient_list, use_tls, use_ssl, updated_at FROM email_notification_settings WHERE id = 1";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return new EmailNotificationSettingsRecord();

        return new EmailNotificationSettingsRecord
        {
            Enabled        = reader.GetInt32(0) != 0,
            SmtpHost       = reader.GetString(1),
            SmtpPort       = reader.GetInt32(2),
            SmtpUsername   = reader.GetString(3),
            SmtpPassword   = reader.GetString(4),
            SenderAddress  = reader.GetString(5),
            RecipientList  = reader.GetString(6),
            UseTls         = reader.GetInt32(7) != 0,
            UseSsl         = reader.GetInt32(8) != 0,
            UpdatedAt      = reader.GetString(9),
        };
    }

    public async Task UpdateEmailSettingsAsync(EmailNotificationSettingsRecord s, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);

        // Determine what to store for the password:
        // • If caller passed an empty string, preserve the existing stored value.
        // • Otherwise encrypt the new value (falls back to plaintext when no key).
        string storedPassword = "";
        bool preservePassword = string.IsNullOrEmpty(s.SmtpPassword);
        if (!preservePassword && string.IsNullOrEmpty(_settings.SecretKey))
        {
            _log.LogWarning(
                "DRIVECHILL_SECRET_KEY is not set — SMTP password will be stored in plaintext. " +
                "Set the environment variable to enable AES-256-GCM encryption at rest.");
            storedPassword = s.SmtpPassword;
        }
        else if (!preservePassword)
        {
            storedPassword = CredentialEncryption.Encrypt(s.SmtpPassword, _settings.SecretKey);
        }

        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = preservePassword
            ? """
            UPDATE email_notification_settings SET enabled=$en, smtp_host=$host, smtp_port=$port,
                smtp_username=$user, sender_address=$sender,
                recipient_list=$recipients, use_tls=$tls, use_ssl=$ssl,
                updated_at=datetime('now')
            WHERE id = 1
            """
            : """
            UPDATE email_notification_settings SET enabled=$en, smtp_host=$host, smtp_port=$port,
                smtp_username=$user, smtp_password=$pass, sender_address=$sender,
                recipient_list=$recipients, use_tls=$tls, use_ssl=$ssl,
                updated_at=datetime('now')
            WHERE id = 1
            """;
        cmd.Parameters.AddWithValue("$en",         s.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$host",       s.SmtpHost);
        cmd.Parameters.AddWithValue("$port",       s.SmtpPort);
        cmd.Parameters.AddWithValue("$user",       s.SmtpUsername);
        if (!preservePassword) cmd.Parameters.AddWithValue("$pass", storedPassword);
        cmd.Parameters.AddWithValue("$sender",     s.SenderAddress);
        cmd.Parameters.AddWithValue("$recipients", s.RecipientList);
        cmd.Parameters.AddWithValue("$tls",        s.UseTls ? 1 : 0);
        cmd.Parameters.AddWithValue("$ssl",        s.UseSsl ? 1 : 0);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Returns the decrypted SMTP password for internal use (sending mail).
    /// Auto-migrates legacy plaintext rows to encrypted form when a key is configured.
    /// Never call this from API response paths — use GetEmailSettingsAsync + has_password instead.
    /// </summary>
    public async Task<string> GetSmtpPasswordAsync(CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT smtp_password FROM email_notification_settings WHERE id = 1";
        var stored = (await cmd.ExecuteScalarAsync(ct) as string) ?? "";

        if (string.IsNullOrEmpty(stored))
            return "";

        if (CredentialEncryption.IsEncrypted(stored))
            return CredentialEncryption.Decrypt(stored, _settings.SecretKey);

        // Legacy plaintext — migrate in place when key is available
        if (!string.IsNullOrEmpty(_settings.SecretKey))
        {
            var encrypted = CredentialEncryption.Encrypt(stored, _settings.SecretKey);
            await using var upd = conn.CreateCommand();
            upd.CommandText = "UPDATE email_notification_settings SET smtp_password=$p WHERE id = 1";
            upd.Parameters.AddWithValue("$p", encrypted);
            await upd.ExecuteNonQueryAsync(ct);
        }

        return stored;
    }

    // -----------------------------------------------------------------------
    // Auth — users and sessions
    // -----------------------------------------------------------------------

    public async Task<bool> UserExistsAsync(CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM users";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) > 0;
    }

    public async Task CreateUserAsync(string username, string passwordHash, string role = "admin", CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO users (username, password_hash, role, created_at) VALUES ($u, $h, $r, $t)";
        cmd.Parameters.AddWithValue("$u", username);
        cmd.Parameters.AddWithValue("$h", passwordHash);
        cmd.Parameters.AddWithValue("$r", role);
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("o"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<(string Username, string PasswordHash)?> GetUserAsync(string username, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT username, password_hash FROM users WHERE username = $u";
        cmd.Parameters.AddWithValue("$u", username);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return (reader.GetString(0), reader.GetString(1));
    }

    public async Task<List<UserRecord>> ListUsersAsync(CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, username, COALESCE(role,'admin'), created_at FROM users ORDER BY id";
        var results = new List<UserRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(new UserRecord(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        return results;
    }

    public async Task<UserRecord?> GetUserByIdAsync(long userId, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, username, COALESCE(role,'admin'), created_at FROM users WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", userId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new UserRecord(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3));
    }

    public async Task<bool> SetUserRoleAsync(long userId, string role, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE users SET role = $role WHERE id = $id";
        cmd.Parameters.AddWithValue("$role", role);
        cmd.Parameters.AddWithValue("$id", userId);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<bool> SetUserPasswordAsync(long userId, string passwordHash, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE users SET password_hash = $h WHERE id = $id";
        cmd.Parameters.AddWithValue("$h", passwordHash);
        cmd.Parameters.AddWithValue("$id", userId);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    /// <summary>Update password for a user identified by username — used by self-password-change.</summary>
    public async Task SetUserPasswordByUsernameAsync(string username, string passwordHash, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE users SET password_hash = $h WHERE username = $u";
        cmd.Parameters.AddWithValue("$h", passwordHash);
        cmd.Parameters.AddWithValue("$u", username);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Delete all sessions for a user by username — called after password change (GAP-2).</summary>
    public async Task DeleteUserSessionsByUsernameAsync(string username, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM sessions WHERE username = $u";
        cmd.Parameters.AddWithValue("$u", username);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> CountAdminUsersAsync(CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM users WHERE COALESCE(role,'admin') = 'admin'";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    public async Task<bool> DeleteUserAsync(long userId, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var txn = await conn.BeginTransactionAsync(ct);

        // Resolve username first so we can invalidate their sessions.
        await using var getUserCmd = conn.CreateCommand();
        getUserCmd.CommandText = "SELECT username FROM users WHERE id = $id";
        getUserCmd.Parameters.AddWithValue("$id", userId);
        var username = await getUserCmd.ExecuteScalarAsync(ct) as string;
        if (username is null) return false;

        await using var deleteCmd = conn.CreateCommand();
        deleteCmd.CommandText = "DELETE FROM users WHERE id = $id";
        deleteCmd.Parameters.AddWithValue("$id", userId);
        var deleted = await deleteCmd.ExecuteNonQueryAsync(ct) > 0;

        if (deleted)
        {
            // Invalidate all active sessions belonging to the deleted user.
            await using var sessionCmd = conn.CreateCommand();
            sessionCmd.CommandText = "DELETE FROM sessions WHERE username = $u";
            sessionCmd.Parameters.AddWithValue("$u", username);
            await sessionCmd.ExecuteNonQueryAsync(ct);
        }

        await txn.CommitAsync(ct);
        return deleted;
    }

    public async Task CreateSessionAsync(string token, string csrfToken, string username,
        string? ip, string? userAgent, TimeSpan ttl, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sessions (token, csrf_token, username, ip, user_agent, created_at, expires_at)
            VALUES ($tok, $csrf, $u, $ip, $ua, $created, $expires)
            """;
        var now = DateTimeOffset.UtcNow;
        cmd.Parameters.AddWithValue("$tok", token);
        cmd.Parameters.AddWithValue("$csrf", csrfToken);
        cmd.Parameters.AddWithValue("$u", username);
        cmd.Parameters.AddWithValue("$ip", (object?)ip ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ua", (object?)userAgent ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$created", now.ToString("o"));
        cmd.Parameters.AddWithValue("$expires", now.Add(ttl).ToString("o"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<(string Username, string CsrfToken, string Role)?> ValidateSessionAsync(string token, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.username, s.csrf_token, u.role
            FROM sessions s
            INNER JOIN users u ON s.username = u.username
            WHERE s.token = $tok AND s.expires_at > $now
            """;
        cmd.Parameters.AddWithValue("$tok", token);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("o"));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return (reader.GetString(0), reader.GetString(1), reader.GetString(2));
    }

    public async Task DeleteSessionAsync(string token, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM sessions WHERE token = $tok";
        cmd.Parameters.AddWithValue("$tok", token);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // -----------------------------------------------------------------------
    // Push subscriptions
    // -----------------------------------------------------------------------

    public async Task<List<PushSubscriptionRecord>> GetAllPushSubscriptionsAsync(CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, endpoint, p256dh, auth_key, user_agent, created_at, last_used_at FROM push_subscriptions ORDER BY created_at ASC";
        var results = new List<PushSubscriptionRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(ReadPushSub(reader));
        return results;
    }

    public async Task<PushSubscriptionRecord?> GetPushSubscriptionAsync(string id, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, endpoint, p256dh, auth_key, user_agent, created_at, last_used_at FROM push_subscriptions WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return ReadPushSub(reader);
    }

    public async Task<PushSubscriptionRecord> CreatePushSubscriptionAsync(PushSubscriptionRecord sub, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);

        // Check for existing subscription with same endpoint (UNIQUE constraint).
        // Update keys if they changed (browser may regenerate them), then return.
        await using var check = conn.CreateCommand();
        check.CommandText = "SELECT id FROM push_subscriptions WHERE endpoint = $ep";
        check.Parameters.AddWithValue("$ep", sub.Endpoint);
        var existingId = await check.ExecuteScalarAsync(ct) as string;
        if (existingId is not null)
        {
            await using var upd = conn.CreateCommand();
            upd.CommandText = "UPDATE push_subscriptions SET p256dh = $p256, auth_key = $auth WHERE id = $id";
            upd.Parameters.AddWithValue("$p256", sub.P256dh ?? "");
            upd.Parameters.AddWithValue("$auth", sub.AuthKey ?? "");
            upd.Parameters.AddWithValue("$id", existingId);
            await upd.ExecuteNonQueryAsync(ct);
            return (await GetPushSubscriptionAsync(existingId, ct))!;
        }

        sub.Id        = Guid.NewGuid().ToString();
        sub.CreatedAt = DateTimeOffset.UtcNow.ToString("o");
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO push_subscriptions (id, endpoint, p256dh, auth_key, user_agent, created_at)
            VALUES ($id, $ep, $p256, $auth, $ua, $ts)
            """;
        cmd.Parameters.AddWithValue("$id",   sub.Id);
        cmd.Parameters.AddWithValue("$ep",   sub.Endpoint);
        cmd.Parameters.AddWithValue("$p256", sub.P256dh);
        cmd.Parameters.AddWithValue("$auth", sub.AuthKey);
        cmd.Parameters.AddWithValue("$ua",   (object?)sub.UserAgent ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ts",   sub.CreatedAt);
        try
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            // Another request inserted the same endpoint after our pre-check.
            await using var retry = conn.CreateCommand();
            retry.CommandText = "SELECT id, endpoint, p256dh, auth_key, user_agent, created_at, last_used_at FROM push_subscriptions WHERE endpoint = $ep";
            retry.Parameters.AddWithValue("$ep", sub.Endpoint);
            await using var retryReader = await retry.ExecuteReaderAsync(ct);
            if (await retryReader.ReadAsync(ct))
                return ReadPushSub(retryReader);
            throw;
        }
        return sub;
    }

    public async Task<bool> DeletePushSubscriptionAsync(string id, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM push_subscriptions WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task TouchPushSubscriptionAsync(string id, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE push_subscriptions SET last_used_at=$ts WHERE id = $id";
        cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static PushSubscriptionRecord ReadPushSub(SqliteDataReader r) => new()
    {
        Id         = r.GetString(0),
        Endpoint   = r.GetString(1),
        P256dh     = r.GetString(2),
        AuthKey    = r.GetString(3),
        UserAgent  = r.IsDBNull(4) ? null : r.GetString(4),
        CreatedAt  = r.GetString(5),
        LastUsedAt = r.IsDBNull(6) ? null : r.GetString(6),
    };

    // -----------------------------------------------------------------------
    // Machine last_command_at
    // -----------------------------------------------------------------------

    public async Task SetMachineLastCommandAsync(string id, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE machines SET last_command_at=$ts, updated_at=$ts WHERE id = $id";
        var now = DateTimeOffset.UtcNow.ToString("o");
        cmd.Parameters.AddWithValue("$ts", now);
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // -----------------------------------------------------------------------
    // Drive settings (key-value store)
    // -----------------------------------------------------------------------

    public async Task<string?> GetSettingAsync(string key, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = $key";
        cmd.Parameters.AddWithValue("$key", key);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result as string;
    }

    public async Task SetSettingAsync(string key, string value, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO settings (key, value, updated_at) VALUES ($key, $value, $now)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at
            """;
        cmd.Parameters.AddWithValue("$key",   key);
        cmd.Parameters.AddWithValue("$value", value);
        cmd.Parameters.AddWithValue("$now",   DateTimeOffset.UtcNow.ToString("o"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<DriveSettings> LoadDriveSettingsAsync(CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);

        // Batch-fetch all 15 drive_* keys in a single query instead of 15 round-trips.
        var keys = new[]
        {
            "drive_monitoring_enabled", "drive_native_provider_enabled", "drive_smartctl_provider_enabled",
            "drive_smartctl_path", "drive_fast_poll_seconds", "drive_health_poll_seconds",
            "drive_rescan_poll_seconds", "drive_hdd_temp_warning_c", "drive_hdd_temp_critical_c",
            "drive_ssd_temp_warning_c", "drive_ssd_temp_critical_c", "drive_nvme_temp_warning_c",
            "drive_nvme_temp_critical_c", "drive_wear_warning_percent_used", "drive_wear_critical_percent_used",
        };
        var placeholders = string.Join(", ", keys.Select((_, i) => $"$k{i}"));
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT key, value FROM settings WHERE key IN ({placeholders})";
        for (var i = 0; i < keys.Length; i++)
            cmd.Parameters.AddWithValue($"$k{i}", keys[i]);

        var dict = new Dictionary<string, string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var k = reader.GetString(0);
            if (!reader.IsDBNull(1))
                dict[k] = reader.GetString(1);
        }

        string? G(string k) => dict.TryGetValue(k, out var v) ? v : null;

        return new DriveChill.Models.DriveSettings
        {
            Enabled                  = G("drive_monitoring_enabled")        is "1" or null,
            NativeProviderEnabled    = G("drive_native_provider_enabled")   is "1" or null,
            SmartctlProviderEnabled  = G("drive_smartctl_provider_enabled") is "1" or null,
            SmartctlPath             = G("drive_smartctl_path")             ?? "smartctl",
            FastPollSeconds          = int.TryParse(G("drive_fast_poll_seconds"),    out var fps)  ? fps  : 15,
            HealthPollSeconds        = int.TryParse(G("drive_health_poll_seconds"),  out var hps)  ? hps  : 300,
            RescanPollSeconds        = int.TryParse(G("drive_rescan_poll_seconds"),  out var rps)  ? rps  : 900,
            HddTempWarningC          = double.TryParse(G("drive_hdd_temp_warning_c"),  out var hw)  ? hw  : 45.0,
            HddTempCriticalC         = double.TryParse(G("drive_hdd_temp_critical_c"), out var hc)  ? hc  : 50.0,
            SsdTempWarningC          = double.TryParse(G("drive_ssd_temp_warning_c"),  out var sw)  ? sw  : 55.0,
            SsdTempCriticalC         = double.TryParse(G("drive_ssd_temp_critical_c"), out var sc)  ? sc  : 65.0,
            NvmeTempWarningC         = double.TryParse(G("drive_nvme_temp_warning_c"), out var nw)  ? nw  : 65.0,
            NvmeTempCriticalC        = double.TryParse(G("drive_nvme_temp_critical_c"),out var nc)  ? nc  : 75.0,
            WearWarningPercentUsed   = double.TryParse(G("drive_wear_warning_percent_used"),  out var ww) ? ww : 80.0,
            WearCriticalPercentUsed  = double.TryParse(G("drive_wear_critical_percent_used"), out var wc) ? wc : 90.0,
        };
    }

    // -----------------------------------------------------------------------
    // Drive self-test runs
    // -----------------------------------------------------------------------

    public async Task<string> CreateSelfTestRunAsync(
        string driveId, string testType, string? providerRef, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        var runId = Guid.NewGuid().ToString("N")[..16];
        var now = DateTimeOffset.UtcNow.ToString("o");
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO drive_self_test_runs (id, drive_id, type, status, started_at, provider_run_ref)
            VALUES ($id, $did, $type, 'running', $now, $ref)
            """;
        cmd.Parameters.AddWithValue("$id",  runId);
        cmd.Parameters.AddWithValue("$did", driveId);
        cmd.Parameters.AddWithValue("$type", testType);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.Parameters.AddWithValue("$ref", (object?)providerRef ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
        return runId;
    }

    public async Task UpdateSelfTestRunAsync(
        string runId, string status, double? progress = null,
        string? failure = null, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        var now = DateTimeOffset.UtcNow.ToString("o");
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE drive_self_test_runs
            SET status=$status, progress_percent=$prog, failure_message=$fail,
                finished_at=CASE WHEN $status IN ('passed','failed','aborted') THEN $now ELSE finished_at END
            WHERE id=$id
            """;
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$prog",   (object?)progress ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$fail",   (object?)failure  ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now",    now);
        cmd.Parameters.AddWithValue("$id",     runId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<Dictionary<string, object?>>> GetSelfTestRunsAsync(
        string driveId, int limit = 10, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, drive_id, type, status, progress_percent,
                   started_at, finished_at, failure_message, provider_run_ref
            FROM drive_self_test_runs WHERE drive_id = $did
            ORDER BY started_at DESC LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$did",   driveId);
        cmd.Parameters.AddWithValue("$limit", limit);
        var result = new List<Dictionary<string, object?>>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new Dictionary<string, object?>
            {
                ["id"]              = reader.GetString(0),
                ["drive_id"]        = reader.GetString(1),
                ["type"]            = reader.GetString(2),
                ["status"]          = reader.GetString(3),
                ["progress_percent"]= reader.IsDBNull(4) ? (object?)null : reader.GetDouble(4),
                ["started_at"]      = reader.GetString(5),
                ["finished_at"]     = reader.IsDBNull(6) ? null : reader.GetString(6),
                ["failure_message"] = reader.IsDBNull(7) ? null : reader.GetString(7),
                ["provider_run_ref"]= reader.IsDBNull(8) ? null : reader.GetString(8),
            });
        }
        return result;
    }

    public async Task<Dictionary<string, object?>?> GetSelfTestRunAsync(
        string runId, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, drive_id, type, status, progress_percent,
                   started_at, finished_at, failure_message, provider_run_ref
            FROM drive_self_test_runs WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", runId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new Dictionary<string, object?>
        {
            ["id"]               = reader.GetString(0),
            ["drive_id"]         = reader.GetString(1),
            ["type"]             = reader.GetString(2),
            ["status"]           = reader.GetString(3),
            ["progress_percent"] = reader.IsDBNull(4) ? (object?)null : reader.GetDouble(4),
            ["started_at"]       = reader.GetString(5),
            ["finished_at"]      = reader.IsDBNull(6) ? null : reader.GetString(6),
            ["failure_message"]  = reader.IsDBNull(7) ? null : reader.GetString(7),
            ["provider_run_ref"] = reader.IsDBNull(8) ? null : reader.GetString(8),
        };
    }

    public async Task<List<Dictionary<string, object?>>> GetRunningSelfTestsAsync(
        CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, drive_id, type, status, progress_percent,
                   started_at, finished_at, failure_message, provider_run_ref
            FROM drive_self_test_runs WHERE status = 'running'
            """;
        var result = new List<Dictionary<string, object?>>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new Dictionary<string, object?>
            {
                ["id"]               = reader.GetString(0),
                ["drive_id"]         = reader.GetString(1),
                ["type"]             = reader.GetString(2),
                ["status"]           = reader.GetString(3),
                ["progress_percent"] = reader.IsDBNull(4) ? (object?)null : reader.GetDouble(4),
                ["started_at"]       = reader.GetString(5),
                ["finished_at"]      = reader.IsDBNull(6) ? null : reader.GetString(6),
                ["failure_message"]  = reader.IsDBNull(7) ? null : reader.GetString(7),
                ["provider_run_ref"] = reader.IsDBNull(8) ? null : reader.GetString(8),
            });
        }
        return result;
    }

    public async Task<List<Dictionary<string, object?>>> GetHealthHistoryAsync(
        string driveId, double hours = 168.0, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT recorded_at, temperature_c, health_status, health_percent,
                   predicted_failure, wear_percent_used, reallocated_sectors,
                   pending_sectors, uncorrectable_errors, media_errors
            FROM drive_health_snapshots
            WHERE drive_id = $did
              AND recorded_at >= datetime('now', $offset || ' hours')
            ORDER BY recorded_at ASC
            """;
        cmd.Parameters.AddWithValue("$did",    driveId);
        cmd.Parameters.AddWithValue("$offset", $"-{hours:F4}");
        var result = new List<Dictionary<string, object?>>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new Dictionary<string, object?>
            {
                ["recorded_at"]          = reader.GetString(0),
                ["temperature_c"]        = reader.IsDBNull(1) ? (object?)null : reader.GetDouble(1),
                ["health_status"]        = reader.GetString(2),
                ["health_percent"]       = reader.IsDBNull(3) ? (object?)null : reader.GetDouble(3),
                ["predicted_failure"]    = reader.GetInt32(4) == 1,
                ["wear_percent_used"]    = reader.IsDBNull(5) ? (object?)null : reader.GetDouble(5),
                ["reallocated_sectors"]  = reader.IsDBNull(6) ? (object?)null : reader.GetInt64(6),
                ["pending_sectors"]      = reader.IsDBNull(7) ? (object?)null : reader.GetInt64(7),
                ["uncorrectable_errors"] = reader.IsDBNull(8) ? (object?)null : reader.GetInt64(8),
                ["media_errors"]         = reader.IsDBNull(9) ? (object?)null : reader.GetInt64(9),
            });
        }
        return result;
    }

    public async Task<Dictionary<string, object?>?> GetDriveSettingsOverrideAsync(
        string driveId, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT temp_warning_c, temp_critical_c, alerts_enabled, curve_picker_enabled, updated_at
            FROM drive_settings_overrides WHERE drive_id = $did
            """;
        cmd.Parameters.AddWithValue("$did", driveId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new Dictionary<string, object?>
        {
            ["drive_id"]             = driveId,
            ["temp_warning_c"]       = reader.IsDBNull(0) ? (object?)null : reader.GetDouble(0),
            ["temp_critical_c"]      = reader.IsDBNull(1) ? (object?)null : reader.GetDouble(1),
            ["alerts_enabled"]       = reader.IsDBNull(2) ? (object?)null : (bool?)(reader.GetInt32(2) == 1),
            ["curve_picker_enabled"] = reader.IsDBNull(3) ? (object?)null : (bool?)(reader.GetInt32(3) == 1),
            ["updated_at"]           = reader.GetString(4),
        };
    }

    public async Task UpsertDriveSettingsOverrideAsync(
        string driveId, DriveChill.Models.DriveSettingsOverride o, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO drive_settings_overrides
                (drive_id, temp_warning_c, temp_critical_c, alerts_enabled, curve_picker_enabled, updated_at)
            VALUES ($did, $warn, $crit, $alert, $curve, $now)
            ON CONFLICT(drive_id) DO UPDATE SET
                temp_warning_c=COALESCE(excluded.temp_warning_c, temp_warning_c),
                temp_critical_c=COALESCE(excluded.temp_critical_c, temp_critical_c),
                alerts_enabled=COALESCE(excluded.alerts_enabled, alerts_enabled),
                curve_picker_enabled=COALESCE(excluded.curve_picker_enabled, curve_picker_enabled),
                updated_at=excluded.updated_at
            """;
        cmd.Parameters.AddWithValue("$did",   driveId);
        cmd.Parameters.AddWithValue("$warn",  (object?)o.TempWarningC ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$crit",  (object?)o.TempCriticalC ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$alert", o.AlertsEnabled.HasValue ? (object)(o.AlertsEnabled.Value ? 1 : 0) : DBNull.Value);
        cmd.Parameters.AddWithValue("$curve", o.CurvePickerEnabled.HasValue ? (object)(o.CurvePickerEnabled.Value ? 1 : 0) : DBNull.Value);
        cmd.Parameters.AddWithValue("$now",   DateTimeOffset.UtcNow.ToString("o"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // -----------------------------------------------------------------------
    // Virtual sensors CRUD
    // -----------------------------------------------------------------------

    public async Task<List<VirtualSensor>> GetVirtualSensorsAsync(CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, name, type, source_ids_json, weights_json,
                   window_seconds, "offset", enabled, created_at, updated_at
            FROM virtual_sensors ORDER BY name
            """;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<VirtualSensor>();
        while (await reader.ReadAsync(ct))
        {
            list.Add(ReadVirtualSensor(reader));
        }
        return list;
    }

    public async Task CreateVirtualSensorAsync(VirtualSensor vs, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO virtual_sensors
            (id, name, type, source_ids_json, weights_json, window_seconds, "offset", enabled, created_at, updated_at)
            VALUES ($id, $name, $type, $sids, $weights, $window, $offset, $enabled, $cat, $uat)
            """;
        var now = DateTimeOffset.UtcNow.ToString("o");
        cmd.Parameters.AddWithValue("$id", vs.Id);
        cmd.Parameters.AddWithValue("$name", vs.Name);
        cmd.Parameters.AddWithValue("$type", vs.Type);
        cmd.Parameters.AddWithValue("$sids", System.Text.Json.JsonSerializer.Serialize(vs.SourceIds));
        cmd.Parameters.AddWithValue("$weights", vs.Weights != null
            ? (object)System.Text.Json.JsonSerializer.Serialize(vs.Weights)
            : DBNull.Value);
        cmd.Parameters.AddWithValue("$window", vs.WindowSeconds.HasValue ? (object)vs.WindowSeconds.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$offset", vs.Offset);
        cmd.Parameters.AddWithValue("$enabled", vs.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$cat", now);
        cmd.Parameters.AddWithValue("$uat", now);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> UpdateVirtualSensorAsync(VirtualSensor vs, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE virtual_sensors SET name=$name, type=$type, source_ids_json=$sids,
            weights_json=$weights, window_seconds=$window, "offset"=$offset,
            enabled=$enabled, updated_at=$uat WHERE id=$id
            """;
        cmd.Parameters.AddWithValue("$id", vs.Id);
        cmd.Parameters.AddWithValue("$name", vs.Name);
        cmd.Parameters.AddWithValue("$type", vs.Type);
        cmd.Parameters.AddWithValue("$sids", System.Text.Json.JsonSerializer.Serialize(vs.SourceIds));
        cmd.Parameters.AddWithValue("$weights", vs.Weights != null
            ? (object)System.Text.Json.JsonSerializer.Serialize(vs.Weights)
            : DBNull.Value);
        cmd.Parameters.AddWithValue("$window", vs.WindowSeconds.HasValue ? (object)vs.WindowSeconds.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$offset", vs.Offset);
        cmd.Parameters.AddWithValue("$enabled", vs.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$uat", DateTimeOffset.UtcNow.ToString("o"));
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<bool> DeleteVirtualSensorAsync(string id, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM virtual_sensors WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    private static VirtualSensor ReadVirtualSensor(SqliteDataReader reader)
    {
        var sidsJson = reader.IsDBNull(3) ? "[]" : reader.GetString(3);
        var weightsJson = reader.IsDBNull(4) ? null : reader.GetString(4);
        return new VirtualSensor
        {
            Id = reader.GetString(0),
            Name = reader.GetString(1),
            Type = reader.GetString(2),
            SourceIds = System.Text.Json.JsonSerializer.Deserialize<List<string>>(sidsJson) ?? new(),
            Weights = weightsJson != null
                ? System.Text.Json.JsonSerializer.Deserialize<List<double>>(weightsJson)
                : null,
            WindowSeconds = reader.IsDBNull(5) ? null : reader.GetDouble(5),
            Offset = reader.IsDBNull(6) ? 0.0 : reader.GetDouble(6),
            Enabled = !reader.IsDBNull(7) && reader.GetInt64(7) != 0,
            CreatedAt = reader.IsDBNull(8) ? null : reader.GetString(8),
            UpdatedAt = reader.IsDBNull(9) ? null : reader.GetString(9),
        };
    }

    // -----------------------------------------------------------------------

    public void Dispose() { _initLock.Dispose(); }

    /// <summary>RFC 4180 CSV field escaping: quote fields containing comma, quote, or newline.</summary>
    private static string CsvEscape(string field)
    {
        if (field.Contains(',') || field.Contains('"') || field.Contains('\n') || field.Contains('\r'))
            return $"\"{field.Replace("\"", "\"\"")}\"";
        return field;
    }

    // -----------------------------------------------------------------------
    // Temperature targets
    // -----------------------------------------------------------------------

    public async Task<List<TemperatureTarget>> ListTemperatureTargetsAsync(CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, name, drive_id, sensor_id, fan_ids_json,
                   target_temp_c, tolerance_c, min_fan_speed, enabled,
                   pid_mode, pid_kp, pid_ki, pid_kd
            FROM temperature_targets ORDER BY created_at
            """;
        var results = new List<TemperatureTarget>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(ReadTemperatureTarget(reader));
        return results;
    }

    public async Task<TemperatureTarget?> GetTemperatureTargetAsync(string targetId, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, name, drive_id, sensor_id, fan_ids_json,
                   target_temp_c, tolerance_c, min_fan_speed, enabled,
                   pid_mode, pid_kp, pid_ki, pid_kd
            FROM temperature_targets WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", targetId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadTemperatureTarget(reader) : null;
    }

    public async Task<TemperatureTarget> CreateTemperatureTargetAsync(TemperatureTarget target, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        var now = DateTimeOffset.UtcNow.ToString("o");
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO temperature_targets
                (id, name, drive_id, sensor_id, fan_ids_json,
                 target_temp_c, tolerance_c, min_fan_speed, enabled,
                 pid_mode, pid_kp, pid_ki, pid_kd, created_at, updated_at)
            VALUES ($id, $name, $driveId, $sensorId, $fanIds,
                    $targetTemp, $tolerance, $minSpeed, $enabled,
                    $pidMode, $pidKp, $pidKi, $pidKd, $now, $now)
            """;
        cmd.Parameters.AddWithValue("$id", target.Id);
        cmd.Parameters.AddWithValue("$name", target.Name);
        cmd.Parameters.AddWithValue("$driveId", (object?)target.DriveId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sensorId", target.SensorId);
        cmd.Parameters.AddWithValue("$fanIds", System.Text.Json.JsonSerializer.Serialize(target.FanIds));
        cmd.Parameters.AddWithValue("$targetTemp", target.TargetTempC);
        cmd.Parameters.AddWithValue("$tolerance", target.ToleranceC);
        cmd.Parameters.AddWithValue("$minSpeed", target.MinFanSpeed);
        cmd.Parameters.AddWithValue("$enabled", target.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$pidMode", target.PidMode ? 1 : 0);
        cmd.Parameters.AddWithValue("$pidKp", target.PidKp);
        cmd.Parameters.AddWithValue("$pidKi", target.PidKi);
        cmd.Parameters.AddWithValue("$pidKd", target.PidKd);
        cmd.Parameters.AddWithValue("$now", now);
        await cmd.ExecuteNonQueryAsync(ct);
        return target;
    }

    public async Task<TemperatureTarget?> UpdateTemperatureTargetAsync(
        string targetId, string name, string? driveId, string sensorId,
        string[] fanIds, double targetTempC, double toleranceC, double minFanSpeed,
        bool pidMode, double pidKp, double pidKi, double pidKd,
        CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        var now = DateTimeOffset.UtcNow.ToString("o");
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE temperature_targets SET
                name = $name, drive_id = $driveId, sensor_id = $sensorId,
                fan_ids_json = $fanIds, target_temp_c = $targetTemp,
                tolerance_c = $tolerance, min_fan_speed = $minSpeed,
                pid_mode = $pidMode, pid_kp = $pidKp, pid_ki = $pidKi, pid_kd = $pidKd,
                updated_at = $now
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", targetId);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$driveId", (object?)driveId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sensorId", sensorId);
        cmd.Parameters.AddWithValue("$fanIds", System.Text.Json.JsonSerializer.Serialize(fanIds));
        cmd.Parameters.AddWithValue("$targetTemp", targetTempC);
        cmd.Parameters.AddWithValue("$tolerance", toleranceC);
        cmd.Parameters.AddWithValue("$minSpeed", minFanSpeed);
        cmd.Parameters.AddWithValue("$pidMode", pidMode ? 1 : 0);
        cmd.Parameters.AddWithValue("$pidKp", pidKp);
        cmd.Parameters.AddWithValue("$pidKi", pidKi);
        cmd.Parameters.AddWithValue("$pidKd", pidKd);
        cmd.Parameters.AddWithValue("$now", now);
        var rows = await cmd.ExecuteNonQueryAsync(ct);
        return rows > 0 ? await GetTemperatureTargetAsync(targetId, ct) : null;
    }

    public async Task<TemperatureTarget?> SetTemperatureTargetEnabledAsync(
        string targetId, bool enabled, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        var now = DateTimeOffset.UtcNow.ToString("o");
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE temperature_targets SET enabled = $enabled, updated_at = $now WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", targetId);
        cmd.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$now", now);
        var rows = await cmd.ExecuteNonQueryAsync(ct);
        return rows > 0 ? await GetTemperatureTargetAsync(targetId, ct) : null;
    }

    public async Task<bool> DeleteTemperatureTargetAsync(string targetId, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM temperature_targets WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", targetId);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    private static TemperatureTarget ReadTemperatureTarget(SqliteDataReader reader)
    {
        var fanIdsJson = reader.IsDBNull(4) ? "[]" : reader.GetString(4);
        return new TemperatureTarget
        {
            Id          = reader.GetString(0),
            Name        = reader.GetString(1),
            DriveId     = reader.IsDBNull(2) ? null : reader.GetString(2),
            SensorId    = reader.GetString(3),
            FanIds      = System.Text.Json.JsonSerializer.Deserialize<string[]>(fanIdsJson) ?? [],
            TargetTempC = reader.GetDouble(5),
            ToleranceC  = reader.GetDouble(6),
            MinFanSpeed = reader.GetDouble(7),
            Enabled     = reader.GetInt32(8) != 0,
            PidMode     = !reader.IsDBNull(9)  && reader.GetInt32(9)  != 0,
            PidKp       = reader.IsDBNull(10)  ? 5.0  : reader.GetDouble(10),
            PidKi       = reader.IsDBNull(11)  ? 0.05 : reader.GetDouble(11),
            PidKd       = reader.IsDBNull(12)  ? 1.0  : reader.GetDouble(12),
        };
    }

    // -----------------------------------------------------------------------
    // Auth log
    // -----------------------------------------------------------------------

    public async Task LogAuthEventAsync(string eventType, string? ip, string? username,
        string outcome, string? detail, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO auth_log (timestamp, event_type, ip, username, outcome, detail)
            VALUES ($ts, $et, $ip, $user, $outcome, $detail)
            """;
        cmd.Parameters.AddWithValue("$ts",      DateTimeOffset.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$et",      eventType);
        cmd.Parameters.AddWithValue("$ip",      (object?)ip       ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$user",    (object?)username ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$outcome", outcome);
        cmd.Parameters.AddWithValue("$detail",  (object?)detail   ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Delete auth_log rows older than retentionDays in batches of 500.</summary>
    public async Task CleanupOldAuthLogsAsync(int retentionDays = 90, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays).ToString("o");
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);

        while (true)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                DELETE FROM auth_log WHERE id IN (
                    SELECT id FROM auth_log WHERE timestamp < $cutoff LIMIT 500
                )
                """;
            cmd.Parameters.AddWithValue("$cutoff", cutoff);
            var deleted = await cmd.ExecuteNonQueryAsync(ct);
            if (deleted == 0) break;
        }
    }

    // -----------------------------------------------------------------------
    // Notification Channels CRUD
    // -----------------------------------------------------------------------

    public async Task<List<NotificationChannel>> GetNotificationChannelsAsync(CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, type, name, enabled, config_json, created_at, updated_at FROM notification_channels ORDER BY created_at";
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        var list = new List<NotificationChannel>();
        while (await rdr.ReadAsync(ct))
            list.Add(ReadNotificationChannel(rdr));
        return list;
    }

    public async Task<NotificationChannel?> GetNotificationChannelAsync(string id, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, type, name, enabled, config_json, created_at, updated_at FROM notification_channels WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        return await rdr.ReadAsync(ct) ? ReadNotificationChannel(rdr) : null;
    }

    public async Task CreateNotificationChannelAsync(string id, string type, string name,
        bool enabled, Dictionary<string, System.Text.Json.JsonElement> config, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO notification_channels (id, type, name, enabled, config_json) VALUES ($id, $type, $name, $enabled, $config)";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$type", type);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$config", System.Text.Json.JsonSerializer.Serialize(config));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> UpdateNotificationChannelAsync(string id, string? name, bool? enabled,
        Dictionary<string, System.Text.Json.JsonElement>? config, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        var parts = new List<string>();
        var parms = new List<SqliteParameter>();
        if (name is not null) { parts.Add("name = $name"); parms.Add(new("$name", name)); }
        if (enabled.HasValue) { parts.Add("enabled = $enabled"); parms.Add(new("$enabled", enabled.Value ? 1 : 0)); }
        if (config is not null) { parts.Add("config_json = $config"); parms.Add(new("$config", System.Text.Json.JsonSerializer.Serialize(config))); }
        if (parts.Count == 0) return false;
        parts.Add("updated_at = datetime('now')");
        parms.Add(new("$id", id));

        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"UPDATE notification_channels SET {string.Join(", ", parts)} WHERE id = $id";
        foreach (var p in parms) cmd.Parameters.Add(p);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<bool> DeleteNotificationChannelAsync(string id, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM notification_channels WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    private static NotificationChannel ReadNotificationChannel(SqliteDataReader rdr)
    {
        var configJson = rdr.IsDBNull(4) ? "{}" : rdr.GetString(4);
        Dictionary<string, System.Text.Json.JsonElement> config;
        try { config = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(configJson) ?? []; }
        catch { config = []; }
        return new NotificationChannel
        {
            Id        = rdr.GetString(0),
            Type      = rdr.GetString(1),
            Name      = rdr.GetString(2),
            Enabled   = rdr.GetInt32(3) != 0,
            Config    = config,
            CreatedAt = rdr.IsDBNull(5) ? "" : rdr.GetString(5),
            UpdatedAt = rdr.IsDBNull(6) ? "" : rdr.GetString(6),
        };
    }

    // -----------------------------------------------------------------------
    // Profiles + profile curves
    // -----------------------------------------------------------------------

    private static readonly System.Text.Json.JsonSerializerOptions _profileJsonOpts = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public async Task<List<Profile>> ListProfilesAsync(CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);

        // Load all profiles
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, description, is_active, created_at, updated_at FROM profiles ORDER BY created_at ASC";
        var profiles = new List<Profile>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                profiles.Add(ReadProfileRow(reader));
        }

        // Load all curves in bulk
        await using var cmd2 = conn.CreateCommand();
        cmd2.CommandText = "SELECT id, profile_id, fan_id, sensor_id, enabled, points_json, sensor_ids_json FROM profile_curves ORDER BY rowid ASC";
        var curvesByProfile = new Dictionary<string, List<FanCurve>>();
        await using (var reader2 = await cmd2.ExecuteReaderAsync(ct))
        {
            while (await reader2.ReadAsync(ct))
            {
                var profileId = reader2.GetString(1);
                if (!curvesByProfile.TryGetValue(profileId, out var list))
                    curvesByProfile[profileId] = list = [];
                list.Add(ReadCurveRow(reader2));
            }
        }

        foreach (var p in profiles)
            p.Curves = curvesByProfile.TryGetValue(p.Id, out var c) ? c : [];

        return profiles;
    }

    public async Task<Profile?> GetProfileAsync(string id, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, description, is_active, created_at, updated_at FROM profiles WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        Profile? profile = null;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
                profile = ReadProfileRow(reader);
        }
        if (profile == null) return null;

        // Load curves
        await using var cmd2 = conn.CreateCommand();
        cmd2.CommandText = "SELECT id, profile_id, fan_id, sensor_id, enabled, points_json, sensor_ids_json FROM profile_curves WHERE profile_id = $pid ORDER BY rowid ASC";
        cmd2.Parameters.AddWithValue("$pid", id);
        profile.Curves = [];
        await using (var reader2 = await cmd2.ExecuteReaderAsync(ct))
        {
            while (await reader2.ReadAsync(ct))
                profile.Curves.Add(ReadCurveRow(reader2));
        }
        return profile;
    }

    public async Task CreateProfileAsync(Profile profile, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO profiles (id, name, description, is_active, created_at, updated_at)
            VALUES ($id, $name, $desc, $active, $created, $updated)
            """;
        cmd.Parameters.AddWithValue("$id", profile.Id);
        cmd.Parameters.AddWithValue("$name", profile.Name);
        cmd.Parameters.AddWithValue("$desc", profile.Description);
        cmd.Parameters.AddWithValue("$active", profile.IsActive ? 1 : 0);
        cmd.Parameters.AddWithValue("$created", profile.CreatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("$updated", profile.UpdatedAt.ToString("o"));
        await cmd.ExecuteNonQueryAsync(ct);

        await InsertCurvesAsync(conn, profile.Id, profile.Curves, ct);

        await tx.CommitAsync(ct);
    }

    public async Task UpdateProfileAsync(Profile profile, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE profiles SET name = $name, description = $desc, is_active = $active, updated_at = $updated
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", profile.Id);
        cmd.Parameters.AddWithValue("$name", profile.Name);
        cmd.Parameters.AddWithValue("$desc", profile.Description);
        cmd.Parameters.AddWithValue("$active", profile.IsActive ? 1 : 0);
        cmd.Parameters.AddWithValue("$updated", profile.UpdatedAt.ToString("o"));
        await cmd.ExecuteNonQueryAsync(ct);

        // Replace curves: delete all, re-insert
        await using var del = conn.CreateCommand();
        del.CommandText = "DELETE FROM profile_curves WHERE profile_id = $pid";
        del.Parameters.AddWithValue("$pid", profile.Id);
        await del.ExecuteNonQueryAsync(ct);

        await InsertCurvesAsync(conn, profile.Id, profile.Curves, ct);

        await tx.CommitAsync(ct);
    }

    public async Task<bool> DeleteProfileAsync(string id, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM profiles WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task ActivateProfileAsync(string id, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using var reset = conn.CreateCommand();
        reset.CommandText = "UPDATE profiles SET is_active = 0";
        await reset.ExecuteNonQueryAsync(ct);

        await using var set = conn.CreateCommand();
        set.CommandText = "UPDATE profiles SET is_active = 1 WHERE id = $id";
        set.Parameters.AddWithValue("$id", id);
        await set.ExecuteNonQueryAsync(ct);

        await tx.CommitAsync(ct);
    }

    private async Task InsertCurvesAsync(SqliteConnection conn, string profileId,
        List<FanCurve> curves, CancellationToken ct)
    {
        foreach (var curve in curves)
        {
            var curveId = Guid.NewGuid().ToString();
            var pointsJson = System.Text.Json.JsonSerializer.Serialize(curve.Points, _profileJsonOpts);
            var sensorIdsJson = System.Text.Json.JsonSerializer.Serialize(curve.SensorIds, _profileJsonOpts);

            await using var ins = conn.CreateCommand();
            ins.CommandText = """
                INSERT INTO profile_curves (id, profile_id, fan_id, sensor_id, enabled, points_json, sensor_ids_json)
                VALUES ($id, $pid, $fanId, $sensorId, $enabled, $points, $sensorIds)
                """;
            ins.Parameters.AddWithValue("$id", curveId);
            ins.Parameters.AddWithValue("$pid", profileId);
            ins.Parameters.AddWithValue("$fanId", curve.FanId);
            ins.Parameters.AddWithValue("$sensorId", curve.SensorId);
            ins.Parameters.AddWithValue("$enabled", curve.Enabled ? 1 : 0);
            ins.Parameters.AddWithValue("$points", pointsJson);
            ins.Parameters.AddWithValue("$sensorIds", sensorIdsJson);
            await ins.ExecuteNonQueryAsync(ct);
        }
    }

    private static Profile ReadProfileRow(SqliteDataReader reader) => new()
    {
        Id          = reader.GetString(0),
        Name        = reader.GetString(1),
        Description = reader.GetString(2),
        IsActive    = reader.GetInt32(3) != 0,
        CreatedAt   = DateTimeOffset.TryParse(reader.GetString(4), out var ca) ? ca : DateTimeOffset.UtcNow,
        UpdatedAt   = DateTimeOffset.TryParse(reader.GetString(5), out var ua) ? ua : DateTimeOffset.UtcNow,
    };

    private FanCurve ReadCurveRow(SqliteDataReader reader)
    {
        var pointsJson = reader.GetString(5);
        var sensorIdsJson = reader.GetString(6);
        return new FanCurve
        {
            FanId     = reader.GetString(2),
            SensorId  = reader.GetString(3),
            Enabled   = reader.GetInt32(4) != 0,
            Points    = System.Text.Json.JsonSerializer.Deserialize<List<FanCurvePoint>>(pointsJson, _profileJsonOpts) ?? [],
            SensorIds = System.Text.Json.JsonSerializer.Deserialize<List<string>>(sensorIdsJson, _profileJsonOpts) ?? [],
        };
    }

    // -----------------------------------------------------------------------
    // Alert rules
    // -----------------------------------------------------------------------

    public async Task<List<AlertRule>> ListAlertRulesAsync(CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, sensor_id, sensor_name, threshold, condition, message, enabled, action_json, created_at FROM alert_rules ORDER BY created_at ASC";
        var results = new List<AlertRule>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(ReadAlertRuleRow(reader));
        return results;
    }

    public async Task CreateAlertRuleAsync(AlertRule rule, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        var actionJson = rule.Action != null
            ? System.Text.Json.JsonSerializer.Serialize(rule.Action, _profileJsonOpts)
            : null;
        cmd.CommandText = """
            INSERT INTO alert_rules (id, sensor_id, sensor_name, threshold, condition, message, enabled, action_json, created_at)
            VALUES ($id, $sid, $sname, $threshold, $cond, $msg, $enabled, $action, $created)
            """;
        cmd.Parameters.AddWithValue("$id", rule.RuleId);
        cmd.Parameters.AddWithValue("$sid", rule.SensorId);
        cmd.Parameters.AddWithValue("$sname", rule.SensorName);
        cmd.Parameters.AddWithValue("$threshold", rule.Threshold);
        cmd.Parameters.AddWithValue("$cond", rule.Condition);
        cmd.Parameters.AddWithValue("$msg", rule.Message);
        cmd.Parameters.AddWithValue("$enabled", rule.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$action", (object?)actionJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$created", rule.CreatedAt.ToString("o"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> DeleteAlertRuleAsync(string id, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM alert_rules WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task SaveAlertRulesAsync(IEnumerable<AlertRule> rules, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using var del = conn.CreateCommand();
        del.CommandText = "DELETE FROM alert_rules";
        await del.ExecuteNonQueryAsync(ct);

        foreach (var rule in rules)
        {
            var actionJson = rule.Action != null
                ? System.Text.Json.JsonSerializer.Serialize(rule.Action, _profileJsonOpts)
                : null;
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO alert_rules (id, sensor_id, sensor_name, threshold, condition, message, enabled, action_json, created_at)
                VALUES ($id, $sid, $sname, $threshold, $cond, $msg, $enabled, $action, $created)
                """;
            cmd.Parameters.AddWithValue("$id", rule.RuleId);
            cmd.Parameters.AddWithValue("$sid", rule.SensorId);
            cmd.Parameters.AddWithValue("$sname", rule.SensorName);
            cmd.Parameters.AddWithValue("$threshold", rule.Threshold);
            cmd.Parameters.AddWithValue("$cond", rule.Condition);
            cmd.Parameters.AddWithValue("$msg", rule.Message);
            cmd.Parameters.AddWithValue("$enabled", rule.Enabled ? 1 : 0);
            cmd.Parameters.AddWithValue("$action", (object?)actionJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$created", rule.CreatedAt.ToString("o"));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    private static AlertRule ReadAlertRuleRow(SqliteDataReader reader)
    {
        AlertAction? action = null;
        if (!reader.IsDBNull(7))
        {
            var actionJson = reader.GetString(7);
            action = System.Text.Json.JsonSerializer.Deserialize<AlertAction>(actionJson,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }

        return new AlertRule
        {
            RuleId     = reader.GetString(0),
            SensorId   = reader.GetString(1),
            SensorName = reader.GetString(2),
            Threshold  = reader.GetDouble(3),
            Condition  = reader.GetString(4),
            Message    = reader.GetString(5),
            Enabled    = reader.GetInt32(6) != 0,
            Action     = action,
            CreatedAt  = DateTimeOffset.TryParse(reader.GetString(8), out var ca) ? ca : DateTimeOffset.UtcNow,
        };
    }
}

/// <summary>Quiet hours rule model.</summary>
public sealed class QuietHoursRule
{
    public int Id { get; set; }
    public int DayOfWeek { get; set; }
    public string StartTime { get; set; } = "00:00";
    public string EndTime { get; set; } = "00:00";
    public string ProfileId { get; set; } = "";
    public bool Enabled { get; set; } = true;
}
