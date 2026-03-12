using System.Text.Json;
using DriveChill.Hardware;
using DriveChill.Models;
using DriveChill.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DriveChill.Tests;

/// <summary>
/// Tests for <see cref="MqttCommandHandler.DispatchCommandAsync"/> -- the
/// command parsing/dispatch logic. No real MQTT broker needed.
/// </summary>
public sealed class MqttCommandTests : IDisposable
{
    private readonly string _tempDir;
    private readonly MqttCommandHandler _handler;
    private readonly MockBackend _hw;
    private readonly FanService _fans;
    private readonly DbService _db;

    public MqttCommandTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
        Environment.SetEnvironmentVariable("DRIVECHILL_DATA_DIR", _tempDir);

        var settings = new AppSettings();
        _db = new DbService(settings, NullLogger<DbService>.Instance);
        _hw = new MockBackend();
        _hw.Initialize();
        var store = new SettingsStore(settings);
        _fans = new FanService(_hw, store);

        var channelSvc = new NotificationChannelService(
            _db, new NullHttpClientFactory(),
            NullLogger<NotificationChannelService>.Instance, settings);

        _handler = new MqttCommandHandler(
            channelSvc, _fans, _db, _hw,
            NullLogger<MqttCommandHandler>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        Environment.SetEnvironmentVariable("DRIVECHILL_DATA_DIR", null);
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // -----------------------------------------------------------------------
    // Fan speed commands
    // -----------------------------------------------------------------------

    [Fact]
    public async Task FanSpeed_ValidPercent_SetsSpeed()
    {
        var payload = JsonSerializer.Serialize(new { percent = 75.0 });
        await _handler.DispatchCommandAsync("drivechill/commands/fans/fan_1/speed", payload, "drivechill");
    }

    [Fact]
    public async Task FanSpeed_ZeroPercent_Accepted()
    {
        var payload = JsonSerializer.Serialize(new { percent = 0.0 });
        await _handler.DispatchCommandAsync("drivechill/commands/fans/fan_1/speed", payload, "drivechill");
    }

    [Fact]
    public async Task FanSpeed_HundredPercent_Accepted()
    {
        var payload = JsonSerializer.Serialize(new { percent = 100.0 });
        await _handler.DispatchCommandAsync("drivechill/commands/fans/fan_1/speed", payload, "drivechill");
    }

    [Fact]
    public async Task FanSpeed_NegativePercent_Rejected()
    {
        var payload = JsonSerializer.Serialize(new { percent = -10.0 });
        await _handler.DispatchCommandAsync("drivechill/commands/fans/fan_1/speed", payload, "drivechill");
    }

    [Fact]
    public async Task FanSpeed_Over100_Rejected()
    {
        var payload = JsonSerializer.Serialize(new { percent = 150.0 });
        await _handler.DispatchCommandAsync("drivechill/commands/fans/fan_1/speed", payload, "drivechill");
    }

    [Fact]
    public async Task FanSpeed_MissingPercent_Rejected()
    {
        var payload = JsonSerializer.Serialize(new { speed = 50.0 });
        await _handler.DispatchCommandAsync("drivechill/commands/fans/fan_1/speed", payload, "drivechill");
    }

    [Fact]
    public async Task FanSpeed_StringPercent_Rejected()
    {
        var payload = JsonSerializer.Serialize(new { percent = "fifty" });
        await _handler.DispatchCommandAsync("drivechill/commands/fans/fan_1/speed", payload, "drivechill");
    }

    // -----------------------------------------------------------------------
    // Profile activate commands
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ProfileActivate_ValidId_ActivatesProfile()
    {
        // First create a profile in the DB
        var profile = new Profile
        {
            Id = "test_prof",
            Name = "Test Profile",
            IsActive = false,
            Curves = [],
        };
        await _db.CreateProfileAsync(profile);

        var payload = JsonSerializer.Serialize(new { profile_id = "test_prof" });
        await _handler.DispatchCommandAsync("drivechill/commands/profiles/activate", payload, "drivechill");

        // Verify profile was activated
        var activated = await _db.GetProfileAsync("test_prof");
        Assert.NotNull(activated);
        Assert.True(activated!.IsActive);
    }

    [Fact]
    public async Task ProfileActivate_NotFound_NoException()
    {
        var payload = JsonSerializer.Serialize(new { profile_id = "nonexistent" });
        await _handler.DispatchCommandAsync("drivechill/commands/profiles/activate", payload, "drivechill");
    }

    [Fact]
    public async Task ProfileActivate_MissingField_NoException()
    {
        var payload = JsonSerializer.Serialize(new { name = "silent" });
        await _handler.DispatchCommandAsync("drivechill/commands/profiles/activate", payload, "drivechill");
    }

    [Fact]
    public async Task ProfileActivate_NumericId_Rejected()
    {
        var payload = JsonSerializer.Serialize(new { profile_id = 123 });
        await _handler.DispatchCommandAsync("drivechill/commands/profiles/activate", payload, "drivechill");
    }

    // -----------------------------------------------------------------------
    // Fan release commands
    // -----------------------------------------------------------------------

    [Fact]
    public async Task FanRelease_NoPayload_ReleasesControl()
    {
        await _handler.DispatchCommandAsync("drivechill/commands/fans/release", null, "drivechill");
        Assert.True(_fans.IsReleased);
    }

    [Fact]
    public async Task FanRelease_WithPayload_ReleasesControl()
    {
        await _handler.DispatchCommandAsync("drivechill/commands/fans/release", "{}", "drivechill");
        Assert.True(_fans.IsReleased);
    }

    // -----------------------------------------------------------------------
    // Malformed / edge cases
    // -----------------------------------------------------------------------

    [Fact]
    public async Task MalformedJson_NoException()
    {
        await _handler.DispatchCommandAsync("drivechill/commands/fans/fan_1/speed", "not-json", "drivechill");
    }

    [Fact]
    public async Task EmptyPayload_NonRelease_NoException()
    {
        await _handler.DispatchCommandAsync("drivechill/commands/fans/fan_1/speed", null, "drivechill");
    }

    [Fact]
    public async Task UnknownCommandTopic_NoException()
    {
        var payload = JsonSerializer.Serialize(new { key = "value" });
        await _handler.DispatchCommandAsync("drivechill/commands/unknown/action", payload, "drivechill");
    }

    [Fact]
    public async Task WrongPrefix_Ignored()
    {
        var payload = JsonSerializer.Serialize(new { percent = 50.0 });
        await _handler.DispatchCommandAsync("other/commands/fans/fan_1/speed", payload, "drivechill");
    }

    [Fact]
    public async Task CustomPrefix_Works()
    {
        var payload = JsonSerializer.Serialize(new { percent = 60.0 });
        await _handler.DispatchCommandAsync("mypc/commands/fans/fan_1/speed", payload, "mypc");
    }
}
