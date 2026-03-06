using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using DriveChill.Api;
using DriveChill.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DriveChill.Tests;

public sealed class SettingsControllerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AppSettings _appSettings;
    private readonly SettingsStore _store;
    private readonly DbService _db;
    private readonly WebhookService _webhooks;
    private readonly NotificationChannelService _notifChannels;
    private readonly SettingsController _ctrl;

    public SettingsControllerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
        Environment.SetEnvironmentVariable("DRIVECHILL_DATA_DIR", _tempDir);

        _appSettings   = new AppSettings();
        _store         = new SettingsStore(_appSettings);
        _db            = new DbService(_appSettings, NullLogger<DbService>.Instance);
        _webhooks      = new WebhookService(_store, new NullHttpClientFactory(), _appSettings);
        _notifChannels = new NotificationChannelService(_db, new NullHttpClientFactory(),
                             NullLogger<NotificationChannelService>.Instance);
        _ctrl          = new SettingsController(_store, _appSettings, _db, _webhooks, _notifChannels);
    }

    public void Dispose()
    {
        _db.Dispose();
        Environment.SetEnvironmentVariable("DRIVECHILL_DATA_DIR", null);
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // -----------------------------------------------------------------------
    // GET /api/settings
    // -----------------------------------------------------------------------

    [Fact]
    public void GetSettings_ReturnsExpectedDefaults()
    {
        var result = _ctrl.GetSettings();
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("C", doc.RootElement.GetProperty("temp_unit").GetString());
        Assert.Equal("lhm", doc.RootElement.GetProperty("hardware_backend").GetString());
    }

    // -----------------------------------------------------------------------
    // PUT /api/settings
    // -----------------------------------------------------------------------

    [Fact]
    public void UpdateSettings_ChangesTempUnit()
    {
        _ctrl.UpdateSettings(new SettingsUpdateRequest { TempUnit = "F" });
        Assert.Equal("F", _store.TempUnit);
    }

    [Fact]
    public void UpdateSettings_ClampsPollInterval()
    {
        _ctrl.UpdateSettings(new SettingsUpdateRequest { SensorPollInterval = 0.1 });
        // Clamped to min 0.5 → 500 ms
        Assert.Equal(500, _store.PollIntervalMs);
    }

    [Fact]
    public void UpdateSettings_ClampsRetentionHours()
    {
        _ctrl.UpdateSettings(new SettingsUpdateRequest { HistoryRetentionHours = 99999 });
        // Clamped to 8760 → 365 days
        Assert.Equal(365, _store.RetentionDays);
    }

    [Fact]
    public void UpdateSettings_ClampsDeadband()
    {
        _ctrl.UpdateSettings(new SettingsUpdateRequest { Deadband = 50.0 });
        Assert.Equal(20.0, _store.Deadband);
    }

    [Fact]
    public void UpdateSettings_ClampsFanRampRate()
    {
        _ctrl.UpdateSettings(new SettingsUpdateRequest { FanRampRatePctPerSec = 200.0 });
        Assert.Equal(100.0, _store.FanRampRatePctPerSec);
    }

    [Fact]
    public void UpdateSettings_RejectInvalidTempUnit()
    {
        _ctrl.UpdateSettings(new SettingsUpdateRequest { TempUnit = "K" });
        // Should stay at default "C" since "K" is not accepted
        Assert.Equal("C", _store.TempUnit);
    }

    // -----------------------------------------------------------------------
    // GET /api/settings/info
    // -----------------------------------------------------------------------

    [Fact]
    public void GetInfo_ReturnsPlatformAndRuntime()
    {
        var result = _ctrl.GetInfo();
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("windows", doc.RootElement.GetProperty("platform").GetString());
        Assert.Equal("dotnet", doc.RootElement.GetProperty("runtime").GetString());
    }

    // -----------------------------------------------------------------------
    // Export / Import
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ExportConfig_ReturnsExportVersion1()
    {
        var result = await _ctrl.ExportConfig();
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(1, doc.RootElement.GetProperty("export_version").GetInt32());
        Assert.True(doc.RootElement.TryGetProperty("profiles", out _));
        Assert.True(doc.RootElement.TryGetProperty("settings", out _));
    }

    [Fact]
    public async Task ImportConfig_RejectsWrongVersion()
    {
        var body = JsonSerializer.Deserialize<JsonElement>("""{"export_version": 99}""");
        var result = await _ctrl.ImportConfig(body);
        Assert.IsType<UnprocessableEntityObjectResult>(result);
    }

    [Fact]
    public async Task ImportConfig_RejectsMissingVersion()
    {
        var body = JsonSerializer.Deserialize<JsonElement>("""{}""");
        var result = await _ctrl.ImportConfig(body);
        Assert.IsType<UnprocessableEntityObjectResult>(result);
    }

    [Fact]
    public async Task ImportConfig_ImportsSettings()
    {
        var body = JsonSerializer.Deserialize<JsonElement>("""
        {
            "export_version": 1,
            "settings": {
                "temp_unit": "F",
                "sensor_poll_interval": 2.0,
                "fan_ramp_rate_pct_per_sec": 10.0
            }
        }
        """);
        var result = await _ctrl.ImportConfig(body);
        var ok = Assert.IsType<OkObjectResult>(result);

        Assert.Equal("F", _store.TempUnit);
        Assert.Equal(2000, _store.PollIntervalMs);
        Assert.Equal(10.0, _store.FanRampRatePctPerSec);
    }

    [Fact]
    public async Task ImportConfig_ImportsProfiles()
    {
        var body = JsonSerializer.Deserialize<JsonElement>("""
        {
            "export_version": 1,
            "profiles": [
                {
                    "id": "test-profile-1",
                    "name": "Imported Silent",
                    "preset": "silent",
                    "is_active": false,
                    "curves": []
                }
            ]
        }
        """);
        await _ctrl.ImportConfig(body);
        var profiles = _store.LoadProfiles();
        Assert.Contains(profiles, p => p.Name == "Imported Silent");
    }

    [Fact]
    public async Task ImportConfig_ImportsSensorLabels()
    {
        var body = JsonSerializer.Deserialize<JsonElement>("""
        {
            "export_version": 1,
            "sensor_labels": {
                "cpu_temp_0": "CPU Package",
                "gpu_temp_0": "GPU Core"
            }
        }
        """);
        await _ctrl.ImportConfig(body);

        var labels = await _db.GetAllLabelsAsync();
        Assert.True(labels.ContainsKey("cpu_temp_0"));
        Assert.Equal("CPU Package", labels["cpu_temp_0"]);
    }

    [Fact]
    public async Task ExportConfig_IncludesNotificationChannels()
    {
        // Create a notification channel in the DB before exporting
        var notifSvc = new NotificationChannelService(_db, new NullHttpClientFactory(),
                            NullLogger<NotificationChannelService>.Instance);
        await notifSvc.CreateAsync("nc_export_test", "slack", "Export Slack", true,
            new System.Collections.Generic.Dictionary<string, System.Text.Json.JsonElement>(),
            System.Threading.CancellationToken.None);

        var result = await _ctrl.ExportConfig();
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
        using var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("notification_channels", out var channels));
        Assert.Equal(1, channels.GetArrayLength());
        Assert.Equal("nc_export_test", channels[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task ImportConfig_RestoresNotificationChannels()
    {
        var body = JsonSerializer.Deserialize<JsonElement>("""
        {
            "export_version": 1,
            "notification_channels": [
                {
                    "id": "nc_import_test",
                    "type": "discord",
                    "name": "Imported Discord",
                    "enabled": true,
                    "config_json": "{\"webhook_url\":\"https://discord.com/api/webhooks/x/y\"}",
                    "created_at": "2025-01-01T00:00:00Z",
                    "updated_at": "2025-01-01T00:00:00Z"
                }
            ]
        }
        """);

        var result = await _ctrl.ImportConfig(body);
        Assert.IsType<OkObjectResult>(result);

        var notifSvc = new NotificationChannelService(_db, new NullHttpClientFactory(),
                            NullLogger<NotificationChannelService>.Instance);
        var channels = await notifSvc.ListAsync();
        Assert.Contains(channels, c => c.Id == "nc_import_test" && c.Type == "discord");
    }

    [Fact]
    public async Task ImportConfig_NeverOverwritesWebhookSigningSecret()
    {
        // Set an existing signing secret
        _webhooks.UpdateConfig(new Models.WebhookConfig
        {
            SigningSecret = "my-secret-key",
            TargetUrl = "",
        });

        var body = JsonSerializer.Deserialize<JsonElement>("""
        {
            "export_version": 1,
            "webhook_config": {
                "enabled": true,
                "target_url": "https://hooks.example.com/webhook",
                "signing_secret": "SHOULD-BE-IGNORED"
            }
        }
        """);
        await _ctrl.ImportConfig(body);

        var cfg = _webhooks.GetConfigRaw();
        Assert.Equal("my-secret-key", cfg.SigningSecret);
    }
}
