using Microsoft.Data.Sqlite;
using DriveChill.Models;
using System.Text;

namespace DriveChill.Services;

/// <summary>
/// Persists sensor readings to a local SQLite database for history and export.
/// Schema is auto-created on first use.
/// </summary>
public sealed class DbService : IDisposable
{
    private readonly string _connStr;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile bool _initialised;

    public DbService(AppSettings settings)
    {
        Directory.CreateDirectory(settings.DataDir);
        _connStr = $"Data Source={settings.DbPath}";
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
                    ts          TEXT    NOT NULL,
                    sensor_id   TEXT    NOT NULL,
                    sensor_name TEXT    NOT NULL,
                    sensor_type TEXT    NOT NULL,
                    value       REAL    NOT NULL,
                    unit        TEXT    NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_sensor_log_ts ON sensor_log(ts);
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
                """;
            await cmd.ExecuteNonQueryAsync(ct);
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
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var ts = DateTimeOffset.UtcNow.ToString("o");

        foreach (var r in readings)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO sensor_log (ts, sensor_id, sensor_name, sensor_type, value, unit)
                VALUES ($ts, $sid, $name, $type, $val, $unit)
                """;
            cmd.Parameters.AddWithValue("$ts",   ts);
            cmd.Parameters.AddWithValue("$sid",  r.Id);
            cmd.Parameters.AddWithValue("$name", r.Name);
            cmd.Parameters.AddWithValue("$type", r.SensorType);
            cmd.Parameters.AddWithValue("$val",  r.Value);
            cmd.Parameters.AddWithValue("$unit", r.Unit);
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
            WHERE sensor_id = $sid AND ts >= $since
            ORDER BY ts ASC
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
            SELECT ts, sensor_id, sensor_name, sensor_type, value, unit
            FROM sensor_log
            WHERE ts >= $since
            ORDER BY ts ASC
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
    // Prune
    // -----------------------------------------------------------------------

    public async Task PruneAsync(int retentionDays, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays).ToString("o");
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM sensor_log WHERE ts < $cutoff";
        cmd.Parameters.AddWithValue("$cutoff", cutoff);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // -----------------------------------------------------------------------
    // Analytics
    // -----------------------------------------------------------------------

    public async Task<List<AnalyticsBucket>> GetAnalyticsHistoryAsync(
        double hours, string? sensorId, int bucketSeconds, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        bucketSeconds = Math.Max(10, Math.Min(86400, bucketSeconds));
        var since = DateTimeOffset.UtcNow.AddHours(-hours).ToString("o");

        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT
              sensor_id,
              MAX(sensor_name) AS sensor_name,
              MAX(sensor_type) AS sensor_type,
              MAX(unit) AS unit,
              CAST(strftime('%s', ts) AS INTEGER) / {bucketSeconds} AS bucket_epoch,
              AVG(CAST(value AS REAL)) AS avg_value,
              MIN(CAST(value AS REAL)) AS min_value,
              MAX(CAST(value AS REAL)) AS max_value,
              COUNT(*) AS sample_count
            FROM sensor_log
            WHERE ts >= $since
              AND ($sid IS NULL OR sensor_id = $sid)
            GROUP BY sensor_id, bucket_epoch
            ORDER BY bucket_epoch ASC
            """;
        cmd.Parameters.AddWithValue("$since", since);
        cmd.Parameters.AddWithValue("$sid", (object?)sensorId ?? DBNull.Value);

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

    public async Task<List<AnalyticsStat>> GetAnalyticsStatsAsync(
        double hours, string? sensorId, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        var since = DateTimeOffset.UtcNow.AddHours(-hours).ToString("o");

        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT sensor_id, MAX(sensor_name), MAX(sensor_type), MAX(unit),
                   MIN(CAST(value AS REAL)), MAX(CAST(value AS REAL)),
                   AVG(CAST(value AS REAL)), COUNT(*)
            FROM sensor_log
            WHERE ts >= $since AND ($sid IS NULL OR sensor_id = $sid)
            GROUP BY sensor_id
            """;
        cmd.Parameters.AddWithValue("$since", since);
        cmd.Parameters.AddWithValue("$sid", (object?)sensorId ?? DBNull.Value);

        var rows = new List<(string Id, string Name, string Type, string Unit, double Min, double Max, double Avg, int Count)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                      reader.GetDouble(4), reader.GetDouble(5), reader.GetDouble(6), reader.GetInt32(7)));

