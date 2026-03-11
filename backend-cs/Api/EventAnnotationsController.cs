using DriveChill.Models;
using DriveChill.Services;
using Microsoft.AspNetCore.Mvc;

namespace DriveChill.Api;

[ApiController]
[Route("api/annotations")]
public sealed class EventAnnotationsController : ControllerBase
{
    private readonly DbService _db;

    public EventAnnotationsController(DbService db) => _db = db;

    [HttpGet("")]
    public async Task<IActionResult> List([FromQuery] string? start = null, [FromQuery] string? end = null, CancellationToken ct = default)
    {
        var annotations = await _db.ListAnnotationsAsync(start, end, ct);
        return Ok(annotations.Select(a => new
        {
            id = a.Id,
            timestamp_utc = a.TimestampUtc,
            label = a.Label,
            description = a.Description,
            created_at = a.CreatedAt,
        }));
    }

    [HttpPost("")]
    public async Task<IActionResult> Create([FromBody] AnnotationRequest body, CancellationToken ct = default)
    {
        var error = Validate(body, out var normalizedTimestamp);
        if (error is not null)
            return UnprocessableEntity(new { detail = error });

        var annotation = new AnnotationRecord
        {
            Id = $"ann_{Guid.NewGuid().ToString("N")[..12]}",
            EventType = "annotation",
            TimestampUtc = normalizedTimestamp!,
            Label = body.Label,
            Description = body.Description,
            CreatedAt = DateTimeOffset.UtcNow.ToString("o"),
        };

        await _db.CreateAnnotationAsync(annotation, ct);
        return Ok(new
        {
            id = annotation.Id,
            timestamp_utc = annotation.TimestampUtc,
            label = annotation.Label,
            description = annotation.Description,
            created_at = annotation.CreatedAt,
        });
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct = default)
    {
        var deleted = await _db.DeleteAnnotationAsync(id, ct);
        return deleted ? NoContent() : NotFound(new { detail = "Annotation not found" });
    }

    private static string? Validate(AnnotationRequest body, out string? normalizedTimestamp)
    {
        normalizedTimestamp = null;
        if (string.IsNullOrWhiteSpace(body.TimestampUtc))
            return "timestamp_utc is required";
        if (string.IsNullOrWhiteSpace(body.Label))
            return "label is required";
        if (body.Label.Length > 200)
            return "label must be 200 characters or fewer";
        if (body.Description is { Length: > 1000 })
            return "description must be 1000 characters or fewer";

        if (!DateTimeOffset.TryParse(body.TimestampUtc, out var parsed))
            return "timestamp_utc must be a valid ISO-8601 datetime";

        normalizedTimestamp = parsed.ToUniversalTime().ToString("o");
        return null;
    }
}

public sealed class AnnotationRequest
{
    public string TimestampUtc { get; set; } = "";
    public string Label { get; set; } = "";
    public string? Description { get; set; }
}
