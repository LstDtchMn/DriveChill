using Microsoft.AspNetCore.Mvc;
using DriveChill.Models;
using DriveChill.Services;
using System.Text.Json;

namespace DriveChill.Api;

[ApiController]
[Route("api/webhooks")]
public sealed class WebhooksController : ControllerBase
{
    private readonly WebhookService _webhooks;

    public WebhooksController(WebhookService webhooks)
    {
        _webhooks = webhooks;
    }

    [HttpGet]
    public IActionResult GetConfig()
    {
        return Ok(new { config = _webhooks.GetConfig() });
    }

    [HttpPut]
    public IActionResult UpdateConfig([FromBody] JsonElement body)
    {
        var current = _webhooks.GetConfigRaw();
        var cfg = new WebhookConfig
        {
            Enabled = ReadBool(body, "enabled", current.Enabled),
            TargetUrl = ReadString(body, "target_url", current.TargetUrl) ?? "",
            TimeoutSeconds = ReadDouble(body, "timeout_seconds", current.TimeoutSeconds),
            MaxRetries = ReadInt(body, "max_retries", current.MaxRetries),
            RetryBackoffSeconds = ReadDouble(body, "retry_backoff_seconds", current.RetryBackoffSeconds),
            UpdatedAt = current.UpdatedAt,
        };

        if (body.TryGetProperty("signing_secret", out var secretNode))
        {
            cfg.SigningSecret = secretNode.ValueKind == JsonValueKind.Null
                ? null
                : secretNode.GetString();
        }
        else
        {
            cfg.SigningSecret = current.SigningSecret;
        }

        try
        {
            var updated = _webhooks.UpdateConfig(cfg);
            return Ok(new { success = true, config = WebhookConfigView.FromConfig(updated) });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    private static bool ReadBool(JsonElement body, string name, bool fallback)
    {
        if (!body.TryGetProperty(name, out var node)) return fallback;
        return node.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => fallback,
        };
    }

    private static string? ReadString(JsonElement body, string name, string? fallback)
    {
        if (!body.TryGetProperty(name, out var node)) return fallback;
        return node.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => node.GetString(),
            _ => fallback,
        };
    }

    private static int ReadInt(JsonElement body, string name, int fallback)
    {
        if (!body.TryGetProperty(name, out var node)) return fallback;
        return node.TryGetInt32(out var value) ? value : fallback;
    }

    private static double ReadDouble(JsonElement body, string name, double fallback)
    {
        if (!body.TryGetProperty(name, out var node)) return fallback;
        return node.TryGetDouble(out var value) ? value : fallback;
    }

    [HttpGet("deliveries")]
    public IActionResult GetDeliveries([FromQuery] int limit = 100)
    {
        return Ok(new { deliveries = _webhooks.GetDeliveries(limit) });
    }
}
