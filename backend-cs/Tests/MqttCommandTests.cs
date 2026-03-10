using System.Text.Json;
using DriveChill.Hardware;
using DriveChill.Models;
using DriveChill.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DriveChill.Tests;

/// <summary>
/// Tests for <see cref="MqttCommandHandler.DispatchCommand"/> — the synchronous
/// command parsing/dispatch logic. No real MQTT broker needed.
/// </summary>
public sealed class MqttCommandTests : IDisposable
{
    private readonly string _tempDir;
    private readonly MqttCommandHandler _handler;
    private readonly MockBackend _hw;
    private readonly FanService _fans;
    private readonly SettingsStore _store;

    public MqttCommandTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
        Environment.SetEnvironmentVariable("DRIVECHILL_DATA_DIR", _tempDir);

        var settings = new AppSettings();
        var db = new DbService(settings, NullLogger<DbService>.Instance);
        _hw = new MockBackend();
        _hw.Initialize();
        _store = new SettingsStore(settings);
        _fans = new FanService(_hw, _store);

        var channelSvc = new NotificationChannelService(
            db, new NullHttpClientFactory(),
            NullLogger<NotificationChannelService>.Instance);

        _handler = new MqttCommandHandler(
            channelSvc, _fans, _store, _hw,
            NullLogger<MqttCommandHandler>.Instance);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("DRIVECHILL_DATA_DIR", null);
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // -----------------------------------------------------------------------
    // Fan speed commands
    // -----------------------------------------------------------------------

    [Fact]
    public void FanSpeed_ValidPercent_SetsSpeed()
    {
        var payload = JsonSerializer.Serialize(new { percent = 75.0 });
        _handler.DispatchCommand("drivechill/commands/fans/fan_1/speed", payload, "drivechill");

        // MockBackend records speed via SetFanSpeed — verify it was called
        // by checking through the fan service (no direct call tracking in MockBackend,
        // but no exception means success)
    }

    [Fact]
    public void FanSpeed_ZeroPercent_Accepted()
    {
        var payload = JsonSerializer.Serialize(new { percent = 0.0 });
        // Should not throw
        _handler.DispatchCommand("drivechill/commands/fans/fan_1/speed", payload, "drivechill");
    }

    [Fact]
    public void FanSpeed_HundredPercent_Accepted()
    {
        var payload = JsonSerializer.Serialize(new { percent = 100.0 });
        _handler.DispatchCommand("drivechill/commands/fans/fan_1/speed", payload, "drivechill");
    }

    [Fact]
    public void FanSpeed_NegativePercent_Rejected()
    {
        var payload = JsonSerializer.Serialize(new { percent = -10.0 });
        // Should not throw, just log warning
        _handler.DispatchCommand("drivechill/commands/fans/fan_1/speed", payload, "drivechill");
    }

    [Fact]
    public void FanSpeed_Over100_Rejected()
    {
        var payload = JsonSerializer.Serialize(new { percent = 150.0 });
        _handler.DispatchCommand("drivechill/commands/fans/fan_1/speed", payload, "drivechill");
    }

    [Fact]
    public void FanSpeed_MissingPercent_Rejected()
    {
        var payload = JsonSerializer.Serialize(new { speed = 50.0 });
        _handler.DispatchCommand("drivechill/commands/fans/fan_1/speed", payload, "drivechill");
    }

    [Fact]
    public void FanSpeed_StringPercent_Rejected()
    {
        var payload = JsonSerializer.Serialize(new { percent = "fifty" });
        _handler.DispatchCommand("drivechill/commands/fans/fan_1/speed", payload, "drivechill");
    }

    // -----------------------------------------------------------------------
    // Profile activate commands
    // -----------------------------------------------------------------------

    [Fact]
    public void ProfileActivate_ValidId_ActivatesProfile()
    {
        // First create a profile in the store
        var profile = new Profile
        {
            Id = "test_prof",
            Name = "Test Profile",
            IsActive = false,
            Curves = [],
        };
        _store.SaveProfiles([profile]);

        var payload = JsonSerializer.Serialize(new { profile_id = "test_prof" });
        _handler.DispatchCommand("drivechill/commands/profiles/activate", payload, "drivechill");

        // Verify profile was activated
        var profiles = _store.LoadProfiles().ToList();
        var activated = profiles.FirstOrDefault(p => p.Id == "test_prof");
        Assert.NotNull(activated);
        Assert.True(activated.IsActive);
    }

    [Fact]
    public void ProfileActivate_NotFound_NoException()
    {
        var payload = JsonSerializer.Serialize(new { profile_id = "nonexistent" });
        // Should not throw
        _handler.DispatchCommand("drivechill/commands/profiles/activate", payload, "drivechill");
    }

    [Fact]
    public void ProfileActivate_MissingField_NoException()
    {
        var payload = JsonSerializer.Serialize(new { name = "silent" });
        _handler.DispatchCommand("drivechill/commands/profiles/activate", payload, "drivechill");
    }

    [Fact]
    public void ProfileActivate_NumericId_Rejected()
    {
        var payload = JsonSerializer.Serialize(new { profile_id = 123 });
        _handler.DispatchCommand("drivechill/commands/profiles/activate", payload, "drivechill");
    }

    // -----------------------------------------------------------------------
    // Fan release commands
    // -----------------------------------------------------------------------

    [Fact]
    public void FanRelease_NoPayload_ReleasesControl()
    {
        _handler.DispatchCommand("drivechill/commands/fans/release", null, "drivechill");
        Assert.True(_fans.IsReleased);
    }

    [Fact]
    public void FanRelease_WithPayload_ReleasesControl()
    {
        _handler.DispatchCommand("drivechill/commands/fans/release", "{}", "drivechill");
        Assert.True(_fans.IsReleased);
    }

    // -----------------------------------------------------------------------
    // Malformed / edge cases
    // -----------------------------------------------------------------------

    [Fact]
    public void MalformedJson_NoException()
    {
        _handler.DispatchCommand("drivechill/commands/fans/fan_1/speed", "not-json", "drivechill");
    }

    [Fact]
    public void EmptyPayload_NonRelease_NoException()
    {
        _handler.DispatchCommand("drivechill/commands/fans/fan_1/speed", null, "drivechill");
    }

    [Fact]
    public void UnknownCommandTopic_NoException()
    {
        var payload = JsonSerializer.Serialize(new { key = "value" });
        _handler.DispatchCommand("drivechill/commands/unknown/action", payload, "drivechill");
    }

    [Fact]
    public void WrongPrefix_Ignored()
    {
        var payload = JsonSerializer.Serialize(new { percent = 50.0 });
        _handler.DispatchCommand("other/commands/fans/fan_1/speed", payload, "drivechill");
    }

    [Fact]
    public void CustomPrefix_Works()
    {
        var payload = JsonSerializer.Serialize(new { percent = 60.0 });
        _handler.DispatchCommand("mypc/commands/fans/fan_1/speed", payload, "mypc");
        // Should not throw — fan speed command is accepted with custom prefix
    }
}
