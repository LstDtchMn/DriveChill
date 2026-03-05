using DriveChill.Models;

namespace DriveChill.Services;

/// <summary>
/// Normalizes raw drive data into health status and percentage scores.
/// Mirrors the logic in Python's DriveHealthNormalizer.
/// </summary>
public sealed class DriveHealthNormalizer
{
    private readonly DriveSettings _s;

    public DriveHealthNormalizer(DriveSettings settings)
    {
        _s = settings;
    }

    private (double Warning, double Critical) TempThresholds(string mediaType) =>
        mediaType switch
        {
            "nvme" => (_s.NvmeTempWarningC, _s.NvmeTempCriticalC),
            "ssd"  => (_s.SsdTempWarningC,  _s.SsdTempCriticalC),
            _      => (_s.HddTempWarningC,   _s.HddTempCriticalC),
        };

    public string HealthStatus(DriveRawData raw)
    {
        if (raw.SmartOverallHealth == "FAILED" || raw.PredictedFailure)
            return "critical";

        if ((raw.UncorrectableErrors ?? 0) > 0) return "critical";
        if ((raw.MediaErrors ?? 0) > 0) return "critical";

        if (raw.WearPercentUsed.HasValue)
        {
            if (raw.WearPercentUsed.Value >= _s.WearCriticalPercentUsed) return "critical";
            if (raw.WearPercentUsed.Value >= _s.WearWarningPercentUsed)  return "warning";
        }

        if (raw.AvailableSparePercent.HasValue && raw.AvailableSparePercent.Value < 10.0)
            return "critical";

        if ((raw.ReallocatedSectors ?? 0) > 0) return "warning";
        if ((raw.PendingSectors ?? 0) > 0)     return "warning";

        var (warnC, critC) = TempThresholds(raw.MediaType);
        if (raw.TemperatureC.HasValue)
        {
            if (raw.TemperatureC.Value >= critC) return "critical";
            if (raw.TemperatureC.Value >= warnC) return "warning";
        }

        bool hasSmartData = raw.Capabilities.SmartRead ||
                            raw.Capabilities.HealthSource != "none";
        return hasSmartData ? "healthy" : "unknown";
    }

    public double? HealthPercent(DriveRawData raw)
    {
        bool hasData = raw.Capabilities.SmartRead ||
                       raw.Capabilities.HealthSource != "none" ||
                       raw.WearPercentUsed.HasValue;
        if (!hasData) return null;

        double score = 100.0;

        if (raw.WearPercentUsed.HasValue)
            score = Math.Min(score, Math.Max(0.0, 100.0 - raw.WearPercentUsed.Value));

        if (raw.SmartOverallHealth == "FAILED") score = Math.Min(score, 0.0);
        else if (raw.PredictedFailure)          score = Math.Min(score, 5.0);

        if ((raw.UncorrectableErrors ?? 0) > 0) score = Math.Min(score, 10.0);
        if ((raw.MediaErrors ?? 0) > 0)         score = Math.Min(score, 20.0);
        if ((raw.ReallocatedSectors ?? 0) > 0)  score = Math.Min(score, 60.0);
        if ((raw.PendingSectors ?? 0) > 0)      score = Math.Min(score, 70.0);

        return Math.Round(score, 1);
    }

    public double TempWarningC(DriveRawData raw) => TempThresholds(raw.MediaType).Warning;
    public double TempCriticalC(DriveRawData raw) => TempThresholds(raw.MediaType).Critical;
}
