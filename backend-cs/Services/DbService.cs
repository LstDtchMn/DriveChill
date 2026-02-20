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
    private bool _initialised;

    public DbService(AppSettings settings)
    {
        Directory.CreateDirectory(settings.DataDir);
        _connStr = $"Data Source={settings.DbPath}";
    }

    // -----------------------------------------------------------------------
    // Schema init (called lazily)
    // -----------------------------------------------------------------------

    private async Task EnsureInitialisedAsync(CancellationToken ct = default)
    {
        if (_initialised) return;
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
            """;
        await cmd.ExecuteNonQueryAsync(ct);
        _initialised = true;
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
            sb.Append(reader.GetString(0)).Append(',')
              .Append(reader.GetString(1)).Append(',')
              .Append(reader.GetString(2)).Append(',')
              .Append(reader.GetString(3)).Append(',')
              .Append(reader.GetDouble(4)).Append(',')
              .Append(reader.GetString(5)).Append('\n');
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

    public void Dispose() { /* connections are disposed per-operation */ }
}
