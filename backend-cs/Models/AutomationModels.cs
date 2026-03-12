namespace DriveChill.Models;

public sealed class NoiseDataPoint
{
    public double Rpm { get; set; }
    public double Db  { get; set; }
}

public sealed class NoiseProfile
{
    public string Id        { get; set; } = "";
    public string FanId     { get; set; } = "";
    public string Mode      { get; set; } = "quick";
    public List<NoiseDataPoint> Data { get; set; } = [];
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
}

public sealed class ReportScheduleRecord
{
    public string Id         { get; set; } = "";
    public string Frequency  { get; set; } = "daily";
    public string TimeUtc    { get; set; } = "08:00";
    public string Timezone   { get; set; } = "UTC";
    public bool Enabled      { get; set; } = true;
    public string? LastSentAt { get; set; }
    public string CreatedAt  { get; set; } = "";
    public string? LastError  { get; set; }
    public string? LastAttemptedAt { get; set; }
    public int ConsecutiveFailures { get; set; }
}

public sealed class ProfileScheduleRecord
{
    public string Id         { get; set; } = "";
    public string ProfileId  { get; set; } = "";
    public string StartTime  { get; set; } = "00:00";
    public string EndTime    { get; set; } = "00:00";
    public string DaysOfWeek { get; set; } = "0,1,2,3,4,5,6";
    public string Timezone   { get; set; } = "UTC";
    public bool Enabled      { get; set; } = true;
    public string CreatedAt  { get; set; } = "";
}

public sealed class AnnotationRecord
{
    public string Id           { get; set; } = "";
    public string EventType    { get; set; } = "annotation";
    public string TimestampUtc { get; set; } = "";
    public string Label        { get; set; } = "";
    public string? Description { get; set; }
    public string? MetadataJson { get; set; }
    public string CreatedAt    { get; set; } = "";
}
