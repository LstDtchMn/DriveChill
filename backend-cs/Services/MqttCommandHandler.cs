using System.Text;
using System.Text.Json;
using DriveChill.Hardware;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Client;

namespace DriveChill.Services;

/// <summary>
/// Background service that subscribes to MQTT command topics and dispatches
/// fan speed, profile activation, and fan release commands.
///
/// Allows external systems (Home Assistant, Node-RED) to control DriveChill via MQTT.
/// Creates separate subscriber clients (not shared with publish clients).
/// </summary>
public sealed class MqttCommandHandler : BackgroundService
{
    private readonly NotificationChannelService _channelSvc;
    private readonly FanService _fans;
    private readonly SettingsStore _store;
    private readonly IHardwareBackend _hw;
    private readonly ILogger<MqttCommandHandler> _log;

    private const int MaxCommandsPerSecond = 10;
    private const int ChannelCheckIntervalSeconds = 30;

    public MqttCommandHandler(
        NotificationChannelService channelSvc,
        FanService fans,
        SettingsStore store,
        IHardwareBackend hw,
        ILogger<MqttCommandHandler> log)
    {
        _channelSvc = channelSvc;
        _fans       = fans;
        _store      = store;
        _hw         = hw;
        _log        = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var activeTasks = new Dictionary<string, (CancellationTokenSource Cts, Task Task)>();

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var channels = await _channelSvc.ListAsync(stoppingToken);
                    var currentIds = new HashSet<string>();

                    foreach (var ch in channels)
                    {
                        if (!ch.Enabled || ch.Type != "mqtt") continue;
                        if (!GetBool(ch.Config, "mqtt_subscribe", false)) continue;

                        currentIds.Add(ch.Id);

                        // Start or restart task if needed
                        if (!activeTasks.TryGetValue(ch.Id, out var entry) || entry.Task.IsCompleted)
                        {
                            entry.Cts?.Dispose();
                            var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                            var task = SubscribeLoopAsync(ch.Config, ch.Name, cts.Token);
                            activeTasks[ch.Id] = (cts, task);
                        }
                    }

                    // Cancel tasks for removed/disabled channels
                    foreach (var id in activeTasks.Keys.ToList())
                    {
                        if (currentIds.Contains(id)) continue;
                        if (activeTasks.TryGetValue(id, out var old))
                        {
                            old.Cts.Cancel();
                            old.Cts.Dispose();
                            activeTasks.Remove(id);
                        }
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "MQTT command handler channel check error");
                }

