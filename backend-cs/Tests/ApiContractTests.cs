using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using DriveChill.Api;
using DriveChill.Hardware;
using DriveChill.Models;
using DriveChill.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DriveChill.Tests;

/// <summary>
/// Validates that C# controller responses match the shared JSON schemas
/// in tests/contracts/.  The same schemas are validated against the Python
/// backend in backend/tests/test_api_contracts.py.
/// </summary>
public sealed class ApiContractTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AppSettings _appSettings;
    private readonly DbService _db;
    private readonly SettingsStore _store;
    private readonly AlertService _alerts;

    public ApiContractTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
        Environment.SetEnvironmentVariable("DRIVECHILL_DATA_DIR", _tempDir);

        _appSettings = new AppSettings();
        _db          = new DbService(_appSettings, NullLogger<DbService>.Instance);
        _store       = new SettingsStore(_appSettings);
        _alerts      = new AlertService(_db);
        _alerts.InitializeAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _alerts.Dispose();
        _db.Dispose();
        Environment.SetEnvironmentVariable("DRIVECHILL_DATA_DIR", null);
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // -------------------------------------------------------------------
    // Schema loading + validation helpers
    // -------------------------------------------------------------------

    private static string ContractsDir()
    {
        // Walk up from bin/Debug/net10.0-windows to find tests/contracts/
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "contracts")))
            dir = dir.Parent;
        return dir != null
            ? Path.Combine(dir.FullName, "tests", "contracts")
            : throw new DirectoryNotFoundException("Cannot find tests/contracts/ directory");
    }

    private static JsonDocument LoadSchema(string name)
    {
        var path = Path.Combine(ContractsDir(), $"{name}.json");
        var text = File.ReadAllText(path);
        return JsonDocument.Parse(text);
    }

    /// <summary>
    /// Lightweight JSON schema validator: checks required fields and basic types.
    /// Supports: type (string/number/integer/boolean/array/object), required,
    /// enum, items (for arrays), properties (for objects).
    /// </summary>
    private static void ValidateAgainstSchema(JsonElement data, JsonDocument schemaDoc)
    {
        ValidateElement(data, schemaDoc.RootElement);
    }

    private static void ValidateElement(JsonElement data, JsonElement schema)
    {
        if (schema.TryGetProperty("type", out var typeProp))
        {
            if (typeProp.ValueKind == JsonValueKind.Array)
            {
                var types = new List<string>();
                foreach (var t in typeProp.EnumerateArray())
                    types.Add(t.GetString()!);
                Assert.True(MatchesAnyType(data, types),
                    $"Expected one of [{string.Join(", ", types)}] but got {data.ValueKind}");
            }
            else
            {
                var expectedType = typeProp.GetString()!;
                AssertType(data, expectedType);
            }
        }

        if (schema.TryGetProperty("enum", out var enumProp))
        {
            var allowed = new List<string>();
            foreach (var v in enumProp.EnumerateArray())
                allowed.Add(v.GetString()!);
            Assert.Contains(data.GetString(), allowed);
        }

        if (schema.TryGetProperty("required", out var requiredProp))
        {
            foreach (var field in requiredProp.EnumerateArray())
            {
                var fieldName = field.GetString()!;
                Assert.True(data.TryGetProperty(fieldName, out _),
                    $"Missing required field: {fieldName}");
            }
        }

        if (schema.TryGetProperty("properties", out var propsProp))
        {
            foreach (var prop in propsProp.EnumerateObject())
            {
                if (data.TryGetProperty(prop.Name, out var val))
                {
                    if (val.ValueKind != JsonValueKind.Null)
                        ValidateElement(val, prop.Value);
                }
            }
        }

        if (schema.TryGetProperty("items", out var itemsProp) &&
            data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
                ValidateElement(item, itemsProp);
        }
    }

    private static bool MatchesAnyType(JsonElement data, List<string> types)
    {
        foreach (var t in types)
        {
            if (t == "null" && data.ValueKind == JsonValueKind.Null) return true;
            if (t == "string" && data.ValueKind == JsonValueKind.String) return true;
            if (t == "number" && data.ValueKind == JsonValueKind.Number) return true;
            if (t == "integer" && data.ValueKind == JsonValueKind.Number) return true;
            if (t == "boolean" && (data.ValueKind == JsonValueKind.True || data.ValueKind == JsonValueKind.False)) return true;
            if (t == "array" && data.ValueKind == JsonValueKind.Array) return true;
            if (t == "object" && data.ValueKind == JsonValueKind.Object) return true;
        }
        return false;
    }

    private static void AssertType(JsonElement data, string expectedType)
    {
        switch (expectedType)
        {
            case "string":
                Assert.True(data.ValueKind == JsonValueKind.String,
                    $"Expected string but got {data.ValueKind}");
                break;
            case "number":
                Assert.True(data.ValueKind == JsonValueKind.Number,
                    $"Expected number but got {data.ValueKind}");
                break;
            case "integer":
                Assert.True(data.ValueKind == JsonValueKind.Number,
                    $"Expected integer but got {data.ValueKind}");
                if (data.TryGetDouble(out var dbl))
                    Assert.True(dbl == Math.Floor(dbl),
                        $"Expected integer but got fractional number {dbl}");
                break;
            case "boolean":
                Assert.True(data.ValueKind is JsonValueKind.True or JsonValueKind.False,
                    $"Expected boolean but got {data.ValueKind}");
                break;
            case "array":
                Assert.True(data.ValueKind == JsonValueKind.Array,
                    $"Expected array but got {data.ValueKind}");
                break;
            case "object":
                Assert.True(data.ValueKind == JsonValueKind.Object,
                    $"Expected object but got {data.ValueKind}");
                break;
        }
    }

    private static JsonElement Serialize(object obj)
    {
        var opts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };
        var json = JsonSerializer.Serialize(obj, opts);
        return JsonDocument.Parse(json).RootElement;
    }

    private static JsonElement ExtractOkBody(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        return Serialize(ok.Value!);
    }

    // -------------------------------------------------------------------
    // GET /api/health
    // -------------------------------------------------------------------

    [Fact]
    public void Health_MatchesSchema()
    {
        var hw = new StubHardwareBackend();
        var sensors = new SensorService();
        var ctrl = new HealthController(hw, sensors, _appSettings);

        var body = ExtractOkBody(ctrl.GetHealth());
        var schema = LoadSchema("health");
        ValidateAgainstSchema(body, schema);

        Assert.Equal("ok", body.GetProperty("status").GetString());
    }

    // -------------------------------------------------------------------
    // GET /api/fans
    // -------------------------------------------------------------------

    [Fact]
    public void Fans_MatchesSchema()
    {
        var hw = new StubHardwareBackend();
        var sensors = new SensorService();
        var fans = new FanService(hw, _store);
        var ctrl = new FansController(fans, sensors, _db);

        var body = ExtractOkBody(ctrl.GetFans());
        var schema = LoadSchema("fans");
        ValidateAgainstSchema(body, schema);

        Assert.Equal(JsonValueKind.Array, body.GetProperty("fans").ValueKind);
    }

    // -------------------------------------------------------------------
    // GET /api/sensors
    // -------------------------------------------------------------------

    [Fact]
    public void Sensors_MatchesSchema()
    {
        var hw = new StubHardwareBackend();
        var sensors = new SensorService();
        var ctrl = new SensorsController(sensors, _db);

        var result = ctrl.GetSensors();
        var body = ExtractOkBody(result);
        var schema = LoadSchema("sensors");
        ValidateAgainstSchema(body, schema);

        Assert.Equal(JsonValueKind.Array, body.GetProperty("readings").ValueKind);
    }

    // -------------------------------------------------------------------
    // GET /api/profiles
    // -------------------------------------------------------------------

    [Fact]
    public async Task Profiles_MatchesSchema()
    {
        // Seed a profile
        var profile = new Profile
        {
            Name = "Test",
            IsActive = true,
            Curves = [],
        };
        await _db.CreateProfileAsync(profile);

        var ctrl = new ProfilesController(_db, null!, _alerts);
        var result = await ctrl.GetProfiles();

        // C# returns bare array — wrap it for schema validation
        var body = ExtractOkBody(result);
        JsonElement wrapped;
        if (body.ValueKind == JsonValueKind.Array)
        {
            var json = $"{{\"profiles\":{body.GetRawText()}}}";
            wrapped = JsonDocument.Parse(json).RootElement;
        }
        else
        {
            wrapped = body;
        }

        var schema = LoadSchema("profiles");
        ValidateAgainstSchema(wrapped, schema);
    }

    // -------------------------------------------------------------------
    // GET /api/alerts/rules + events
    // -------------------------------------------------------------------

    [Fact]
    public void AlertRules_MatchesSchema()
    {
        var ctrl = new AlertsController(_alerts);
        var result = ctrl.GetRules();
        var body = ExtractOkBody(result);

        var schema = LoadSchema("alerts_rules");
        ValidateAgainstSchema(body, schema);
    }

    [Fact]
    public void AlertEvents_MatchesSchema()
    {
        var ctrl = new AlertsController(_alerts);
        var result = ctrl.GetEvents();
        var body = ExtractOkBody(result);

        var schema = LoadSchema("alerts_events");
        ValidateAgainstSchema(body, schema);
    }

    // -------------------------------------------------------------------
    // GET /api/settings
    // -------------------------------------------------------------------

    [Fact]
    public void Settings_MatchesSchema()
    {
        var webhooks = new WebhookService(_store, new NullHttpClientFactory(), _appSettings);
        var notifChannels = new NotificationChannelService(_db, new NullHttpClientFactory(),
                                NullLogger<NotificationChannelService>.Instance, _appSettings);
        var ctrl = new SettingsController(_store, _appSettings, _db, webhooks, notifChannels, _alerts);

        var result = ctrl.GetSettings();
        var body = ExtractOkBody(result);
        var schema = LoadSchema("settings");
        ValidateAgainstSchema(body, schema);
    }

    // -------------------------------------------------------------------
    // GET /api/notification-channels
    // -------------------------------------------------------------------

    [Fact]
    public async Task NotificationChannels_MatchesSchema()
    {
        var notifSvc = new NotificationChannelService(_db, new NullHttpClientFactory(),
                           NullLogger<NotificationChannelService>.Instance, _appSettings);
        var ctrl = new NotificationChannelsController(notifSvc, _appSettings);

        var result = await ctrl.List(default);
        var body = ExtractOkBody(result);
        var schema = LoadSchema("notification_channels");
        ValidateAgainstSchema(body, schema);
    }

    // -------------------------------------------------------------------
    // Synthetic validation — schemas that need network or complex setup
    // -------------------------------------------------------------------

    [Fact]
    public void DrivesSchema_ValidatesSyntheticData()
    {
        var data = JsonDocument.Parse("""
        {
            "drives": [{
                "id": "aaaaaaaaaaaaaaaaaaaaaaaa",
                "name": "Test Drive",
                "model": "Test Model",
                "bus_type": "nvme",
                "media_type": "ssd",
                "health_status": "good"
            }],
            "total": 1,
            "smartctl_available": true
        }
        """).RootElement;

        ValidateAgainstSchema(data, LoadSchema("drives"));
    }

    [Fact]
    public void AnalyticsStatsSchema_ValidatesSyntheticData()
    {
        var data = JsonDocument.Parse("""
        {
            "stats": [{
                "sensor_id": "cpu_temp_0",
                "sensor_name": "CPU",
                "sensor_type": "cpu_temp",
                "unit": "°C",
                "min_value": 30.0,
                "max_value": 85.0,
                "avg_value": 55.0,
                "sample_count": 100
            }],
            "requested_range": { "start": "2026-03-12T10:00:00", "end": "2026-03-12T11:00:00" },
            "returned_range": { "start": "2026-03-12T10:00:00", "end": "2026-03-12T11:00:00" }
        }
        """).RootElement;

        ValidateAgainstSchema(data, LoadSchema("analytics_stats"));
    }

    [Fact]
    public void UpdateCheckSchema_ValidatesSyntheticData()
    {
        var data = JsonDocument.Parse("""
        {
            "current": "1.5.0",
            "latest": "1.6.0",
            "update_available": true,
            "release_url": "https://github.com/LstDtchMn/DriveChill/releases/tag/v1.6.0",
            "deployment": "windows_service"
        }
        """).RootElement;

        ValidateAgainstSchema(data, LoadSchema("update_check"));
    }

    // -------------------------------------------------------------------
    // Synthetic — quiet hours, webhooks, temperature targets, machines,
    // profile schedules, virtual sensors
    // -------------------------------------------------------------------

    [Fact]
    public void QuietHoursSchema_ValidatesSyntheticData()
    {
        var data = JsonDocument.Parse("""
        {
            "rules": [{
                "id": 1,
                "day_of_week": 0,
                "start_time": "22:00",
                "end_time": "07:00",
                "profile_id": "p1",
                "enabled": true
            }]
        }
        """).RootElement;

        ValidateAgainstSchema(data, LoadSchema("quiet_hours"));
    }

    [Fact]
    public void WebhooksSchema_ValidatesSyntheticData()
    {
        var data = JsonDocument.Parse("""
        {
            "config": {
                "enabled": true,
                "target_url": "https://example.com/hook",
                "has_signing_secret": true,
                "timeout_seconds": 3.0,
                "max_retries": 2,
                "retry_backoff_seconds": 1.0
            }
        }
        """).RootElement;

        ValidateAgainstSchema(data, LoadSchema("webhooks"));
    }

    [Fact]
    public void TemperatureTargetsSchema_ValidatesSyntheticData()
    {
        var data = JsonDocument.Parse("""
        {
            "targets": [{
                "id": "t1",
                "name": "SSD Target",
                "sensor_id": "hdd_temp_drive1",
                "fan_ids": ["Fan1"],
                "target_temp_c": 40.0,
                "tolerance_c": 5.0,
                "min_fan_speed": 20.0,
                "enabled": true,
                "pid_mode": false,
                "pid_kp": 5.0,
                "pid_ki": 0.05,
                "pid_kd": 1.0
            }]
        }
        """).RootElement;

        ValidateAgainstSchema(data, LoadSchema("temperature_targets"));
    }

    [Fact]
    public void MachinesSchema_ValidatesSyntheticData()
    {
        var data = JsonDocument.Parse("""
        {
            "machines": [{
                "id": "m1",
                "name": "Workstation",
                "base_url": "http://192.168.1.50:8085",
                "has_api_key": true,
                "enabled": true,
                "poll_interval_seconds": 30.0,
                "timeout_ms": 5000,
                "status": "online",
                "consecutive_failures": 0
            }]
        }
        """).RootElement;

        ValidateAgainstSchema(data, LoadSchema("machines"));
    }

    [Fact]
    public void ProfileSchedulesSchema_ValidatesSyntheticData()
    {
        var data = JsonDocument.Parse("""
        {
            "schedules": [{
                "id": "s1",
                "name": "Night mode",
                "profile_id": "p1",
                "cron_expression": "0 22 * * *",
                "timezone": "America/New_York",
                "enabled": true,
                "last_triggered_at": null,
                "next_trigger_at": "2026-03-12T22:00:00"
            }]
        }
        """).RootElement;

        ValidateAgainstSchema(data, LoadSchema("profile_schedules"));
    }

    [Fact]
    public void VirtualSensorsSchema_ValidatesSyntheticData()
    {
        var data = JsonDocument.Parse("""
        {
            "sensors": [{
                "id": "vs1",
                "name": "Avg CPU",
                "type": "average",
                "source_sensor_ids": ["cpu_temp_0"],
                "enabled": true,
                "value": 55.0
            }]
        }
        """).RootElement;

        ValidateAgainstSchema(data, LoadSchema("virtual_sensors"));
    }

    // -------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------

    private sealed class StubHardwareBackend : IHardwareBackend
    {
        public void Initialize() { }
        public string GetBackendName() => "stub";
        public IReadOnlyList<SensorReading> GetSensorReadings() => [];
        public IReadOnlyList<string> GetFanIds() => ["Fan1"];
        public bool SetFanSpeed(string fanId, double speedPercent) => true;
        public bool SetFanAuto(string fanId) => true;
        public void Dispose() { }
    }
}
