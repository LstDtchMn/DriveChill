using System;
using System.IO;
using System.Threading.Tasks;
using DriveChill.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DriveChill.Tests;

public class DbServiceTests : IDisposable
{
    private readonly string _tempDir;

    public DbServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
        Environment.SetEnvironmentVariable("DRIVECHILL_DATA_DIR", _tempDir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("DRIVECHILL_DATA_DIR", null);
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task PruneAsync_DeletesOldRowsFromBothTables()
    {
        var settings = new AppSettings();
        var db       = new DbService(settings, NullLogger<DbService>.Instance);

        // Initialize schema (no-op prune with very large retention)
        await db.PruneAsync(retentionDays: 365);

        var oldTs    = DateTimeOffset.UtcNow.AddDays(-10).ToString("o");
        var recentTs = DateTimeOffset.UtcNow.AddHours(-1).ToString("o");
        var connStr  = $"Data Source={settings.DbPath}";

        // Seed one old + one recent row into each table
        await using (var conn = new SqliteConnection(connStr))
        {
            await conn.OpenAsync();

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                INSERT INTO sensor_log (timestamp, sensor_id, sensor_name, sensor_type, value, unit)
                VALUES ('{oldTs}',    'test_sensor', 'Test', 'temperature', 40.0, 'C'),
                       ('{recentTs}', 'test_sensor', 'Test', 'temperature', 41.0, 'C');

                INSERT INTO drives (id, name) VALUES ('d_old', 'Drive Old'), ('d_recent', 'Drive Recent');

                INSERT INTO drive_health_snapshots (drive_id, recorded_at, temperature_c)
                VALUES ('d_old',    '{oldTs}',    38.0),
                       ('d_recent', '{recentTs}', 39.0);
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        // Prune with 1-day retention — 10-day-old rows should be deleted
        await db.PruneAsync(retentionDays: 1);

        // Verify only recent rows survive
        await using (var conn = new SqliteConnection(connStr))
        {
            await conn.OpenAsync();

            await using var cmd1 = conn.CreateCommand();
            cmd1.CommandText = "SELECT COUNT(*) FROM sensor_log";
            var sensorCount = (long)(await cmd1.ExecuteScalarAsync())!;
            Assert.Equal(1L, sensorCount);

            await using var cmd2 = conn.CreateCommand();
            cmd2.CommandText = "SELECT COUNT(*) FROM drive_health_snapshots";
            var healthCount = (long)(await cmd2.ExecuteScalarAsync())!;
            Assert.Equal(1L, healthCount);
        }

        db.Dispose();
    }

    [Fact]
    public async Task PruneAsync_RetainsRowsWithinRetentionWindow()
    {
        var settings = new AppSettings();
        var db       = new DbService(settings, NullLogger<DbService>.Instance);
        await db.PruneAsync(retentionDays: 365);

        var recentTs = DateTimeOffset.UtcNow.AddHours(-2).ToString("o");
        var connStr  = $"Data Source={settings.DbPath}";

        await using (var conn = new SqliteConnection(connStr))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                INSERT INTO sensor_log (timestamp, sensor_id, sensor_name, sensor_type, value, unit)
                VALUES ('{recentTs}', 'test_sensor', 'Test', 'temperature', 40.0, 'C');
                INSERT INTO drives (id, name) VALUES ('d1', 'Drive 1');
                INSERT INTO drive_health_snapshots (drive_id, recorded_at, temperature_c)
                VALUES ('d1', '{recentTs}', 38.0);
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        // 30-day retention — recent rows should be kept
        await db.PruneAsync(retentionDays: 30);

        await using (var conn = new SqliteConnection(connStr))
        {
            await conn.OpenAsync();

            await using var cmd1 = conn.CreateCommand();
            cmd1.CommandText = "SELECT COUNT(*) FROM sensor_log";
            Assert.Equal(1L, (long)(await cmd1.ExecuteScalarAsync())!);

            await using var cmd2 = conn.CreateCommand();
            cmd2.CommandText = "SELECT COUNT(*) FROM drive_health_snapshots";
            Assert.Equal(1L, (long)(await cmd2.ExecuteScalarAsync())!);
        }

        db.Dispose();
    }
}
