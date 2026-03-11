using System.IO;
using System.Text.Json;
using DriveChill.Api;
using DriveChill.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DriveChill.Tests;

public sealed class EventAnnotationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DbService _db;
    private readonly EventAnnotationsController _ctrl;

    public EventAnnotationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
        Environment.SetEnvironmentVariable("DRIVECHILL_DATA_DIR", _tempDir);

        var settings = new AppSettings();
        _db = new DbService(settings, NullLogger<DbService>.Instance);
        _ctrl = new EventAnnotationsController(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        Environment.SetEnvironmentVariable("DRIVECHILL_DATA_DIR", null);
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task CreateListDelete_RoundTripsAnnotation()
    {
        var create = await _ctrl.Create(new AnnotationRequest
        {
            TimestampUtc = "2026-03-10T12:00:00Z",
            Label = "Repasted CPU",
            Description = "Applied NT-H2",
        });

        var ok = Assert.IsType<OkObjectResult>(create);
        var createdJson = JsonSerializer.Serialize(ok.Value);
        using var createdDoc = JsonDocument.Parse(createdJson);
        var id = createdDoc.RootElement.GetProperty("id").GetString();
        Assert.Equal("Repasted CPU", createdDoc.RootElement.GetProperty("label").GetString());
        Assert.Equal("2026-03-10T12:00:00.0000000+00:00", createdDoc.RootElement.GetProperty("timestamp_utc").GetString());

        var list = await _ctrl.List(start: "2026-03-10T00:00:00Z");
        var listOk = Assert.IsType<OkObjectResult>(list);
        var listJson = JsonSerializer.Serialize(listOk.Value);
        using var listDoc = JsonDocument.Parse(listJson);
        Assert.Single(listDoc.RootElement.EnumerateArray());

        var delete = await _ctrl.Delete(id!);
        Assert.IsType<NoContentResult>(delete);
    }

    [Fact]
    public async Task Create_RejectsInvalidTimestamp()
    {
        var result = await _ctrl.Create(new AnnotationRequest
        {
            TimestampUtc = "not-a-date",
            Label = "Test",
        });

        Assert.IsType<UnprocessableEntityObjectResult>(result);
    }

    [Fact]
    public async Task Delete_ReturnsNotFound_ForMissingAnnotation()
    {
        var result = await _ctrl.Delete("ann_missing");
        Assert.IsType<NotFoundObjectResult>(result);
    }
}
