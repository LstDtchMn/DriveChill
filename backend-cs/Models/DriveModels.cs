using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

namespace DriveChill.Models;

// ── Enums ────────────────────────────────────────────────────────────────────

public enum BusType { Sata, Nvme, Usb, Raid, Unknown }
public enum MediaType { Hdd, Ssd, Nvme, Unknown }
public enum HealthStatus { Good, Warning, Critical, Unknown }
public enum SelfTestType { Short, Extended, Conveyance }
public enum SelfTestStatus { Queued, Running, Passed, Failed, Aborted, Unsupported }

// ── Capability set ────────────────────────────────────────────────────────────

public sealed class DriveCapabilitySet
{
    public bool SmartRead { get; init; }
    public bool SmartSelfTestShort { get; init; }
    public bool SmartSelfTestExtended { get; init; }
    public bool SmartSelfTestConveyance { get; init; }
    public bool SmartSelfTestAbort { get; init; }
    public string TemperatureSource { get; init; } = "none";
    public string HealthSource { get; init; } = "none";
}

// ── Raw attribute ─────────────────────────────────────────────────────────────

public sealed class DriveRawAttribute
{
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public int? NormalizedValue { get; init; }
    public int? WorstValue { get; init; }
    public int? Threshold { get; init; }
    public string RawValue { get; init; } = "";
    public string Status { get; init; } = "unknown";
    public string SourceKind { get; init; } = "ata_smart";
}

// ── Self-test run ─────────────────────────────────────────────────────────────

public sealed class DriveSelfTestRun
{
    public string Id { get; init; } = "";
    public string DriveId { get; init; } = "";
    public string Type { get; init; } = "short";
    public string Status { get; init; } = "queued";
    public double? ProgressPercent { get; init; }
    public string StartedAt { get; init; } = "";
    public string? FinishedAt { get; init; }
    public string? FailureMessage { get; init; }
    public string? ProviderRunRef { get; init; }
}

// ── Drive summary (list response) ─────────────────────────────────────────────

public class DriveSummary
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Model { get; init; } = "";
    public string SerialMasked { get; init; } = "****";
    public string DevicePathMasked { get; init; } = "";
    public string BusType { get; init; } = "unknown";
    public string MediaType { get; init; } = "unknown";
    public long CapacityBytes { get; init; }
    public double? TemperatureC { get; init; }
    public string HealthStatus { get; init; } = "unknown";
    public double? HealthPercent { get; init; }
    public bool SmartAvailable { get; init; }
    public bool NativeAvailable { get; init; }
    public bool SupportsSelfTest { get; init; }
    public bool SupportsAbort { get; init; }
    public string? LastUpdatedAt { get; init; }
}

// ── Drive detail (single drive response) ─────────────────────────────────────

public sealed class DriveDetail : DriveSummary
{
    public string SerialFull { get; init; } = "";
    public string DevicePath { get; init; } = "";
    public string FirmwareVersion { get; init; } = "";
    public string? InterfaceSpeed { get; init; }
    public int? RotationRateRpm { get; init; }
    public long? PowerOnHours { get; init; }
    public long? PowerCycleCount { get; init; }
    public long? UnsafeShutdowns { get; init; }
    public double? WearPercentUsed { get; init; }
    public double? AvailableSparePercent { get; init; }
    public long? ReallocatedSectors { get; init; }
    public long? PendingSectors { get; init; }
    public long? UncorrectableErrors { get; init; }
    public long? MediaErrors { get; init; }
    public bool PredictedFailure { get; init; }
    public double TemperatureWarningC { get; init; }
    public double TemperatureCriticalC { get; init; }
    public DriveCapabilitySet Capabilities { get; init; } = new();
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public DriveSelfTestRun? LastSelfTest { get; init; }
    public IReadOnlyList<DriveRawAttribute> RawAttributes { get; init; } = [];
    public int HistoryRetentionHoursEffective { get; init; } = 168;
}

// ── Drive settings ────────────────────────────────────────────────────────────

public sealed class DriveSettings
{
    public bool Enabled { get; set; } = true;
    public bool NativeProviderEnabled { get; set; } = true;
    public bool SmartctlProviderEnabled { get; set; } = true;
    [RegularExpression(@"^[a-zA-Z0-9_./ :\\-]{1,260}$", ErrorMessage = "smartctl_path contains invalid characters")]
    public string SmartctlPath { get; set; } = "smartctl";
    [Range(5, 86400)] public int FastPollSeconds { get; set; } = 15;
    [Range(5, 86400)] public int HealthPollSeconds { get; set; } = 300;
    [Range(5, 86400)] public int RescanPollSeconds { get; set; } = 900;
    public double HddTempWarningC { get; set; } = 45.0;
    public double HddTempCriticalC { get; set; } = 50.0;
    public double SsdTempWarningC { get; set; } = 55.0;
    public double SsdTempCriticalC { get; set; } = 65.0;
    public double NvmeTempWarningC { get; set; } = 65.0;
    public double NvmeTempCriticalC { get; set; } = 75.0;
    public double WearWarningPercentUsed { get; set; } = 80.0;
    public double WearCriticalPercentUsed { get; set; } = 90.0;
}

// ── Per-drive override ────────────────────────────────────────────────────────

public sealed class DriveSettingsOverride
{
    public double? TempWarningC { get; set; }
    public double? TempCriticalC { get; set; }
    public bool? AlertsEnabled { get; set; }
    public bool? CurvePickerEnabled { get; set; }
}

// ── Internal raw data (provider → normalizer) ─────────────────────────────────

public sealed class DriveRawData
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Model { get; init; } = "";
    public string Serial { get; init; } = "";
    public string DevicePath { get; init; } = "";
    public string BusType { get; init; } = "unknown";
    public string MediaType { get; init; } = "unknown";
    public long CapacityBytes { get; init; }
    public string FirmwareVersion { get; init; } = "";
    public string? InterfaceSpeed { get; init; }
    public int? RotationRateRpm { get; init; }
    public double? TemperatureC { get; init; }
    public long? PowerOnHours { get; init; }
    public long? PowerCycleCount { get; init; }
    public long? UnsafeShutdowns { get; init; }
    public double? WearPercentUsed { get; init; }
    public double? AvailableSparePercent { get; init; }
    public long? ReallocatedSectors { get; init; }
    public long? PendingSectors { get; init; }
    public long? UncorrectableErrors { get; init; }
    public long? MediaErrors { get; init; }
    public bool PredictedFailure { get; init; }
    public string? SmartOverallHealth { get; init; }
    public DriveCapabilitySet Capabilities { get; init; } = new();
    public IReadOnlyList<DriveRawAttribute> RawAttributes { get; init; } = [];
}
