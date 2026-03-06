using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DriveChill.Utils;
using Microsoft.Extensions.Logging;

namespace DriveChill.Services;

public sealed class NotificationChannel
{
    [JsonPropertyName("id")]          public string Id { get; set; } = "";
    [JsonPropertyName("type")]        public string Type { get; set; } = "";
    [JsonPropertyName("name")]        public string Name { get; set; } = "";
    [JsonPropertyName("enabled")]     public bool Enabled { get; set; } = true;
    [JsonPropertyName("config")]      public Dictionary<string, JsonElement> Config { get; set; } = [];
    [JsonPropertyName("created_at")]  public string CreatedAt { get; set; } = "";
    [JsonPropertyName("updated_at")]  public string UpdatedAt { get; set; } = "";
}

/// <summary>
/// Manages notification channels (ntfy, Discord, Slack, generic webhook).
/// Uses DbService for persistence, HttpClient for delivery.
/// </summary>
public sealed class NotificationChannelService
{
    private static readonly HashSet<string> ValidTypes = ["ntfy", "discord", "slack", "generic_webhook"];
    private readonly DbService _db;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<NotificationChannelService> _logger;

    public NotificationChannelService(DbService db, IHttpClientFactory httpFactory,
                                       ILogger<NotificationChannelService> logger)
    {
        _db         = db;
        _httpFactory = httpFactory;
        _logger     = logger;
    }

    public static bool IsValidType(string type) => ValidTypes.Contains(type);

    public async Task<List<NotificationChannel>> ListAsync(CancellationToken ct = default)
        => await _db.GetNotificationChannelsAsync(ct);

    public async Task<NotificationChannel?> GetAsync(string id, CancellationToken ct = default)
        => await _db.GetNotificationChannelAsync(id, ct);

    public async Task<NotificationChannel> CreateAsync(string id, string type, string name,
                                                        bool enabled, Dictionary<string, JsonElement> config,
                                                        CancellationToken ct = default)
    {
        await _db.CreateNotificationChannelAsync(id, type, name, enabled, config, ct);
        return (await GetAsync(id, ct))!;
    }

    public async Task<bool> UpdateAsync(string id, string? name, bool? enabled,
                                         Dictionary<string, JsonElement>? config,
                                         CancellationToken ct = default)
        => await _db.UpdateNotificationChannelAsync(id, name, enabled, config, ct);

    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
        => await _db.DeleteNotificationChannelAsync(id, ct);

    public async Task<int> SendAlertAllAsync(string sensorName, double value, double threshold,
                                              CancellationToken ct = default)
    {
        var channels = await ListAsync(ct);
        int successes = 0;
        foreach (var ch in channels)
        {
            if (!ch.Enabled) continue;
            try
            {
                if (await SendAsync(ch, sensorName, value, threshold, false, ct))
                    successes++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send via channel {Name} ({Type})", ch.Name, ch.Type);
            }
        }
        return successes;
    }

