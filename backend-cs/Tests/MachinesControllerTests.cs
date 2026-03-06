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

public sealed class MachinesControllerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DbService _db;
    private readonly MachinesController _ctrl;

    public MachinesControllerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
        Environment.SetEnvironmentVariable("DRIVECHILL_DATA_DIR", _tempDir);

        var settings = new AppSettings();
        _db   = new DbService(settings, NullLogger<DbService>.Instance);
        _ctrl = new MachinesController(_db, settings);
    }

    public void Dispose()
    {
        _db.Dispose();
        Environment.SetEnvironmentVariable("DRIVECHILL_DATA_DIR", null);
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static JsonElement ToJsonElement(object obj)
    {
        var json = JsonSerializer.Serialize(obj);
        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    // -----------------------------------------------------------------------
    // CRUD
    // -----------------------------------------------------------------------

    [Fact]
    public async Task CreateMachine_ReturnsOk_WithMachineView()
    {
        var body = ToJsonElement(new { name = "TestMachine", base_url = "https://example.com:8085" });
        var result = await _ctrl.CreateMachine(body, default);
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("machine", out _));
    }

    [Fact]
    public async Task CreateMachine_RequiresNameAndBaseUrl()
    {
        var body = ToJsonElement(new { name = "", base_url = "" });
        var result = await _ctrl.CreateMachine(body, default);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task CreateMachine_RejectsInvalidScheme()
    {
        var body = ToJsonElement(new { name = "Test", base_url = "ftp://example.com" });
        var result = await _ctrl.CreateMachine(body, default);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task GetMachines_ReturnsEmptyList_Initially()
    {
        var result = await _ctrl.GetMachines(default);
        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(ok.Value);
    }

    [Fact]
    public async Task GetMachine_ReturnsNotFound_ForMissingId()
    {
        var result = await _ctrl.GetMachine("nonexistent", default);
        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task GetMachine_ReturnsCreatedMachine()
    {
        var body = ToJsonElement(new { name = "MyMachine", base_url = "https://machine.example.com" });
        await _ctrl.CreateMachine(body, default);

        var machines = await _db.GetMachinesAsync();
        Assert.Single(machines);

        var result = await _ctrl.GetMachine(machines[0].Id, default);
        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task UpdateMachine_ReturnsNotFound_ForMissingId()
    {
        var body = ToJsonElement(new { name = "Updated" });
        var result = await _ctrl.UpdateMachine("missing", body, default);
        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task UpdateMachine_UpdatesName()
    {
        var createBody = ToJsonElement(new { name = "Original", base_url = "https://m.example.com" });
        await _ctrl.CreateMachine(createBody, default);
        var machines = await _db.GetMachinesAsync();
        var id = machines[0].Id;

        var updateBody = ToJsonElement(new { name = "Updated" });
        var result = await _ctrl.UpdateMachine(id, updateBody, default);
        var ok = Assert.IsType<OkObjectResult>(result);

        var json = JsonSerializer.Serialize(ok.Value);
        Assert.Contains("Updated", json);
    }

    [Fact]
    public async Task UpdateMachine_RejectsInvalidBaseUrl()
    {
        var createBody = ToJsonElement(new { name = "Test", base_url = "https://m.example.com" });
        await _ctrl.CreateMachine(createBody, default);
        var machines = await _db.GetMachinesAsync();

        var updateBody = ToJsonElement(new { base_url = "not-a-url" });
        var result = await _ctrl.UpdateMachine(machines[0].Id, updateBody, default);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task DeleteMachine_ReturnsNotFound_ForMissingId()
    {
        var result = await _ctrl.DeleteMachine("missing", default);
        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task DeleteMachine_RemovesMachine()
    {
        var body = ToJsonElement(new { name = "ToDelete", base_url = "https://del.example.com" });
        await _ctrl.CreateMachine(body, default);
        var machines = await _db.GetMachinesAsync();
        Assert.Single(machines);

        var result = await _ctrl.DeleteMachine(machines[0].Id, default);
        Assert.IsType<OkObjectResult>(result);

        var after = await _db.GetMachinesAsync();
        Assert.Empty(after);
    }

    // -----------------------------------------------------------------------
    // Snapshot + verify
    // -----------------------------------------------------------------------

    [Fact]
    public async Task GetSnapshot_ReturnsNotFound_ForMissingMachine()
    {
        var result = await _ctrl.GetSnapshot("missing", default);
        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task GetSnapshot_ReturnsNullSnapshot_ForNewMachine()
    {
        var body = ToJsonElement(new { name = "Snap", base_url = "https://snap.example.com" });
        await _ctrl.CreateMachine(body, default);
        var machines = await _db.GetMachinesAsync();

        var result = await _ctrl.GetSnapshot(machines[0].Id, default);
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("snapshot").ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task VerifyMachine_ReturnsNotFound_ForMissingMachine()
    {
        var result = await _ctrl.VerifyMachine("missing", default);
        Assert.IsType<NotFoundObjectResult>(result);
    }

    // -----------------------------------------------------------------------
    // SSRF protection — base_url scheme validation
    // -----------------------------------------------------------------------

    [Fact]
    public async Task CreateMachine_RejectsFileScheme()
    {
        var body = ToJsonElement(new { name = "Evil", base_url = "file:///etc/passwd" });
        var result = await _ctrl.CreateMachine(body, default);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task CreateMachine_RejectsJavascriptScheme()
    {
        var body = ToJsonElement(new { name = "XSS", base_url = "javascript:alert(1)" });
        var result = await _ctrl.CreateMachine(body, default);
        Assert.IsType<BadRequestObjectResult>(result);
    }
}
