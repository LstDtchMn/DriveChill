using System;
using System.IO;
using System.Threading.Tasks;
using DriveChill.Models;
using DriveChill.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DriveChill.Tests;

/// <summary>
/// Tests for machine status tracking: consecutive_failures increments, recovery resets,
/// last_seen_at updates, last_command_at, and default field values after creation.
/// </summary>
public sealed class MachineStatusTests : IDisposable
{
    private readonly string    _tempDir;
    private readonly DbService _db;

    public MachineStatusTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
        Environment.SetEnvironmentVariable("DRIVECHILL_DATA_DIR", _tempDir);

        var settings = new AppSettings();
        _db = new DbService(settings, NullLogger<DbService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        Environment.SetEnvironmentVariable("DRIVECHILL_DATA_DIR", null);
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private Task<MachineRecord> CreateMachineAsync(string name = "TestMachine",
        string baseUrl = "https://machine.example.com")
    {
        var record = new MachineRecord { Name = name, BaseUrl = baseUrl };
        return _db.CreateMachineAsync(record);
    }

    // -----------------------------------------------------------------------
    // Default field values
    // -----------------------------------------------------------------------

    [Fact]
    public async Task NewMachine_HasDefaultStatus_Unknown()
    {
        var machine = await CreateMachineAsync();
        Assert.Equal("unknown", machine.Status);
    }

    [Fact]
    public async Task NewMachine_HasZeroConsecutiveFailures()
    {
        var machine = await CreateMachineAsync();
        Assert.Equal(0, machine.ConsecutiveFailures);
    }

    [Fact]
    public async Task NewMachine_HasNullLastSeenAt()
    {
        var machine = await CreateMachineAsync();
        Assert.Null(machine.LastSeenAt);
    }

    [Fact]
    public async Task NewMachine_HasNullLastError()
    {
        var machine = await CreateMachineAsync();
        Assert.Null(machine.LastError);
    }

    [Fact]
    public async Task NewMachine_HasDefaultPollIntervalAndTimeout()
    {
        var machine = await CreateMachineAsync();
        Assert.Equal(30.0, machine.PollIntervalSeconds);
        Assert.Equal(5000, machine.TimeoutMs);
    }

    [Fact]
    public async Task NewMachine_HasNullLastCommandAt()
    {
        var machine = await CreateMachineAsync();
        Assert.Null(machine.LastCommandAt);
    }

    [Fact]
    public async Task NewMachine_IsEnabledByDefault()
    {
        var machine = await CreateMachineAsync();
        Assert.True(machine.Enabled);
    }

    // -----------------------------------------------------------------------
    // UpdateMachineStatusAsync — going offline
    // -----------------------------------------------------------------------

    [Fact]
    public async Task UpdateMachineStatus_SetsOfflineStatus()
    {
        var machine = await CreateMachineAsync();

        await _db.UpdateMachineStatusAsync(machine.Id, "offline", null, "connection refused", 1, null);

        var updated = await _db.GetMachineAsync(machine.Id);
        Assert.NotNull(updated);
        Assert.Equal("offline", updated!.Status);
    }

    [Fact]
    public async Task UpdateMachineStatus_SetsLastError()
    {
        var machine = await CreateMachineAsync();

        await _db.UpdateMachineStatusAsync(machine.Id, "offline", null, "timeout", 1, null);

        var updated = await _db.GetMachineAsync(machine.Id);
        Assert.Equal("timeout", updated!.LastError);
    }

    [Fact]
    public async Task UpdateMachineStatus_IncrementsConsecutiveFailures()
    {
        var machine = await CreateMachineAsync();

        await _db.UpdateMachineStatusAsync(machine.Id, "offline", null, "err", 1, null);
        var after1 = await _db.GetMachineAsync(machine.Id);
        Assert.Equal(1, after1!.ConsecutiveFailures);

        await _db.UpdateMachineStatusAsync(machine.Id, "offline", null, "err", 2, null);
        var after2 = await _db.GetMachineAsync(machine.Id);
        Assert.Equal(2, after2!.ConsecutiveFailures);
    }

    [Fact]
    public async Task UpdateMachineStatus_MultipleFailures_AccumulatesCount()
    {
        var machine = await CreateMachineAsync();

        for (int i = 1; i <= 5; i++)
            await _db.UpdateMachineStatusAsync(machine.Id, "offline", null, "err", i, null);

        var updated = await _db.GetMachineAsync(machine.Id);
        Assert.Equal(5, updated!.ConsecutiveFailures);
    }

    // -----------------------------------------------------------------------
    // UpdateMachineStatusAsync — coming online (recovery resets)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task UpdateMachineStatus_OnlineAfterFailures_ResetsConsecutiveFailures()
    {
        var machine = await CreateMachineAsync();

        // Simulate 3 consecutive failures
        await _db.UpdateMachineStatusAsync(machine.Id, "offline", null, "err", 3, null);

        // Recovery
        var nowStr = DateTimeOffset.UtcNow.ToString("o");
        await _db.UpdateMachineStatusAsync(machine.Id, "online", nowStr, null, 0, null);

        var updated = await _db.GetMachineAsync(machine.Id);
        Assert.Equal("online", updated!.Status);
        Assert.Equal(0, updated.ConsecutiveFailures);
    }

    [Fact]
    public async Task UpdateMachineStatus_OnlineStatus_SetsLastSeenAt()
    {
        var machine = await CreateMachineAsync();
        var seenAt  = DateTimeOffset.UtcNow.ToString("o");

        await _db.UpdateMachineStatusAsync(machine.Id, "online", seenAt, null, 0, null);

        var updated = await _db.GetMachineAsync(machine.Id);
        Assert.NotNull(updated!.LastSeenAt);
        // Stored string should contain the date portion
        Assert.Contains(DateTimeOffset.UtcNow.ToString("yyyy-MM-dd"), updated.LastSeenAt);
    }

    [Fact]
    public async Task UpdateMachineStatus_Recovery_ClearsLastError()
    {
        var machine = await CreateMachineAsync();

        // Set an error first
        await _db.UpdateMachineStatusAsync(machine.Id, "offline", null, "connection refused", 1, null);
        var errored = await _db.GetMachineAsync(machine.Id);
        Assert.NotNull(errored!.LastError);

        // Recovery clears the error
        var nowStr = DateTimeOffset.UtcNow.ToString("o");
        await _db.UpdateMachineStatusAsync(machine.Id, "online", nowStr, null, 0, null);

        var recovered = await _db.GetMachineAsync(machine.Id);
        Assert.Null(recovered!.LastError);
    }

    // -----------------------------------------------------------------------
    // UpdateMachineStatusAsync — snapshot_json
    // -----------------------------------------------------------------------

    [Fact]
    public async Task UpdateMachineStatus_PersistsSnapshotJson()
    {
        var machine      = await CreateMachineAsync();
        var snapshotJson = """{"fans":[{"id":"fan1","speed":800}]}""";
        var nowStr       = DateTimeOffset.UtcNow.ToString("o");

        await _db.UpdateMachineStatusAsync(machine.Id, "online", nowStr, null, 0, snapshotJson);

        var updated = await _db.GetMachineAsync(machine.Id);
        Assert.Equal(snapshotJson, updated!.SnapshotJson);
    }

    [Fact]
    public async Task UpdateMachineStatus_NullSnapshot_LeavesSnapshotNull()
    {
        var machine = await CreateMachineAsync();
        var nowStr  = DateTimeOffset.UtcNow.ToString("o");

        await _db.UpdateMachineStatusAsync(machine.Id, "online", nowStr, null, 0, null);

        var updated = await _db.GetMachineAsync(machine.Id);
        Assert.Null(updated!.SnapshotJson);
    }

    // -----------------------------------------------------------------------
    // SetMachineLastCommandAsync
    // -----------------------------------------------------------------------

    [Fact]
    public async Task SetMachineLastCommandAsync_PopulatesLastCommandAt()
    {
        var machine = await CreateMachineAsync();
        Assert.Null(machine.LastCommandAt);

        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        await _db.SetMachineLastCommandAsync(machine.Id);

        var updated = await _db.GetMachineAsync(machine.Id);
        Assert.NotNull(updated!.LastCommandAt);

        Assert.True(DateTimeOffset.TryParse(updated.LastCommandAt, out var ts));
        Assert.True(ts >= before);
    }

    [Fact]
    public async Task SetMachineLastCommandAsync_UpdatesTimestampOnRepeatCalls()
    {
        var machine = await CreateMachineAsync();

        await _db.SetMachineLastCommandAsync(machine.Id);
        var after1 = await _db.GetMachineAsync(machine.Id);
        var ts1    = after1!.LastCommandAt;

        // Small delay to ensure a different timestamp
        await Task.Delay(20);

        await _db.SetMachineLastCommandAsync(machine.Id);
        var after2 = await _db.GetMachineAsync(machine.Id);
        var ts2    = after2!.LastCommandAt;

        // Both should be set; second should be >= first
        Assert.NotNull(ts1);
        Assert.NotNull(ts2);
        Assert.True(DateTimeOffset.Parse(ts2!) >= DateTimeOffset.Parse(ts1!));
    }

    // -----------------------------------------------------------------------
    // Eviction / status isolation between machines
    // -----------------------------------------------------------------------

    [Fact]
    public async Task UpdateMachineStatus_DoesNotAffectOtherMachines()
    {
        var m1 = await CreateMachineAsync("Machine1", "https://m1.example.com");
        var m2 = await CreateMachineAsync("Machine2", "https://m2.example.com");

        // Mark m1 as offline with 3 failures
        await _db.UpdateMachineStatusAsync(m1.Id, "offline", null, "err", 3, null);

        // m2 should remain untouched
        var m2After = await _db.GetMachineAsync(m2.Id);
        Assert.Equal("unknown",  m2After!.Status);
        Assert.Equal(0, m2After.ConsecutiveFailures);
        Assert.Null(m2After.LastError);
    }

    [Fact]
    public async Task MultipleFailuresThenRecovery_OnlyAffectsTargetMachine()
    {
        var m1 = await CreateMachineAsync("Alpha", "https://alpha.example.com");
        var m2 = await CreateMachineAsync("Beta",  "https://beta.example.com");

        // Degrade m1
        await _db.UpdateMachineStatusAsync(m1.Id, "offline", null, "err", 5, null);

        // Recover m1
        var nowStr = DateTimeOffset.UtcNow.ToString("o");
        await _db.UpdateMachineStatusAsync(m1.Id, "online", nowStr, null, 0, null);

        // m2 is still unknown/0
        var m2After = await _db.GetMachineAsync(m2.Id);
        Assert.Equal("unknown", m2After!.Status);
        Assert.Equal(0, m2After.ConsecutiveFailures);
    }

    // -----------------------------------------------------------------------
    // UpdatedAt advances on status change
    // -----------------------------------------------------------------------

    [Fact]
    public async Task UpdateMachineStatus_AdvancesUpdatedAt()
    {
        var machine    = await CreateMachineAsync();
        var createdUpdatedAt = machine.UpdatedAt;

        // Wait a tick to get a measurably later timestamp
        await Task.Delay(10);

        await _db.UpdateMachineStatusAsync(machine.Id, "offline", null, "err", 1, null);

        var updated = await _db.GetMachineAsync(machine.Id);
        Assert.True(DateTimeOffset.Parse(updated!.UpdatedAt) >= DateTimeOffset.Parse(createdUpdatedAt));
    }
}
