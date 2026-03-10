using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DriveChill.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DriveChill.Tests;

/// <summary>
/// Tests for MQTT channel handling in <see cref="NotificationChannelService"/>.
/// Covers config parsing, channel listing, and telemetry skip logic.
/// Network I/O is not exercised — invalid broker URLs cause connection failure paths
/// which are caught and returned as false/0.
/// </summary>
public sealed class MqttChannelTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AppSettings _settings;
    private readonly DbService _db;
    private readonly NotificationChannelService _svc;

    public MqttChannelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
        Environment.SetEnvironmentVariable("DRIVECHILL_DATA_DIR", _tempDir);

        _settings = new AppSettings();
        _db       = new DbService(_settings, NullLogger<DbService>.Instance);
        _svc      = new NotificationChannelService(_db, new NullHttpClientFactory(),
                        NullLogger<NotificationChannelService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        Environment.SetEnvironmentVariable("DRIVECHILL_DATA_DIR", null);
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    // Helper to build a config dictionary from key/value pairs.
    private static Dictionary<string, JsonElement> Cfg(params (string key, object val)[] pairs)
    {
        var dict = new Dictionary<string, JsonElement>();
        foreach (var (k, v) in pairs)
            dict[k] = JsonSerializer.SerializeToElement(v);
        return dict;
    }

    // -----------------------------------------------------------------------
    // Channel creation
    // -----------------------------------------------------------------------

    [Fact]
    public async Task CreateMqttChannel_AcceptsValidConfig()
    {
        var config = Cfg(
            ("broker_url", "mqtt://broker.example.com:1883"),
            ("topic_prefix", "drivechill"),
            ("qos", 1),
            ("retain", false),
            ("publish_telemetry", true)
        );

        var ch = await _svc.CreateAsync("mqtt_valid", "mqtt", "Home MQTT", true, config, CancellationToken.None);

        Assert.Equal("mqtt", ch.Type);
        Assert.Equal("Home MQTT", ch.Name);
        Assert.True(ch.Enabled);
    }

    [Fact]
    public async Task CreateMqttChannel_AcceptsMqttsScheme()
    {
        var config = Cfg(
            ("broker_url", "mqtts://secure.broker.example.com:8883"),
            ("topic_prefix", "home"),
            ("publish_telemetry", false)
        );

        var ch = await _svc.CreateAsync("mqtt_tls", "mqtt", "Secure MQTT", true, config, CancellationToken.None);

        Assert.Equal("mqtt", ch.Type);
        Assert.Equal("Secure MQTT", ch.Name);
    }

    [Fact]
    public async Task CreateMqttChannel_EmptyConfig_Accepted()
    {
        // An mqtt channel with no config keys is valid at creation time.
        // Connection will fail at send time — that is expected and handled gracefully.
        var ch = await _svc.CreateAsync("mqtt_empty", "mqtt", "Empty MQTT", true,
            new Dictionary<string, JsonElement>(), CancellationToken.None);

        Assert.Equal("mqtt", ch.Type);
        Assert.NotNull(ch.Id);
    }

    // -----------------------------------------------------------------------
    // PublishTelemetryAsync skip logic
    // -----------------------------------------------------------------------

    [Fact]
    public async Task PublishTelemetry_NoMqttChannels_ReturnsZero()
    {
        // No channels at all — should return 0 without error.
        var readings = new List<TelemetryReading>
        {
            new() { SensorId = "cpu0", SensorName = "CPU Core #0", SensorType = "temperature", Value = 55.0, Unit = "C" },
        };

        var published = await _svc.PublishTelemetryAsync(readings, CancellationToken.None);

        Assert.Equal(0, published);
    }

    [Fact]
    public async Task PublishTelemetry_NonMqttChannel_Skipped()
    {
        // A discord channel must be skipped because PublishTelemetryAsync only processes mqtt type.
        await _svc.CreateAsync("discord_skip", "discord", "Discord Skip", true,
            Cfg(("webhook_url", "https://discord.com/api/webhooks/fake")),
            CancellationToken.None);

        var readings = new List<TelemetryReading>
        {
            new() { SensorId = "cpu0", SensorName = "CPU", SensorType = "temperature", Value = 60.0, Unit = "C" },
        };

        var published = await _svc.PublishTelemetryAsync(readings, CancellationToken.None);

        Assert.Equal(0, published);
    }

    [Fact]
    public async Task PublishTelemetry_MqttWithoutPublishFlag_Skipped()
    {
        // An mqtt channel without publish_telemetry=true must be skipped.
        await _svc.CreateAsync("mqtt_nopub", "mqtt", "No Publish", true,
            Cfg(
                ("broker_url", "mqtt://localhost:1883"),
                ("publish_telemetry", false)
            ),
            CancellationToken.None);

        var readings = new List<TelemetryReading>
        {
            new() { SensorId = "cpu0", SensorName = "CPU", SensorType = "temperature", Value = 70.0, Unit = "C" },
        };

        var published = await _svc.PublishTelemetryAsync(readings, CancellationToken.None);

        // Channel is skipped because publish_telemetry is false; returns 0.
        Assert.Equal(0, published);
    }

    // -----------------------------------------------------------------------
    // SendTestAsync with unreachable broker
    // -----------------------------------------------------------------------

    [Fact]
    public async Task SendAlert_InvalidBrokerUrl_ReturnsFalse()
    {
        // A broker URL pointing to an unresolvable host causes connection failure.
        // The service must catch the exception and return (false, errorMessage).
        await _svc.CreateAsync("mqtt_bad", "mqtt", "Bad Broker", true,
            Cfg(
                ("broker_url", "mqtt://no-such-host-drivechill-test.invalid:1883")
            ),
            CancellationToken.None);

        var (success, error) = await _svc.SendTestAsync("mqtt_bad", CancellationToken.None);

        Assert.False(success);
        Assert.NotNull(error);
    }

    // -----------------------------------------------------------------------
    // ListAsync includes mqtt type
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ListChannels_IncludesMqttType()
    {
        await _svc.CreateAsync("mqtt_list", "mqtt", "Listed MQTT", true,
            Cfg(("broker_url", "mqtt://broker.example.com:1883")),
            CancellationToken.None);

        var channels = await _svc.ListAsync(CancellationToken.None);

        Assert.Contains(channels, c => c.Id == "mqtt_list" && c.Type == "mqtt" && c.Name == "Listed MQTT");
    }

    // -----------------------------------------------------------------------
    // HA Discovery — config disabled
    // -----------------------------------------------------------------------

    [Fact]
    public async Task PublishTelemetry_HaDiscoveryDisabled_NoExtraPublish()
    {
        // When ha_discovery is false (default), PublishTelemetryAsync should still
        // work but not attempt any HA discovery publishes.
        // With an unreachable broker, the telemetry publish returns 0 — the key
        // assertion is that no exception is thrown due to HA discovery logic.
        await _svc.CreateAsync("mqtt_noha", "mqtt", "No HA", true,
            Cfg(
                ("broker_url", "mqtt://localhost:1883"),
                ("publish_telemetry", true),
                ("ha_discovery", false)
            ),
            CancellationToken.None);

        var readings = new List<TelemetryReading>
        {
            new() { SensorId = "cpu0", SensorName = "CPU Core #0", SensorType = "temperature", Value = 55.0, Unit = "C" },
        };

        // Should not throw — connection will fail gracefully
        var published = await _svc.PublishTelemetryAsync(readings, CancellationToken.None);
        Assert.Equal(0, published);
    }

    [Fact]
    public async Task PublishTelemetry_HaDiscoveryEnabled_NoExtraException()
    {
        // When ha_discovery is true but broker is unreachable, the method should
        // still handle gracefully (connection failure before discovery publish).
        await _svc.CreateAsync("mqtt_ha", "mqtt", "With HA", true,
            Cfg(
                ("broker_url", "mqtt://no-such-host-drivechill-test.invalid:1883"),
                ("publish_telemetry", true),
                ("ha_discovery", true),
                ("ha_discovery_prefix", "homeassistant")
            ),
            CancellationToken.None);

        var readings = new List<TelemetryReading>
        {
            new() { SensorId = "cpu0", SensorName = "CPU", SensorType = "temperature", Value = 60.0, Unit = "C" },
            new() { SensorId = "fan1", SensorName = "Fan 1", SensorType = "fan_speed", Value = 1200, Unit = "RPM" },
        };

        var published = await _svc.PublishTelemetryAsync(readings, CancellationToken.None);
        Assert.Equal(0, published);
    }
}
