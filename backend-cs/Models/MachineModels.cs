namespace DriveChill.Models;

public sealed class MachineRecord
{
    public string  Id                   { get; set; } = "";
    public string  Name                 { get; set; } = "";
    public string  BaseUrl              { get; set; } = "";
    public string? ApiKeyHash           { get; set; }
    public bool    Enabled              { get; set; } = true;
    public double  PollIntervalSeconds  { get; set; } = 30.0;
    public int     TimeoutMs            { get; set; } = 5000;
    public string  Status               { get; set; } = "unknown";
    public string? LastSeenAt           { get; set; }
    public string? LastError            { get; set; }
    public int     ConsecutiveFailures  { get; set; }
    public string  CreatedAt            { get; set; } = "";
    public string  UpdatedAt            { get; set; } = "";
    public double? FreshnessSeconds     { get; set; }
    public string? SnapshotJson         { get; set; }
    public string  CapabilitiesJson     { get; set; } = "[]";
    public string? LastCommandAt        { get; set; }
}
