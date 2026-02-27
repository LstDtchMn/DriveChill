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
