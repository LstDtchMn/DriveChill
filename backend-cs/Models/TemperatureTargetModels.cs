using System.ComponentModel.DataAnnotations;

namespace DriveChill.Models;

public sealed class TemperatureTarget
{
    public string  Id          { get; set; } = "";
    public string  Name        { get; set; } = "";
    public string? DriveId     { get; set; }
    public string  SensorId    { get; set; } = "";
    public string[] FanIds     { get; set; } = [];
    public double  TargetTempC { get; set; }
    public double  ToleranceC  { get; set; } = 5.0;
    public double  MinFanSpeed { get; set; } = 20.0;
    public bool    Enabled     { get; set; } = true;
}

public sealed class TemperatureTargetCreateRequest
{
    public string  Name        { get; set; } = "";
    public string? DriveId     { get; set; }
    public string  SensorId    { get; set; } = "";
    public string[] FanIds     { get; set; } = [];
    [Range(20.0, 85.0)] public double  TargetTempC { get; set; }
    [Range(1.0, 20.0)]  public double  ToleranceC  { get; set; } = 5.0;
    [Range(0.0, 100.0)] public double  MinFanSpeed { get; set; } = 20.0;
}

public sealed class TemperatureTargetUpdateRequest
{
    public string  Name        { get; set; } = "";
    public string? DriveId     { get; set; }
    public string  SensorId    { get; set; } = "";
    public string[] FanIds     { get; set; } = [];
    [Range(20.0, 85.0)] public double  TargetTempC { get; set; }
    [Range(1.0, 20.0)]  public double  ToleranceC  { get; set; } = 5.0;
    [Range(0.0, 100.0)] public double  MinFanSpeed { get; set; } = 20.0;
}

public sealed class TemperatureTargetToggleRequest
{
    public bool Enabled { get; set; }
}
