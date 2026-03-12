using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using DriveChill.Services;
using DriveChill.Utils;

namespace DriveChill.Api;

[ApiController]
[Route("api/notification-channels")]
public sealed class NotificationChannelsController : ControllerBase
{
    private readonly NotificationChannelService _svc;
    private readonly AppSettings _settings;
    private static readonly string[] UrlConfigKeys = ["url", "webhook_url", "broker_url"];

    public NotificationChannelsController(NotificationChannelService svc, AppSettings settings)
    {
        _svc = svc;
        _settings = settings;
    }

    /// <summary>Returns an error detail string if any URL-typed config field fails SSRF validation.</summary>
    private async Task<string?> ValidateConfigUrlsAsync(Dictionary<string, JsonElement>? config)
    {
        if (config is null) return null;
        foreach (var key in UrlConfigKeys)
        {
            if (config.TryGetValue(key, out var el) && el.ValueKind == JsonValueKind.String)
            {
                var val = el.GetString() ?? "";
                if (string.IsNullOrEmpty(val)) continue;

                // For broker_url, rewrite mqtt(s)://|ssl:// → http:// so the
                // SSRF validator can parse and resolve the hostname.
                var checkVal = val;
                if (key == "broker_url")
                    checkVal = System.Text.RegularExpressions.Regex.Replace(
                        val, @"^(mqtts?|ssl)://", "http://", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                var allowPrivate = key == "broker_url" && _settings.AllowPrivateBrokerTargets;
                var (valid, reason) = await UrlSecurity.TryValidateOutboundHttpUrlAsync(checkVal, allowPrivateTargets: allowPrivate);
                if (!valid)
                    return $"Config '{key}': {reason}";
            }
        }
        return null;
    }

    [HttpGet("")]
    public async Task<IActionResult> List(CancellationToken ct)
        => Ok(new { channels = await _svc.ListAsync(ct) });

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id, CancellationToken ct)
    {
        var ch = await _svc.GetAsync(id, ct);
        return ch is not null ? Ok(ch) : NotFound(new { detail = "Channel not found" });
    }

    [HttpPost("")]
    public async Task<IActionResult> Create([FromBody] CreateChannelRequest body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Name))
            return BadRequest(new { detail = "name is required" });
        if (!NotificationChannelService.IsValidType(body.Type))
            return BadRequest(new { detail = $"Invalid type. Must be one of: discord, generic_webhook, ntfy, slack" });
        var urlErr = await ValidateConfigUrlsAsync(body.Config);
        if (urlErr is not null) return BadRequest(new { detail = urlErr });

        var id = $"nc_{Guid.NewGuid().ToString()[..16]}";
        var ch = await _svc.CreateAsync(id, body.Type, body.Name, body.Enabled, body.Config ?? [], ct);
        return StatusCode(201, new { success = true, channel = ch });
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(string id, [FromBody] UpdateChannelRequest body, CancellationToken ct)
    {
        var urlErr = await ValidateConfigUrlsAsync(body.Config);
        if (urlErr is not null) return BadRequest(new { detail = urlErr });

        var ok = await _svc.UpdateAsync(id, body.Name, body.Enabled, body.Config, ct);
        if (!ok) return NotFound(new { detail = "Channel not found" });
        var ch = await _svc.GetAsync(id, ct);
        return Ok(new { success = true, channel = ch });
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        return await _svc.DeleteAsync(id, ct)
            ? Ok(new { success = true })
            : NotFound(new { detail = "Channel not found" });
    }

    [HttpPost("{id}/test")]
    public async Task<IActionResult> Test(string id, CancellationToken ct)
    {
        var (success, error) = await _svc.SendTestAsync(id, ct);
        return Ok(new { success, error });
    }
}

public sealed class CreateChannelRequest
{
    public string Type { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public Dictionary<string, JsonElement>? Config { get; set; }
}

public sealed class UpdateChannelRequest
{
    public string? Name { get; set; }
    public bool? Enabled { get; set; }
    public Dictionary<string, JsonElement>? Config { get; set; }
}
