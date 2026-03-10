using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using DriveChill.Api;
using DriveChill.Models;
using DriveChill.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DriveChill.Tests;

public sealed class ExportControllerTests : IDisposable
{
    private readonly string          _tempDir;
    private readonly AppSettings     _settings;
    private readonly SettingsStore   _store;
    private readonly DbService       _db;
    private readonly AnalyticsController _ctrl;

    public ExportControllerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
        Environment.SetEnvironmentVariable("DRIVECHILL_DATA_DIR", _tempDir);

        _settings = new AppSettings();
        _store    = new SettingsStore(_settings);
        _db       = new DbService(_settings, NullLogger<DbService>.Instance);
        _ctrl     = new AnalyticsController(_db, _store);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("DRIVECHILL_DATA_DIR", null);
        _db.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    /// <summary>Insert a sensor_log row directly via raw SQL.</summary>
    private async Task InsertReadingAsync(string sensorId, string sensorName,
        string sensorType, double value, string unit, DateTimeOffset timestamp)
    {
        // Ensure schema exists
        await _db.PruneAsync(retentionDays: 365);

        var connStr = $"Data Source={_settings.DbPath}";
        await using var conn = new SqliteConnection(connStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sensor_log (timestamp, sensor_id, sensor_name, sensor_type, value, unit)
            VALUES ($ts, $sid, $name, $type, $val, $unit)
            """;
        cmd.Parameters.AddWithValue("$ts",   timestamp.ToString("o"));
        cmd.Parameters.AddWithValue("$sid",  sensorId);
        cmd.Parameters.AddWithValue("$name", sensorName);
        cmd.Parameters.AddWithValue("$type", sensorType);
        cmd.Parameters.AddWithValue("$val",  value);
        cmd.Parameters.AddWithValue("$unit", unit);
        await cmd.ExecuteNonQueryAsync();
    }

    private static string GetFileResultContent(IActionResult result)
    {
        var file = Assert.IsType<FileContentResult>(result);
        return Encoding.UTF8.GetString(file.FileContents);
    }

    // ------------------------------------------------------------------
    // Tests
    // ------------------------------------------------------------------

    [Fact]
    public async Task CsvExport_EmptyData_ReturnsHeadersOnly()
    {
        // Ensure DB schema is initialised (no rows)
        await _db.PruneAsync(retentionDays: 365);

        var result = await _ctrl.Export(format: "csv", hours: 24);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("text/csv", file.ContentType);

        var content = Encoding.UTF8.GetString(file.FileContents);
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // Only the header row should be present
        Assert.Single(lines);
        Assert.Equal(
            "timestamp_utc,sensor_id,sensor_name,sensor_type,unit,avg_value,min_value,max_value,sample_count",
            lines[0].Trim());
    }

    [Fact]
    public async Task JsonExport_EmptyData_ReturnsEmptyArray()
    {
        await _db.PruneAsync(retentionDays: 365);

        var result = await _ctrl.Export(format: "json", hours: 24);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("application/json", file.ContentType);

        var json = Encoding.UTF8.GetString(file.FileContents);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(0, doc.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task CsvExport_WithData_IncludesRows()
    {
        var ts = DateTimeOffset.UtcNow.AddMinutes(-5);
        await InsertReadingAsync("cpu_0", "CPU Package", "cpu_temp", 55.0, "C", ts);

        // Use a wide window so the row falls inside the export range
        var result = await _ctrl.Export(format: "csv", hours: 1);

        var content = GetFileResultContent(result);
        var lines   = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // Header + at least one data row
        Assert.True(lines.Length >= 2, $"Expected at least 2 lines, got:\n{content}");
        Assert.Contains("cpu_0", lines[1]);
        Assert.Contains("CPU Package", lines[1]);
        Assert.Contains("cpu_temp", lines[1]);
    }

    [Fact]
    public async Task CsvExport_SpecialCharacters_Escaped()
    {
        // A sensor name that contains a comma must be quoted in the CSV output
        var ts = DateTimeOffset.UtcNow.AddMinutes(-5);
        await InsertReadingAsync("sensor_x", "Temp, Core #1", "cpu_temp", 42.0, "C", ts);

        var result = await _ctrl.Export(format: "csv", hours: 1);

        var content = GetFileResultContent(result);
        // The quoted form should appear somewhere in the body
        Assert.Contains("\"Temp, Core #1\"", content);
    }

    [Fact]
    public async Task Export_InvalidFormat_ReturnsBadRequest()
    {
        await _db.PruneAsync(retentionDays: 365);

        var result = await _ctrl.Export(format: "xml", hours: 24);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        // Verify the detail message matches the controller implementation
        var json = JsonSerializer.Serialize(bad.Value);
        Assert.Contains("format must be csv or json", json);
    }
}
