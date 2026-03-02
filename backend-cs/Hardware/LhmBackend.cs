using DriveChill.Models;
using LibreHardwareMonitor.Hardware;

namespace DriveChill.Hardware;

/// <summary>
/// Real hardware backend using LibreHardwareMonitorLib NuGet package.
///
/// LHM is not thread-safe, so all Computer interactions are serialized
/// with a SemaphoreSlim(1,1). The SensorWorker calls GetSensorReadings()
/// from Task.Run(), so mutual exclusion is all we need — no dedicated thread.
///
/// Requires administrator elevation (handled by app.manifest UAC).
/// </summary>
public sealed class LhmBackend : IHardwareBackend
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private Computer? _computer;
    // stable sensor ID → LHM IControl (fan speed control)
    private readonly Dictionary<string, IControl> _controls = [];
    private bool _initialized;

    // -----------------------------------------------------------------------
    // Lifecycle
    // -----------------------------------------------------------------------

    public void Initialize()
    {
        _lock.Wait();
        try
        {
            _computer = new Computer
            {
                IsCpuEnabled         = true,
                IsGpuEnabled         = true,
                IsStorageEnabled     = true,
                IsMotherboardEnabled = true,   // fan headers on most boards
                IsControllerEnabled  = true,   // USB/HID fan controllers
                IsNetworkEnabled     = false,
                IsMemoryEnabled      = true,
            };
            _computer.Open();
            _initialized = true;
        }
        finally { _lock.Release(); }
    }

    public void Dispose()
    {
        _lock.Wait();
        try
        {
            if (_computer is not null)
            {
                try { _computer.Close(); } catch { }
                _computer = null;
            }
        }
        finally
        {
            _lock.Release();
            _lock.Dispose();
        }
    }

    // -----------------------------------------------------------------------
    // Sensor readings
    // -----------------------------------------------------------------------

    public IReadOnlyList<SensorReading> GetSensorReadings()
    {
        if (!_initialized || _computer is null) return [];

        _lock.Wait();
        try
        {
            var readings = new List<SensorReading>();
            var newControls = new Dictionary<string, IControl>();

            foreach (var hw in _computer.Hardware)
                ProcessHardware(hw, readings, newControls);

            foreach (var (k, v) in newControls)
                _controls[k] = v;

            return readings;
        }
        finally { _lock.Release(); }
    }

    private static void ProcessHardware(
        IHardware hw,
        List<SensorReading> readings,
        Dictionary<string, IControl> controls)
    {
        hw.Update();

        foreach (var sensor in hw.Sensors)
        {
            var (sensorType, unit) = Classify(hw.HardwareType, sensor.SensorType);
            if (sensorType is null) continue;

            var sensorId = MakeSensorId(hw.Identifier.ToString(), sensor.Identifier.ToString());

            readings.Add(new SensorReading
            {
                Id         = sensorId,
                Name       = sensor.Name,
                SensorType = sensorType,
                Value      = sensor.Value.HasValue ? Math.Round((double)sensor.Value.Value, 1) : 0.0,
                MinValue   = sensor.Min.HasValue ? Math.Round((double)sensor.Min.Value, 1) : null,
                MaxValue   = sensor.Max.HasValue ? Math.Round((double)sensor.Max.Value, 1) : null,
                Unit       = unit,
            });

            if (sensor.SensorType == LibreHardwareMonitor.Hardware.SensorType.Control
                && sensor.Control is not null)
                controls[sensorId] = sensor.Control;
        }

        foreach (var sub in hw.SubHardware)
            ProcessHardware(sub, readings, controls);
    }

    // -----------------------------------------------------------------------
    // Sensor classification — mirrors lhm_direct_backend._classify()
    // -----------------------------------------------------------------------

    private static (string? sensorType, string unit) Classify(
        HardwareType hwType,
        LibreHardwareMonitor.Hardware.SensorType stType)
    {
        bool isGpu     = hwType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel;
        bool isStorage = hwType == HardwareType.Storage;
        bool isMobo    = hwType is HardwareType.Motherboard or HardwareType.SuperIO;

        return stType switch
        {
            LibreHardwareMonitor.Hardware.SensorType.Temperature when isGpu     => (SensorTypeValues.GpuTemp,    "\u00b0C"),
            LibreHardwareMonitor.Hardware.SensorType.Temperature when isStorage => (SensorTypeValues.HddTemp,    "\u00b0C"),
            LibreHardwareMonitor.Hardware.SensorType.Temperature when isMobo    => (SensorTypeValues.CaseTemp,   "\u00b0C"),
            LibreHardwareMonitor.Hardware.SensorType.Temperature                => (SensorTypeValues.CpuTemp,    "\u00b0C"),
            LibreHardwareMonitor.Hardware.SensorType.Load when isGpu            => (SensorTypeValues.GpuLoad,    "%"),
            LibreHardwareMonitor.Hardware.SensorType.Load                       => (SensorTypeValues.CpuLoad,    "%"),
            LibreHardwareMonitor.Hardware.SensorType.Fan                        => (SensorTypeValues.FanRpm,     "RPM"),
            LibreHardwareMonitor.Hardware.SensorType.Control                    => (SensorTypeValues.FanPercent, "%"),
            _ => (null, ""),
        };
    }

    private static string MakeSensorId(string hwId, string sensorId)
        => "lhm_" + $"{hwId}{sensorId}".TrimStart('/').Replace('/', '_');

    // -----------------------------------------------------------------------
    // Fan control
    // -----------------------------------------------------------------------

    public bool SetFanSpeed(string fanId, double speedPercent)
    {
        _lock.Wait();
        try
        {
            if (!_controls.TryGetValue(fanId, out var control)) return false;
            control.SetSoftware((float)Math.Clamp(speedPercent, 0, 100));
            return true;
        }
        catch { return false; }
        finally { _lock.Release(); }
    }

    public bool SetFanAuto(string fanId)
    {
        _lock.Wait();
        try
        {
            if (!_controls.TryGetValue(fanId, out var control)) return false;
            control.SetDefault();
            return true;
        }
        catch { return false; }
        finally { _lock.Release(); }
    }

    public IReadOnlyList<string> GetFanIds()
    {
        _lock.Wait();
        try { return [.. _controls.Keys]; }
        finally { _lock.Release(); }
    }

    public string GetBackendName() => "LibreHardwareMonitor (.NET)";
}
