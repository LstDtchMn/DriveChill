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
    public string Severity     { get; set; } = "warning";
}

public sealed class AnalyticsCorrelationSample
{
    public long   Epoch { get; set; }
    public double X     { get; set; }
    public double Y     { get; set; }
}

public sealed class AnalyticsRegression
{
    public string  SensorId    { get; set; } = "";
    public string  SensorName  { get; set; } = "";
    public double  BaselineAvg { get; set; }
    public double  RecentAvg   { get; set; }
    public double  Delta       { get; set; }
    public string  Severity    { get; set; } = "warning";
    public string  Message     { get; set; } = "";
    /// <summary>Load band (low/medium/high) when load_band_aware is true; null otherwise.</summary>
    public string? LoadBand    { get; set; }
}
