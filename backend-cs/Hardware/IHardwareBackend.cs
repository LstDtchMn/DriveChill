using DriveChill.Models;

namespace DriveChill.Hardware;

/// <summary>
/// Abstraction over hardware sensor and fan control systems.
/// All members are synchronous — LHM is a synchronous .NET API.
/// Thread safety is the responsibility of each implementation.
/// </summary>
public interface IHardwareBackend : IDisposable
{
    /// <summary>Initialize hardware (loads kernel drivers). Call once before anything else.</summary>
    void Initialize();

    /// <summary>Read all available sensor data. Thread-safe.</summary>
    IReadOnlyList<SensorReading> GetSensorReadings();

    /// <summary>Set a fan's speed (0–100 %). Returns true on success.</summary>
    bool SetFanSpeed(string fanId, double speedPercent);

    /// <summary>Return a fan to automatic (BIOS/firmware) control.</summary>
    bool SetFanAuto(string fanId);

    /// <summary>IDs of fans that can be controlled via SetFanSpeed.</summary>
    IReadOnlyList<string> GetFanIds();

    /// <summary>Human-readable backend name shown in the UI.</summary>
    string GetBackendName();
}
