using DriveChill.Models;

namespace DriveChill.Services;

/// <summary>
/// Configurable in-memory drive provider for unit tests and development.
/// Registered when <c>DRIVECHILL_BACKEND=mock</c>.
/// </summary>
public sealed class MockDriveProvider : IDriveProvider
{
    /// <summary>Drives returned by <see cref="DiscoverDrivesAsync"/> and <see cref="GetDriveDataAsync"/>.</summary>
    public List<DriveRawData> Drives { get; set; } = [];

    /// <summary>Controls <see cref="CheckAvailableAsync"/> return value.</summary>
    public bool Available { get; set; } = true;

    /// <summary>Token returned by <see cref="StartSelfTestAsync"/>. Null simulates failure.</summary>
    public string? SelfTestToken { get; set; } = "mock_selftest_token";

    /// <summary>Controls <see cref="AbortSelfTestAsync"/> return value.</summary>
    public bool AbortResult { get; set; } = true;

    // ── IDriveProvider ────────────────────────────────────────────────────────

    public Task<bool> CheckAvailableAsync(DriveSettings s, CancellationToken ct)
        => Task.FromResult(Available);

    public Task<IReadOnlyList<DriveRawData>> DiscoverDrivesAsync(DriveSettings s, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<DriveRawData>>(Drives);

    public Task<DriveRawData?> GetDriveDataAsync(string devicePath, DriveSettings s, CancellationToken ct)
        => Task.FromResult(Drives.FirstOrDefault(d => d.DevicePath == devicePath));

    public Task<string?> StartSelfTestAsync(string devicePath, string testType, DriveSettings s, CancellationToken ct)
        => Task.FromResult(SelfTestToken);

    public Task<bool> AbortSelfTestAsync(string devicePath, DriveSettings s, CancellationToken ct)
        => Task.FromResult(AbortResult);
}