    public async Task<(bool Success, string? Error)> SendTestAsync(string channelId,
                                                                     CancellationToken ct = default)
    {
        var ch = await GetAsync(channelId, ct);
        if (ch is null) return (false, "Channel not found");
        try
        {
            var ok = await SendAsync(ch, "Test Sensor", 85.0, 80.0, true, ct);
            return (ok, ok ? null : "Delivery failed");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private async Task<bool> SendAsync(NotificationChannel ch, string sensorName,
                                        double value, double threshold, bool test,
                                        CancellationToken ct)
    {
        return ch.Type switch
        {
            "ntfy"            => await SendNtfyAsync(ch.Config, sensorName, value, threshold, test, ct),
            "discord"         => await SendDiscordAsync(ch.Config, sensorName, value, threshold, test, ct),
            "slack"           => await SendSlackAsync(ch.Config, sensorName, value, threshold, test, ct),
            "generic_webhook" => await SendGenericAsync(ch.Config, sensorName, value, threshold, test, ct),
            _ => false,
        };
    }

    private async Task<bool> SendNtfyAsync(Dictionary<string, JsonElement> config,
        string sensorName, double value, double threshold, bool test, CancellationToken ct)
    {
        var url = GetStr(config, "url")?.TrimEnd('/') ?? "";
        var topic = GetStr(config, "topic") ?? "";
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(topic)) return false;
        if (!UrlSecurity.TryValidateOutboundHttpUrl(url, allowPrivateTargets: false, out var ssrfErr))
        {
            _logger.LogWarning("ntfy delivery blocked (SSRF): {Reason}", ssrfErr);
            return false;
        }

        var client = _httpFactory.CreateClient("webhooks");
        var req = new HttpRequestMessage(HttpMethod.Post, $"{url}/{topic}");
        req.Content = new StringContent($"{sensorName}: {value}°C (threshold: {threshold}°C)");
        req.Headers.TryAddWithoutValidation("Title", test ? "DriveChill Test Alert" : "DriveChill Alert");
        req.Headers.TryAddWithoutValidation("Priority", GetStr(config, "priority") ?? "high");
        req.Headers.TryAddWithoutValidation("Tags", "thermometer,warning");
        var token = GetStr(config, "token");
        if (!string.IsNullOrEmpty(token))
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");

        var resp = await client.SendAsync(req, ct);
        return resp.StatusCode is System.Net.HttpStatusCode.OK or System.Net.HttpStatusCode.Created;
    }

    private async Task<bool> SendDiscordAsync(Dictionary<string, JsonElement> config,
        string sensorName, double value, double threshold, bool test, CancellationToken ct)
    {
        var webhookUrl = GetStr(config, "webhook_url") ?? "";
        if (string.IsNullOrEmpty(webhookUrl)) return false;
        if (!UrlSecurity.TryValidateOutboundHttpUrl(webhookUrl, allowPrivateTargets: false, out var ssrfErr))
        {
            _logger.LogWarning("Discord delivery blocked (SSRF): {Reason}", ssrfErr);
            return false;
        }

        var title = test ? "DriveChill Test Alert" : "DriveChill Alert";
        var payload = new { embeds = new[] { new { title, description = $"**{sensorName}** reached {value}°C (threshold: {threshold}°C)", color = 0xFF4444 } } };

        var client = _httpFactory.CreateClient("webhooks");
        var resp = await client.PostAsJsonAsync(webhookUrl, payload, ct);
        return resp.StatusCode is System.Net.HttpStatusCode.OK or System.Net.HttpStatusCode.NoContent;
    }

    private async Task<bool> SendSlackAsync(Dictionary<string, JsonElement> config,
        string sensorName, double value, double threshold, bool test, CancellationToken ct)
    {
        var webhookUrl = GetStr(config, "webhook_url") ?? "";
        if (string.IsNullOrEmpty(webhookUrl)) return false;
        if (!UrlSecurity.TryValidateOutboundHttpUrl(webhookUrl, allowPrivateTargets: false, out var ssrfErr))
        {
            _logger.LogWarning("Slack delivery blocked (SSRF): {Reason}", ssrfErr);
            return false;
        }

        var prefix = test ? ":test_tube: " : ":warning: ";
        var payload = new { text = $"{prefix}*DriveChill Alert*\n{sensorName}: {value}°C (threshold: {threshold}°C)" };

        var client = _httpFactory.CreateClient("webhooks");
        var resp = await client.PostAsJsonAsync(webhookUrl, payload, ct);
        return resp.IsSuccessStatusCode;
    }

    private async Task<bool> SendGenericAsync(Dictionary<string, JsonElement> config,
        string sensorName, double value, double threshold, bool test, CancellationToken ct)
    {
        var url = GetStr(config, "url") ?? "";
        if (string.IsNullOrEmpty(url)) return false;
        if (!UrlSecurity.TryValidateOutboundHttpUrl(url, allowPrivateTargets: false, out var ssrfErr))
        {
            _logger.LogWarning("Generic webhook delivery blocked (SSRF): {Reason}", ssrfErr);
            return false;
        }

        var payload = new { source = "drivechill", test, sensor_name = sensorName, value, threshold,
                            message = $"{sensorName}: {value}°C (threshold: {threshold}°C)" };

        // Serialize once so HMAC is computed over the exact bytes sent
        var body = JsonSerializer.Serialize(payload);
        var bodyBytes = Encoding.UTF8.GetBytes(body);

        var client = _httpFactory.CreateClient("webhooks");
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new ByteArrayContent(bodyBytes),
        };
        req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json")
            { CharSet = "utf-8" };

        var secret = GetStr(config, "hmac_secret");
        if (!string.IsNullOrEmpty(secret))
        {
            var sig = Convert.ToHexString(
                HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), bodyBytes)).ToLowerInvariant();
            req.Headers.TryAddWithoutValidation("X-DriveChill-Signature", $"sha256={sig}");
        }

        var resp = await client.SendAsync(req, ct);
        return resp.IsSuccessStatusCode;
    }

    private static string? GetStr(Dictionary<string, JsonElement> config, string key)
        => config.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
