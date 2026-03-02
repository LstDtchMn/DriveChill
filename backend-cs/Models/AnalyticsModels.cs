namespace DriveChill.Models;

public sealed class AnalyticsBucket
{
    public string SensorId     { get; set; } = "";
    public string SensorName   { get; set; } = "";
    public string SensorType   { get; set; } = "";
    public string Unit         { get; set; } = "";
    public string TimestampUtc { get; set; } = "";
    public double AvgValue     { get; set; }
    public double MinValue     { get; set; }
    public double MaxValue     { get; set; }
    public int    SampleCount  { get; set; }
}

public sealed class AnalyticsStat
{
    public string SensorId    { get; set; } = "";
    public string SensorName  { get; set; } = "";
    public string SensorType  { get; set; } = "";
    public string Unit        { get; set; } = "";
    public double MinValue    { get; set; }
    public double MaxValue    { get; set; }
    public double AvgValue    { get; set; }
    public double P95Value    { get; set; }
    public int    SampleCount { get; set; }
}

public sealed class AnalyticsAnomaly
{
    public string TimestampUtc { get; set; } = "";
    public string SensorId     { get; set; } = "";
    public string SensorName   { get; set; } = "";
    public double Value        { get; set; }
    public string Unit         { get; set; } = "";
    public double ZScore       { get; set; }
    public double Mean         { get; set; }
    public double Stdev        { get; set; }
}
