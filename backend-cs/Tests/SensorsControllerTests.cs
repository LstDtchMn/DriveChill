using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using DriveChill.Api;
using DriveChill.Models;
using DriveChill.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DriveChill.Tests;

public sealed class SensorsControllerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AppSettings _settings;
    private readonly DbService _db;
    private readonly SensorService _sensors;
    private readonly SensorsController _ctrl;

    public SensorsControllerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
        Environment.SetEnvironmentVariable("DRIVECHILL_DATA_DIR", _tempDir);

        _settings = new AppSettings();
        _db = new DbService(_settings, NullLogger<DbService>.Instance);
        _sensors = new SensorService();
        _ctrl = new SensorsController(_sensors, _db);
    }

    public void Dispose()
    {
        _db.Dispose();
        Environment.SetEnvironmentVariable("DRIVECHILL_DATA_DIR", null);
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // -----------------------------------------------------------------------
    // GET /api/sensors
    // -----------------------------------------------------------------------

    [Fact]
    public void GetSensors_ReturnsOkWithSnapshot()
    {
        var result = _ctrl.GetSensors();
        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.IsType<SensorSnapshot>(ok.Value);
    }

    // -----------------------------------------------------------------------
    // GET /api/sensors/labels
    // -----------------------------------------------------------------------

    [Fact]
    public async Task GetLabels_ReturnsEmptyInitially()
    {
        var result = await _ctrl.GetLabels(default);
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value);
        Assert.Contains("\"labels\":{}", json);
    }

    // -----------------------------------------------------------------------
    // PUT /api/sensors/{sensorId}/label
    // -----------------------------------------------------------------------

    [Fact]
    public async Task SetLabel_ReturnsOk_ForValidLabel()
    {
        var req = new SetLabelRequest { Label = "CPU Package" };
        var result = await _ctrl.SetLabel("cpu_temp_1", req, default);
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value);
        Assert.Contains("\"success\":true", json);
        Assert.Contains("\"label\":\"CPU Package\"", json);
    }

    [Fact]
    public async Task SetLabel_ReturnsBadRequest_ForEmptyLabel()
    {
        var req = new SetLabelRequest { Label = "" };
        var result = await _ctrl.SetLabel("cpu_temp_1", req, default);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task SetLabel_ReturnsBadRequest_ForTooLongLabel()
    {
        var req = new SetLabelRequest { Label = new string('X', 101) };
        var result = await _ctrl.SetLabel("cpu_temp_1", req, default);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task SetLabel_ReturnsUnprocessable_ForTooLongSensorId()
    {
        var req = new SetLabelRequest { Label = "OK" };
        var longId = new string('a', 201);
        var result = await _ctrl.SetLabel(longId, req, default);
        Assert.IsType<UnprocessableEntityObjectResult>(result);
    }

    // -----------------------------------------------------------------------
    // DELETE /api/sensors/{sensorId}/label
    // -----------------------------------------------------------------------

    [Fact]
    public async Task DeleteLabel_ReturnsNotFound_WhenNoLabelExists()
    {
        var result = await _ctrl.DeleteLabel("nonexistent_sensor", default);
        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task DeleteLabel_ReturnsOk_AfterSetLabel()
    {
        // Set a label first
        var req = new SetLabelRequest { Label = "My Sensor" };
        await _ctrl.SetLabel("sensor_del", req, default);

        // Delete it
        var result = await _ctrl.DeleteLabel("sensor_del", default);
        Assert.IsType<OkObjectResult>(result);

        // Verify it's gone
        var delAgain = await _ctrl.DeleteLabel("sensor_del", default);
        Assert.IsType<NotFoundObjectResult>(delAgain);
    }
}
