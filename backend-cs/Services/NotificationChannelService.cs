using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DriveChill.Utils;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

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
    private static readonly HashSet<string> ValidTypes = ["ntfy", "discord", "slack", "generic_webhook", "mqtt"];
    private readonly DbService _db;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<NotificationChannelService> _logger;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, MqttClientWrapper> _mqttClients = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, HashSet<string>> _haAdvertised = new();
    private readonly SemaphoreSlim _mqttConnectLock = new(1, 1);

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
            "mqtt"            => await SendMqttAsync(ch, sensorName, value, threshold, test, ct),
            _ => false,
        };
    }

    private async Task<bool> SendNtfyAsync(Dictionary<string, JsonElement> config,
        string sensorName, double value, double threshold, bool test, CancellationToken ct)
    {
        var url = GetStr(config, "url")?.TrimEnd('/') ?? "";
        var topic = GetStr(config, "topic") ?? "";
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(topic)) return false;
        var (ntfyValid, ssrfErr) = await UrlSecurity.TryValidateOutboundHttpUrlAsync(url, allowPrivateTargets: false);
        if (!ntfyValid)
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
        var (discordValid, ssrfErr) = await UrlSecurity.TryValidateOutboundHttpUrlAsync(webhookUrl, allowPrivateTargets: false);
        if (!discordValid)
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
        var (slackValid, ssrfErr) = await UrlSecurity.TryValidateOutboundHttpUrlAsync(webhookUrl, allowPrivateTargets: false);
        if (!slackValid)
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
        var (genericValid, ssrfErr) = await UrlSecurity.TryValidateOutboundHttpUrlAsync(url, allowPrivateTargets: false);
        if (!genericValid)
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

    // ── MQTT ─────────────────────────────────────────────────────────────

    private async Task<MqttClientWrapper?> GetOrCreateMqttClientAsync(NotificationChannel ch, CancellationToken ct)
    {
        if (_mqttClients.TryGetValue(ch.Id, out var existing) && existing.IsConnected)
            return existing;

        var brokerUrl = GetStr(ch.Config, "broker_url") ?? "";
        if (string.IsNullOrEmpty(brokerUrl)) return null;

        // SSRF check: rewrite scheme to http:// so the shared validator
        // can resolve the hostname and block private/loopback IPs.
        var checkUrl = System.Text.RegularExpressions.Regex.Replace(
            brokerUrl, @"^(mqtts?|ssl)://", "http://",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var (mqttValid, ssrfReason) = await UrlSecurity.TryValidateOutboundHttpUrlAsync(checkUrl, allowPrivateTargets: false);
        if (!mqttValid)
        {
            _logger.LogWarning("MQTT connection blocked (SSRF): {Reason}", ssrfReason);
            return null;
        }

        await _mqttConnectLock.WaitAsync(ct);
        try
        {
            // Double-check after acquiring lock
            if (_mqttClients.TryGetValue(ch.Id, out var rechecked) && rechecked.IsConnected)
                return rechecked;

            var uri = new Uri(brokerUrl);
            var hostname = uri.Host;
            var port = uri.Port > 0 ? uri.Port : (uri.Scheme == "mqtts" ? 8883 : 1883);
            var useTls = uri.Scheme is "mqtts" or "ssl";
            var username = GetStr(ch.Config, "username");
            var password = GetStr(ch.Config, "password");
            var clientId = GetStr(ch.Config, "client_id") ?? $"drivechill-{ch.Id[..Math.Min(8, ch.Id.Length)]}";

            var factory = new MqttFactory();
            var client = factory.CreateMqttClient();

            var optionsBuilder = new MqttClientOptionsBuilder()
                .WithTcpServer(hostname, port)
                .WithClientId(clientId);

            if (useTls)
                optionsBuilder.WithTlsOptions(o => { });

            if (!string.IsNullOrEmpty(username))
                optionsBuilder.WithCredentials(username, password ?? "");

            await client.ConnectAsync(optionsBuilder.Build(), ct);
            var wrapper = new MqttClientWrapper(client);
            _mqttClients[ch.Id] = wrapper;
            _logger.LogInformation("MQTT connected to {Host}:{Port} for channel {Name}", hostname, port, ch.Name);
            return wrapper;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MQTT connection failed for channel {Name}", ch.Name);
            return null;
        }
        finally
        {
            _mqttConnectLock.Release();
        }
    }

    private async Task<bool> SendMqttAsync(NotificationChannel ch, string sensorName,
        double value, double threshold, bool test, CancellationToken ct)
    {
        var wrapper = await GetOrCreateMqttClientAsync(ch, ct);
        if (wrapper is null) return false;

        var topicPrefix = GetStr(ch.Config, "topic_prefix") ?? "drivechill";
        var qos = Math.Clamp(GetInt(ch.Config, "qos", 1), 0, 2);
        var retain = GetBool(ch.Config, "retain", false);

        var payload = JsonSerializer.Serialize(new
        {
            source = "drivechill",
            type = test ? "test_alert" : "alert",
            sensor_name = sensorName,
            value,
            threshold,
            message = $"{sensorName}: {value}°C (threshold: {threshold}°C)",
        });

        try
        {
            var msg = new MqttApplicationMessageBuilder()
                .WithTopic($"{topicPrefix}/alerts")
                .WithPayload(Encoding.UTF8.GetBytes(payload))
                .WithQualityOfServiceLevel((MqttQualityOfServiceLevel)qos)
                .WithRetainFlag(retain)
                .Build();

            await wrapper.Client.PublishAsync(msg, ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MQTT publish failed for channel {Name}", ch.Name);
            _mqttClients.TryRemove(ch.Id, out _);
            try { await wrapper.Client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), ct); } catch { }
            return false;
        }
    }

    private async Task PublishHaDiscoveryAsync(NotificationChannel ch, MqttClientWrapper wrapper,
        IReadOnlyList<TelemetryReading> readings, CancellationToken ct)
    {
        if (!GetBool(ch.Config, "ha_discovery", false)) return;

        var haPrefix = GetStr(ch.Config, "ha_discovery_prefix") ?? "homeassistant";
        var topicPrefix = GetStr(ch.Config, "topic_prefix") ?? "drivechill";

        var device = new Dictionary<string, object>
        {
            ["identifiers"] = new[] { "drivechill" },
            ["name"] = "DriveChill",
            ["manufacturer"] = "DriveChill",
            ["model"] = "Fan Controller",
        };

        var currentIds = new HashSet<string>();
        foreach (var r in readings)
        {
            currentIds.Add(r.SensorId);
            var isFan = r.SensorType.Contains("fan", StringComparison.OrdinalIgnoreCase);
            var component = isFan ? "fan" : "sensor";

            object configPayload;
            if (isFan)
            {
                configPayload = new Dictionary<string, object>
                {
                    ["name"] = $"DriveChill {r.SensorName}",
                    ["unique_id"] = $"drivechill_{r.SensorId}",
                    ["state_topic"] = $"{topicPrefix}/sensors/{r.SensorId}",
                    ["value_template"] = "{{ value_json.value }}",
                    ["percentage_state_topic"] = $"{topicPrefix}/sensors/{r.SensorId}",
                    ["percentage_value_template"] = "{{ value_json.value }}",
                    ["command_topic"] = $"{topicPrefix}/commands/fans/{r.SensorId}/speed",
                    ["percentage_command_topic"] = $"{topicPrefix}/commands/fans/{r.SensorId}/speed",
                    ["device"] = device,
                };
            }
            else
            {
                configPayload = new Dictionary<string, object>
                {
                    ["name"] = $"DriveChill {r.SensorName}",
                    ["unique_id"] = $"drivechill_{r.SensorId}",
                    ["state_topic"] = $"{topicPrefix}/sensors/{r.SensorId}",
                    ["value_template"] = "{{ value_json.value }}",
                    ["unit_of_measurement"] = r.Unit,
                    ["device"] = device,
                };
            }

            var configTopic = $"{haPrefix}/{component}/drivechill_{r.SensorId}/config";
            var msg = new MqttApplicationMessageBuilder()
                .WithTopic(configTopic)
                .WithPayload(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(configPayload)))
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .WithRetainFlag(true)
                .Build();
            await wrapper.Client.PublishAsync(msg, ct);
        }

        // Remove previously advertised IDs no longer present
        if (_haAdvertised.TryGetValue(ch.Id, out var previous))
        {
            foreach (var oldId in previous)
            {
                if (currentIds.Contains(oldId)) continue;
                // Remove from both component types since we don't track which it was
                foreach (var comp in new[] { "sensor", "fan" })
                {
                    var removeTopic = $"{haPrefix}/{comp}/drivechill_{oldId}/config";
                    var removeMsg = new MqttApplicationMessageBuilder()
                        .WithTopic(removeTopic)
                        .WithPayload(Array.Empty<byte>())
                        .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                        .WithRetainFlag(true)
                        .Build();
                    await wrapper.Client.PublishAsync(removeMsg, ct);
                }
            }
        }

        _haAdvertised[ch.Id] = currentIds;
    }

    public async Task<int> PublishTelemetryAsync(IReadOnlyList<TelemetryReading> readings, CancellationToken ct = default)
    {
        var channels = await ListAsync(ct);
        int successes = 0;
        foreach (var ch in channels)
        {
            if (!ch.Enabled || ch.Type != "mqtt") continue;
            if (!GetBool(ch.Config, "publish_telemetry", false)) continue;

            var wrapper = await GetOrCreateMqttClientAsync(ch, ct);
            if (wrapper is null) continue;

            var topicPrefix = GetStr(ch.Config, "topic_prefix") ?? "drivechill";
            var qos = Math.Clamp(GetInt(ch.Config, "qos", 0), 0, 2);
            var retain = GetBool(ch.Config, "retain", false);

            try
            {
                // Publish HA discovery messages before telemetry
                await PublishHaDiscoveryAsync(ch, wrapper, readings, ct);

                foreach (var r in readings)
                {
                    var payload = JsonSerializer.Serialize(r);
                    var msg = new MqttApplicationMessageBuilder()
                        .WithTopic($"{topicPrefix}/sensors/{r.SensorId}")
                        .WithPayload(Encoding.UTF8.GetBytes(payload))
                        .WithQualityOfServiceLevel((MqttQualityOfServiceLevel)qos)
                        .WithRetainFlag(retain)
                        .Build();
                    await wrapper.Client.PublishAsync(msg, ct);
                }
                successes++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MQTT telemetry publish failed for channel {Name}", ch.Name);
                _mqttClients.TryRemove(ch.Id, out _);
                try { await wrapper.Client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), ct); } catch { }
            }
        }
        return successes;
    }

    public async Task CloseMqttClientsAsync()
    {
        foreach (var (_, wrapper) in _mqttClients)
        {
            try { await wrapper.Client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build()); } catch { }
        }
        _mqttClients.Clear();
    }

    private static string? GetStr(Dictionary<string, JsonElement> config, string key)
        => config.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int GetInt(Dictionary<string, JsonElement> config, string key, int defaultValue)
    {
        if (config.TryGetValue(key, out var v))
        {
            if (v.ValueKind == JsonValueKind.Number) return v.GetInt32();
            if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var parsed)) return parsed;
        }
        return defaultValue;
    }

    private static bool GetBool(Dictionary<string, JsonElement> config, string key, bool defaultValue)
    {
        if (config.TryGetValue(key, out var v))
        {
            if (v.ValueKind is JsonValueKind.True or JsonValueKind.False) return v.GetBoolean();
            if (v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out var parsed)) return parsed;
        }
        return defaultValue;
    }
}

internal sealed class MqttClientWrapper(IMqttClient client)
{
    public IMqttClient Client { get; } = client;
    public bool IsConnected => Client.IsConnected;
}

public sealed class TelemetryReading
{
    [JsonPropertyName("sensor_id")]   public string SensorId { get; set; } = "";
    [JsonPropertyName("sensor_name")] public string SensorName { get; set; } = "";
    [JsonPropertyName("sensor_type")] public string SensorType { get; set; } = "";
    [JsonPropertyName("value")]       public double Value { get; set; }
    [JsonPropertyName("unit")]        public string Unit { get; set; } = "";
}
