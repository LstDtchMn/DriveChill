using Microsoft.Extensions.Logging;

namespace DriveChill.Services;

/// <summary>
/// SMART trend alerting: detects drive degradation and feeds synthetic alerts
/// into the existing alert/notification pipeline.
/// </summary>
public sealed class SmartTrendService
{
    private readonly ILogger<SmartTrendService> _logger;
    private readonly Dictionary<string, DriveSnapshot> _snapshots = [];

    public double WearWarningPct { get; set; } = 80.0;
    public double WearCriticalPct { get; set; } = 90.0;
    public int PowerOnHoursWarning { get; set; } = 35_000;
    public int PowerOnHoursCritical { get; set; } = 50_000;
    public int ReallocatedSectorDeltaThreshold { get; set; } = 1;

    public SmartTrendService(ILogger<SmartTrendService>? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SmartTrendService>.Instance;
    }

    /// <summary>
    /// Check a drive's SMART values for trend alerts.
    /// Returns a list of alert descriptions.
    /// </summary>
    public List<SmartTrendAlert> CheckDrive(
        string driveId,
        string driveName,
        long? reallocatedSectors,
        double? wearPercentUsed,
        long? powerOnHours)
    {
        _snapshots.TryGetValue(driveId, out var prev);
        prev ??= new DriveSnapshot();
        var alerts = new List<SmartTrendAlert>();

        // 1. Reallocated sector increase
        if (reallocatedSectors.HasValue && prev.ReallocatedSectors.HasValue)
        {
            long delta = reallocatedSectors.Value - prev.ReallocatedSectors.Value;
            if (delta >= ReallocatedSectorDeltaThreshold)
            {
                if (!prev.ActiveConditions.Contains("reallocated_increase"))
                {
                    alerts.Add(new SmartTrendAlert
                    {
                        Condition   = "reallocated_increase",
                        Severity    = "critical",
                        Message     = $"{driveName}: reallocated sectors increased by {delta} ({prev.ReallocatedSectors} → {reallocatedSectors})",
                        ActualValue = reallocatedSectors.Value,
                        Threshold   = ReallocatedSectorDeltaThreshold,
                    });
                    prev.ActiveConditions.Add("reallocated_increase");
                }
            }
            else
            {
                prev.ActiveConditions.Remove("reallocated_increase");
            }
        }

        // 2. Wear threshold crossing
        if (wearPercentUsed.HasValue)
        {
            if (wearPercentUsed.Value >= WearCriticalPct)
            {
                if (!prev.ActiveConditions.Contains("wear_critical"))
                {
                    alerts.Add(new SmartTrendAlert
                    {
                        Condition   = "wear_critical",
                        Severity    = "critical",
                        Message     = $"{driveName}: wear level critical ({wearPercentUsed:F1}% used)",
                        ActualValue = wearPercentUsed.Value,
                        Threshold   = WearCriticalPct,
                    });
                    prev.ActiveConditions.Add("wear_critical");
                }
                prev.ActiveConditions.Remove("wear_warning");
            }
            else if (wearPercentUsed.Value >= WearWarningPct)
            {
                if (!prev.ActiveConditions.Contains("wear_warning"))
                {
                    alerts.Add(new SmartTrendAlert
                    {
                        Condition   = "wear_warning",
                        Severity    = "warning",
                        Message     = $"{driveName}: wear level warning ({wearPercentUsed:F1}% used)",
                        ActualValue = wearPercentUsed.Value,
                        Threshold   = WearWarningPct,
                    });
                    prev.ActiveConditions.Add("wear_warning");
                }
                prev.ActiveConditions.Remove("wear_critical");
            }
            else
            {
                prev.ActiveConditions.Remove("wear_warning");
                prev.ActiveConditions.Remove("wear_critical");
            }
        }

        // 3. Power-on-hours threshold
        if (powerOnHours.HasValue)
        {
            if (powerOnHours.Value >= PowerOnHoursCritical)
            {
                if (!prev.ActiveConditions.Contains("poh_critical"))
                {
                    alerts.Add(new SmartTrendAlert
                    {
                        Condition   = "poh_critical",
                        Severity    = "critical",
                        Message     = $"{driveName}: power-on hours critical ({powerOnHours:N0}h)",
                        ActualValue = powerOnHours.Value,
                        Threshold   = PowerOnHoursCritical,
                    });
                    prev.ActiveConditions.Add("poh_critical");
                }
                prev.ActiveConditions.Remove("poh_warning");
            }
            else if (powerOnHours.Value >= PowerOnHoursWarning)
            {
                if (!prev.ActiveConditions.Contains("poh_warning"))
                {
                    alerts.Add(new SmartTrendAlert
                    {
                        Condition   = "poh_warning",
                        Severity    = "warning",
                        Message     = $"{driveName}: power-on hours warning ({powerOnHours:N0}h)",
                        ActualValue = powerOnHours.Value,
                        Threshold   = PowerOnHoursWarning,
                    });
                    prev.ActiveConditions.Add("poh_warning");
                }
                prev.ActiveConditions.Remove("poh_critical");
            }
            else
            {
                prev.ActiveConditions.Remove("poh_warning");
                prev.ActiveConditions.Remove("poh_critical");
            }
        }

        // Update snapshot
        _snapshots[driveId] = new DriveSnapshot
        {
            ReallocatedSectors = reallocatedSectors,
            WearPercentUsed = wearPercentUsed,
            PowerOnHours = powerOnHours,
            ActiveConditions = prev.ActiveConditions,
        };

        foreach (var a in alerts)
            _logger.LogWarning("SMART trend alert: {Message}", a.Message);

        return alerts;
    }

    private sealed class DriveSnapshot
    {
        public long? ReallocatedSectors { get; set; }
        public double? WearPercentUsed { get; set; }
        public long? PowerOnHours { get; set; }
        public HashSet<string> ActiveConditions { get; set; } = [];
    }
}

public sealed class SmartTrendAlert
{
    public string Condition { get; set; } = "";
    public string Severity { get; set; } = "";
    public string Message { get; set; } = "";
    /// <summary>Measured value that triggered the alert (sector count, wear %, or hours).</summary>
    public double ActualValue { get; set; }
    /// <summary>Threshold that was crossed.</summary>
    public double Threshold { get; set; }
}