        var stats = new List<AnalyticsStat>();
        foreach (var row in rows)
        {
            // Compute p95 via separate sorted query
            double p95 = row.Avg;
            await using var cmd2 = conn.CreateCommand();
            cmd2.CommandText = """
                SELECT CAST(value AS REAL) FROM sensor_log
                WHERE ts >= $since AND sensor_id = $sid
                ORDER BY CAST(value AS REAL) ASC
                """;
            cmd2.Parameters.AddWithValue("$since", since);
            cmd2.Parameters.AddWithValue("$sid", row.Id);
            var vals = new List<double>();
            await using var r2 = await cmd2.ExecuteReaderAsync(ct);
            while (await r2.ReadAsync(ct)) vals.Add(r2.GetDouble(0));
            if (vals.Count > 0)
                p95 = vals[Math.Min(vals.Count - 1, (int)(vals.Count * 0.95))];

            stats.Add(new AnalyticsStat
            {
                SensorId    = row.Id,
                SensorName  = row.Name,
                SensorType  = row.Type,
                Unit        = row.Unit,
                MinValue    = row.Min,
                MaxValue    = row.Max,
                AvgValue    = row.Avg,
                P95Value    = p95,
                SampleCount = row.Count,
            });
        }
        return stats;
    }

    public async Task<List<AnalyticsAnomaly>> GetAnalyticsAnomaliesAsync(
        double hours, double zScoreThreshold, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        var since = DateTimeOffset.UtcNow.AddHours(-hours).ToString("o");

        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT ts, sensor_id, MAX(sensor_name) AS sensor_name,
                   MAX(sensor_type), CAST(value AS REAL), MAX(unit)
            FROM sensor_log
            WHERE ts >= $since
            GROUP BY ts, sensor_id
            ORDER BY ts ASC
            """;
        cmd.Parameters.AddWithValue("$since", since);

        // Load all rows and group in memory
        var byId = new Dictionary<string, List<(string Ts, double V, string Name, string Type, string Unit)>>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var sid = reader.GetString(1);
            if (!byId.TryGetValue(sid, out var list))
                byId[sid] = list = new();
            list.Add((reader.GetString(0), reader.GetDouble(4), reader.GetString(2), reader.GetString(3), reader.GetString(5)));
        }

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
                    });
            }
        }
        anomalies.Sort((a, b) => b.ZScore.CompareTo(a.ZScore));
        return anomalies;
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
            UPDATE machines SET name=$name, base_url=$url, enabled=$en,
                poll_interval_seconds=$poll, timeout_ms=$timeout, updated_at=$updated
            WHERE id=$id
            """;
        cmd.Parameters.AddWithValue("$id",      id);
        cmd.Parameters.AddWithValue("$name",    patch.Name);
        cmd.Parameters.AddWithValue("$url",     patch.BaseUrl);
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
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
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
        cmd.Parameters.AddWithValue("$pass",       s.SmtpPassword);
        cmd.Parameters.AddWithValue("$sender",     s.SenderAddress);
        cmd.Parameters.AddWithValue("$recipients", s.RecipientList);
        cmd.Parameters.AddWithValue("$tls",        s.UseTls ? 1 : 0);
        cmd.Parameters.AddWithValue("$ssl",        s.UseSsl ? 1 : 0);
        await cmd.ExecuteNonQueryAsync(ct);
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

    public async Task CreateUserAsync(string username, string passwordHash, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO users (username, password_hash, created_at) VALUES ($u, $h, $t)";
        cmd.Parameters.AddWithValue("$u", username);
        cmd.Parameters.AddWithValue("$h", passwordHash);
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

    public async Task<(string Username, string CsrfToken)?> ValidateSessionAsync(string token, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT username, csrf_token FROM sessions WHERE token = $tok AND expires_at > $now";
        cmd.Parameters.AddWithValue("$tok", token);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("o"));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return (reader.GetString(0), reader.GetString(1));
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

    public void Dispose() { _initLock.Dispose(); }

    /// <summary>RFC 4180 CSV field escaping: quote fields containing comma, quote, or newline.</summary>
    private static string CsvEscape(string field)
    {
        if (field.Contains(',') || field.Contains('"') || field.Contains('\n') || field.Contains('\r'))
            return $"\"{field.Replace("\"", "\"\"")}\"";
        return field;
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
