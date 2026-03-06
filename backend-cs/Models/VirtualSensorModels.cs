namespace DriveChill.Models;

public sealed class VirtualSensor
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "max"; // max | min | avg | weighted | delta | moving_avg
    public List<string> SourceIds { get; set; } = new();
    public List<double>? Weights { get; set; }
    public double? WindowSeconds { get; set; }
    public double Offset { get; set; }
    public bool Enabled { get; set; } = true;
    public string? CreatedAt { get; set; }
    public string? UpdatedAt { get; set; }
}

public sealed class VirtualSensorRequest
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "max";
    public List<string> SourceIds { get; set; } = new();
    public List<double>? Weights { get; set; }
    public double? WindowSeconds { get; set; }
    public double Offset { get; set; }
    public bool Enabled { get; set; } = true;
}
