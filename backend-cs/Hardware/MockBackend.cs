using DriveChill.Models;

namespace DriveChill.Hardware;

/// <summary>
/// Mock backend for development — no hardware required.
/// Generates oscillating sensor values that mirror Python mock_backend.py.
/// Set env var DRIVECHILL_BACKEND=mock to activate.
/// </summary>
public sealed class MockBackend : IHardwareBackend
{
    private readonly DateTime _start = DateTime.UtcNow;
    private readonly Random _rng = new();
    private readonly Dictionary<string, double> _fanSpeeds = new()
    {
        ["fan_cpu"]        = 45.0,
        ["fan_gpu"]        = 40.0,
        ["fan_case_front"] = 35.0,
        ["fan_case_rear"]  = 35.0,
    };

    public void Initialize() { }
    public void Dispose() { }

    private double Wave(double baseVal, double amplitude, double period, double offset = 0)
    {
        var elapsed = (DateTime.UtcNow - _start).TotalSeconds;
        var noise = (_rng.NextDouble() - 0.5) * 3.0;
        return baseVal + amplitude * Math.Sin(2 * Math.PI * elapsed / period + offset) + noise;
    }

    private SensorReading R(string id, string name, string type,
        double val, double? min, double? max, string unit) =>
        new() { Id = id, Name = name, SensorType = type,
                Value = Math.Round(val, 1), MinValue = min, MaxValue = max, Unit = unit };

    public IReadOnlyList<SensorReading> GetSensorReadings()
    {
        var readings = new List<SensorReading>
        {
            R("cpu_temp_0", "CPU Package",     SensorTypeValues.CpuTemp,    Wave(55, 15, 120),      30, 100, "\u00b0C"),
            R("cpu_temp_1", "CPU Core 0",      SensorTypeValues.CpuTemp,    Wave(53, 14, 115, 0.5), 30, 100, "\u00b0C"),
            R("cpu_temp_2", "CPU Core 1",      SensorTypeValues.CpuTemp,    Wave(54, 13, 110, 1.0), 30, 100, "\u00b0C"),
            R("cpu_load_0", "CPU Usage",       SensorTypeValues.CpuLoad,    Wave(35, 25, 90),         0, 100, "%"),
            R("gpu_temp_0", "GPU Temperature", SensorTypeValues.GpuTemp,    Wave(48, 18, 150, 2.0), 25, 95,  "\u00b0C"),
            R("gpu_load_0", "GPU Usage",       SensorTypeValues.GpuLoad,    Wave(30, 30, 100, 1.5),  0, 100, "%"),
            R("hdd_temp_0", "SSD (C:)",        SensorTypeValues.HddTemp,    Wave(38, 5, 200),        20, 70,  "\u00b0C"),
            R("hdd_temp_1", "HDD (D:)",        SensorTypeValues.HddTemp,    Wave(36, 4, 180, 3.0),   20, 70,  "\u00b0C"),
            R("case_temp_0","Case Ambient",    SensorTypeValues.CaseTemp,   Wave(32, 3, 300),        20, 55,  "\u00b0C"),
        };

        foreach (var (fanId, pct) in _fanSpeeds)
        {
            double maxRpm = fanId.Contains("cpu") ? 1800 : 1200;
            double rpm = maxRpm * (pct / 100) + (_rng.NextDouble() - 0.5) * 40;
            readings.Add(R($"{fanId}_rpm", fanId.Replace("_", " ").ToUpperInvariant() + " RPM",
                SensorTypeValues.FanRpm, Math.Max(0, rpm), 0, maxRpm, "RPM"));
            readings.Add(R($"{fanId}_pct", fanId.Replace("_", " ").ToUpperInvariant() + " Speed",
                SensorTypeValues.FanPercent, Math.Round(pct, 1), 0, 100, "%"));
        }

        return readings;
    }

    public bool SetFanSpeed(string fanId, double speedPercent)
    {
        if (!_fanSpeeds.ContainsKey(fanId)) return false;
        _fanSpeeds[fanId] = Math.Clamp(speedPercent, 0, 100);
        return true;
    }

    public bool SetFanAuto(string fanId) => _fanSpeeds.ContainsKey(fanId);

    public IReadOnlyList<string> GetFanIds() => [.. _fanSpeeds.Keys];

    public string GetBackendName() => "Mock (Development)";
}
