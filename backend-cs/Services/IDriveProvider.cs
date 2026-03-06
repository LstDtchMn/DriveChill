using DriveChill.Models;

namespace DriveChill.Services;

/// <summary>
/// Abstraction over a physical drive data source.
/// Allows swapping in a mock for unit tests and adding future providers
/// (e.g. USB HID fan controllers).
/// </summary>
public interface IDriveProvider
{
    /// <summary>Returns true if the provider's underlying tool/hardware is available.</summary>
    Task<bool> CheckAvailableAsync(DriveSettings s, CancellationToken ct);

    /// <summary>Discover all drives and return their full data snapshots.</summary>
    Task<IReadOnlyList<DriveRawData>> DiscoverDrivesAsync(DriveSettings s, CancellationToken ct);

    /// <summary>Fetch the latest data for a single drive by device path.</summary>
    Task<DriveRawData?> GetDriveDataAsync(string devicePath, DriveSettings s, CancellationToken ct);

    /// <summary>Start a self-test of the given type. Returns a token string or null on failure.</summary>
    Task<string?> StartSelfTestAsync(string devicePath, string testType, DriveSettings s, CancellationToken ct);

    /// <summary>Abort an in-progress self-test. Returns true on success.</summary>
    Task<bool> AbortSelfTestAsync(string devicePath, DriveSettings s, CancellationToken ct);
}