                await Task.Delay(TimeSpan.FromSeconds(ChannelCheckIntervalSeconds), stoppingToken);
            }
        }
        finally
        {
            foreach (var (_, (cts, _)) in activeTasks)
            {
                cts.Cancel();
                cts.Dispose();
            }
        }
    }

    private async Task SubscribeLoopAsync(
        Dictionary<string, JsonElement> config,
        string channelName,
        CancellationToken ct)
    {
        var brokerUrl = GetStr(config, "broker_url") ?? "";
        if (string.IsNullOrEmpty(brokerUrl)) return;

        Uri uri;
        try { uri = new Uri(brokerUrl); }
        catch { return; }

        var hostname = uri.Host;
        var port = uri.Port > 0 ? uri.Port : (uri.Scheme == "mqtts" ? 8883 : 1883);
        var useTls = uri.Scheme is "mqtts" or "ssl";
        var username = GetStr(config, "username");
        var password = GetStr(config, "password");
        var topicPrefix = GetStr(config, "topic_prefix") ?? "drivechill";
        var clientId = $"drivechill-sub-{channelName[..Math.Min(8, channelName.Length)]}";

        var factory = new MqttFactory();
        using var client = factory.CreateMqttClient();

        var optionsBuilder = new MqttClientOptionsBuilder()
            .WithTcpServer(hostname, port)
            .WithClientId(clientId);

        if (useTls)
            optionsBuilder.WithTlsOptions(o => { });
        if (!string.IsNullOrEmpty(username))
            optionsBuilder.WithCredentials(username, password ?? "");

        // Rate tracking
        var rateTracker = new List<double>();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // Set up message handler before connecting
        client.ApplicationMessageReceivedAsync += async e =>
        {
            // Rate limit
            var now = stopwatch.Elapsed.TotalSeconds;
            rateTracker.RemoveAll(t => now - t >= 1.0);
            if (rateTracker.Count >= MaxCommandsPerSecond)
            {
                _log.LogWarning("MQTT command rate limit exceeded for channel {Channel}, dropping", channelName);
                return;
            }
            rateTracker.Add(now);

            var topic = e.ApplicationMessage.Topic;
            var payload = e.ApplicationMessage.PayloadSegment.Count > 0
                ? Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment)
                : null;

            try
            {
                DispatchCommand(topic, payload, topicPrefix);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "MQTT command dispatch error");
            }

            await Task.CompletedTask;
        };

        try
        {
            await client.ConnectAsync(optionsBuilder.Build(), ct);
            await client.SubscribeAsync(
                new MqttTopicFilterBuilder()
                    .WithTopic($"{topicPrefix}/commands/#")
                    .Build(),
                ct);

            _log.LogInformation("MQTT subscribed to {Prefix}/commands/# for channel {Channel}",
                topicPrefix, channelName);

            // Keep alive until cancelled
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                if (!client.IsConnected) break;
            }
        }
        catch (OperationCanceledException) { /* expected on shutdown */ }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "MQTT subscribe loop error for channel {Channel}", channelName);
        }
        finally
        {
            try
            {
                if (client.IsConnected)
                    await client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build());
            }
            catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Parse and dispatch a single MQTT command. Called from the message handler.
    /// </summary>
    internal void DispatchCommand(string topic, string? payload, string prefix)
    {
        var expectedStart = $"{prefix}/";
        if (!topic.StartsWith(expectedStart)) return;

        var suffix = topic[expectedStart.Length..];

        // commands/fans/release — no payload needed
        if (suffix == "commands/fans/release")
        {
            _fans.ReleaseFanControl();
            _log.LogInformation("MQTT: released fan control");
            return;
        }

        if (string.IsNullOrEmpty(payload))
        {
            _log.LogDebug("Empty MQTT payload for topic {Topic}", topic);
            return;
        }

        JsonElement data;
        try
        {
            data = JsonSerializer.Deserialize<JsonElement>(payload);
        }
        catch (JsonException)
        {
            _log.LogWarning("Malformed JSON on MQTT command topic {Topic}", topic);
            return;
        }

        // commands/fans/{fan_id}/speed
        if (suffix.StartsWith("commands/fans/") && suffix.EndsWith("/speed"))
        {
            var parts = suffix.Split('/');
            if (parts.Length == 4)
            {
                var fanId = parts[2];
                if (data.TryGetProperty("percent", out var percentEl)
                    && percentEl.ValueKind == JsonValueKind.Number)
                {
                    var percent = percentEl.GetDouble();
                    if (percent >= 0 && percent <= 100)
                    {
                        _hw.SetFanSpeed(fanId, percent);
                        _log.LogInformation("MQTT: set fan {FanId} to {Percent}%", fanId, percent);
                    }
                    else
                    {
                        _log.LogWarning("Invalid percent in MQTT fan speed command: {Percent}", percent);
                    }
                }
                else
                {
                    _log.LogWarning("Missing or invalid 'percent' in MQTT fan speed command");
                }
            }
        }
        // commands/profiles/activate
        else if (suffix == "commands/profiles/activate")
        {
            if (data.TryGetProperty("profile_id", out var pidEl)
                && pidEl.ValueKind == JsonValueKind.String)
            {
                var profileId = pidEl.GetString()!;
                var profiles = _store.LoadProfiles().ToList();
                var profile = profiles.FirstOrDefault(p => p.Id == profileId);
                if (profile != null)
                {
                    // Mark active and save
                    foreach (var p in profiles) p.IsActive = p.Id == profileId;
                    _store.SaveProfiles(profiles);
                    _fans.SetCurves(profile.Curves);
                    _log.LogInformation("MQTT: activated profile {ProfileId}", profileId);
                }
                else
                {
                    _log.LogWarning("MQTT: profile {ProfileId} not found", profileId);
                }
            }
            else
            {
                _log.LogWarning("Missing or invalid 'profile_id' in MQTT activate command");
            }
        }
        else
        {
            _log.LogDebug("Unknown MQTT command topic: {Topic}", topic);
        }
    }

    private static string? GetStr(Dictionary<string, JsonElement> config, string key)
        => config.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

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
