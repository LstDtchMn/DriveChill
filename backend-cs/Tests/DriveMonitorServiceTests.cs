using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DriveChill.Models;
using DriveChill.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DriveChill.Tests;

/// <summary>
/// Tests for <see cref="DriveMonitorService"/> using <see cref="MockDriveProvider"/>.
/// No smartctl required — all drive data is injected via the mock.
/// </summary>
public sealed class DriveMonitorServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AppSettings _settings;
    private readonly MockDriveProvider _provider;
    private readonly SensorService _sensor;
    private readonly DriveMonitorService _svc;

    private static DriveSettings DefaultDriveSettings() => new()
    {
        Enabled = true,
        SmartctlPath = "smartctl",
        FastPollSeconds = 60,
        HealthPollSeconds = 300,
        RescanPollSeconds = 3600,
    };

    private static DriveRawData MakeDrive(string id, string name, double? tempC = 35.0,
        string devicePath = "/dev/sda", string busType = "sata")
    {
        return new DriveRawData
        {
            Id              = id,
            Name            = name,
            Model           = name,
            Serial          = "SN" + id,
            DevicePath      = devicePath,
            BusType         = busType,
            MediaType       = "hdd",
            CapacityBytes   = 1_000_000_000L,
            FirmwareVersion = "1.0",
            TemperatureC    = tempC,
            SmartOverallHealth  = "PASSED",
            PredictedFailure    = false,
            Capabilities    = new DriveCapabilitySet
            {
                SmartRead          = true,
                SmartSelfTestShort = true,
                SmartSelfTestAbort = true,
                TemperatureSource  = "smartctl",
                HealthSource       = "smartctl",
            },
            RawAttributes = [],
        };
    }

    private async Task InitDbAsync()
    {
        var db = new DbService(_settings, NullLogger<DbService>.Instance);
        await db.PruneAsync(retentionDays: 36500); // triggers schema init
    }

    public DriveMonitorServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"dms_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        Environment.SetEnvironmentVariable("DRIVECHILL_DATA_DIR", _tempDir);
        _settings = new AppSettings();
        _provider = new MockDriveProvider();
        _sensor   = new SensorService();
        _svc = new DriveMonitorService(
            _provider, _settings,
            NullLogger<DriveMonitorService>.Instance,
            _sensor);
    }

    public void Dispose()
    {
        _svc.Dispose();
        Environment.SetEnvironmentVariable("DRIVECHILL_DATA_DIR", null);
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // ── Provider availability ─────────────────────────────────────────────────

    [Fact]
    public async Task SmartctlAvailable_TrueWhenProviderAvailable()
    {
        _provider.Available = true;
        _provider.Drives = [MakeDrive("aaa", "Drive A")];
        await InitDbAsync();
        await _svc.StartAsync(DefaultDriveSettings(), CancellationToken.None);
        await _svc.StopAsync();
        Assert.True(_svc.SmartctlAvailable);
    }

    [Fact]
    public async Task SmartctlAvailable_FalseWhenProviderUnavailable()
    {
        _provider.Available = false;
        await InitDbAsync();
        await _svc.StartAsync(DefaultDriveSettings(), CancellationToken.None);
        await _svc.StopAsync();
        Assert.False(_svc.SmartctlAvailable);
    }

    // ── Drive discovery ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetAllDrives_EmptyWhenProviderReturnsNone()
    {
        _provider.Available = true;
        _provider.Drives = [];
        await InitDbAsync();
        await _svc.StartAsync(DefaultDriveSettings(), CancellationToken.None);
        await _svc.StopAsync();
        Assert.Empty(_svc.GetAllDrives());
    }

    [Fact]
    public async Task GetAllDrives_ReturnsAllDiscoveredDrives()
    {
        _provider.Available = true;
        _provider.Drives = [
            MakeDrive("aaa", "Drive A", devicePath: "/dev/sda"),
            MakeDrive("bbb", "Drive B", devicePath: "/dev/sdb"),
        ];
        await InitDbAsync();
        await _svc.StartAsync(DefaultDriveSettings(), CancellationToken.None);
        await _svc.StopAsync();
        var drives = _svc.GetAllDrives();
        Assert.Equal(2, drives.Count);
        Assert.Contains(drives, d => d.Id == "aaa");
        Assert.Contains(drives, d => d.Id == "bbb");
    }

    [Fact]
    public async Task GetDrive_ByIdReturnsCorrectDrive()
    {
        _provider.Available = true;
        _provider.Drives = [MakeDrive("abc123", "My SSD", devicePath: "/dev/sda")];
        await InitDbAsync();
        await _svc.StartAsync(DefaultDriveSettings(), CancellationToken.None);
        await _svc.StopAsync();
        var d = _svc.GetDrive("abc123");
        Assert.NotNull(d);
        Assert.Equal("My SSD", d!.Name);
    }

    [Fact]
    public async Task GetDrive_ReturnsNullForUnknownId()
    {
        _provider.Available = true;
        _provider.Drives = [MakeDrive("abc123", "My SSD")];
        await InitDbAsync();
        await _svc.StartAsync(DefaultDriveSettings(), CancellationToken.None);
        await _svc.StopAsync();
        Assert.Null(_svc.GetDrive("nonexistent"));
    }

    // ── Temperature sensor injection ──────────────────────────────────────────

    [Fact]
    public async Task DriveWithTemperature_PublishesSensorReading()
    {
        _provider.Available = true;
        _provider.Drives = [MakeDrive("aaa", "Hot Drive", tempC: 42.0, devicePath: "/dev/sda")];
        await InitDbAsync();
        await _svc.StartAsync(DefaultDriveSettings(), CancellationToken.None);
        await _svc.StopAsync();
        // UpdateDriveReadings stores drive readings; they merge into Latest on the next Update call.
        _sensor.Update(new SensorSnapshot());
        var readings = _sensor.Latest.Readings;
        Assert.Contains(readings, r => r.Id == "hdd_temp_aaa");
        Assert.Equal(42.0, readings.First(r => r.Id == "hdd_temp_aaa").Value);
    }

    [Fact]
    public async Task DriveWithNoTemperature_DoesNotPublishSensor()
    {
        _provider.Available = true;
        _provider.Drives = [MakeDrive("aaa", "No Temp", tempC: null, devicePath: "/dev/sda")];
        await InitDbAsync();
        await _svc.StartAsync(DefaultDriveSettings(), CancellationToken.None);
        await _svc.StopAsync();
        _sensor.Update(new SensorSnapshot());
        Assert.DoesNotContain(_sensor.Latest.Readings, r => r.Id == "hdd_temp_aaa");
    }

    // ── Self-test delegation ──────────────────────────────────────────────────

    [Fact]
    public async Task StartSelfTest_ReturnsProviderToken()
    {
        _provider.Available = true;
        _provider.Drives = [MakeDrive("aaa", "Drive A", devicePath: "/dev/sda")];
        _provider.SelfTestToken = "mock_token_42";
        await InitDbAsync();
        await _svc.StartAsync(DefaultDriveSettings(), CancellationToken.None);
        await _svc.StopAsync();
        var token = await _svc.StartSelfTestAsync("aaa", SelfTestType.Short, DefaultDriveSettings());
        Assert.Equal("mock_token_42", token);
    }

    [Fact]
    public async Task StartSelfTest_ReturnsNullWhenProviderFails()
    {
        _provider.Available = true;
        _provider.Drives = [MakeDrive("aaa", "Drive A", devicePath: "/dev/sda")];
        _provider.SelfTestToken = null;
        await InitDbAsync();
        await _svc.StartAsync(DefaultDriveSettings(), CancellationToken.None);
        await _svc.StopAsync();
        var token = await _svc.StartSelfTestAsync("aaa", SelfTestType.Short, DefaultDriveSettings());
        Assert.Null(token);
    }

    [Fact]
    public async Task StartSelfTest_ReturnsNullForUnknownDrive()
    {
        _provider.Available = true;
        _provider.Drives = [];
        await InitDbAsync();
        await _svc.StartAsync(DefaultDriveSettings(), CancellationToken.None);
        await _svc.StopAsync();
        var token = await _svc.StartSelfTestAsync("noexist", SelfTestType.Short, DefaultDriveSettings());
        Assert.Null(token);
    }

    [Fact]
    public async Task AbortSelfTest_DelegatesToProvider()
    {
        _provider.Available = true;
        _provider.Drives = [MakeDrive("aaa", "Drive A", devicePath: "/dev/sda")];
        _provider.AbortResult = true;
        await InitDbAsync();
        await _svc.StartAsync(DefaultDriveSettings(), CancellationToken.None);
        await _svc.StopAsync();
        var ok = await _svc.AbortSelfTestAsync("aaa", DefaultDriveSettings());
        Assert.True(ok);
    }

    [Fact]
    public async Task AbortSelfTest_ReturnsFalseForUnknownDrive()
    {
        _provider.Available = true;
        _provider.Drives = [];
        await InitDbAsync();
        await _svc.StartAsync(DefaultDriveSettings(), CancellationToken.None);
        await _svc.StopAsync();
        var ok = await _svc.AbortSelfTestAsync("noexist", DefaultDriveSettings());
        Assert.False(ok);
    }

    // ── ToSummary / ToDetail ──────────────────────────────────────────────────

    [Fact]
    public void ToSummary_MasksSerialAndDevicePath()
    {
        var drive = new DriveRawData
        {
            Id = "aaa", Name = "Test Drive", Model = "Test Drive",
            Serial = "ABCDEF1234", DevicePath = "/dev/sda",
            BusType = "sata", MediaType = "hdd", CapacityBytes = 0,
            FirmwareVersion = "1.0", PredictedFailure = false,
            Capabilities = new DriveCapabilitySet { TemperatureSource = "none", HealthSource = "none" },
            RawAttributes = [],
        };
        var summary = _svc.ToSummary(drive);
        Assert.StartsWith("****", summary.SerialMasked);
        Assert.Equal("sda", summary.DevicePathMasked);
        Assert.DoesNotContain("/", summary.DevicePathMasked);
    }
}
