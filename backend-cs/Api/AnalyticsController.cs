using Microsoft.AspNetCore.Mvc;
using DriveChill.Services;

namespace DriveChill.Api;

[ApiController]
[Route("api/analytics")]
public sealed class AnalyticsController : ControllerBase
{
    private readonly DbService     _db;
    private readonly SettingsStore _store;

    public AnalyticsController(DbService db, SettingsStore store)
    {
        _db    = db;
        _store = store;
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    internal static (DateTimeOffset Start, DateTimeOffset End) ResolveRange(
        double? hours, string? startStr, string? endStr)
    {
        var now = DateTimeOffset.UtcNow;
        // Custom range requires both start and end; one-sided falls back to hours.
        if (startStr != null && endStr != null
            && DateTimeOffset.TryParse(startStr, out var parsedStart)
            && DateTimeOffset.TryParse(endStr,   out var parsedEnd))
        {
            var start = parsedStart.ToUniversalTime();
            var end   = parsedEnd.ToUniversalTime();
            if (start < end)
                return (start, end);
        }
        var h = Math.Clamp(hours ?? 24.0, 0.1, 8760.0);
        return (now.AddHours(-h), now);
    }

    // Parse sensor_id + sensor_ids into a deduplicated array.
    // Returns null when no IDs are specified (= all sensors).
    private static string[]? ParseSensorIds(string? sensorId, string? sensorIds)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(sensorId)) ids.Add(sensorId);
        if (!string.IsNullOrEmpty(sensorIds))
            foreach (var s in sensorIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                ids.Add(s);
        return ids.Count > 0 ? ids.ToArray() : null;
    }

    private static int AutoBucketSeconds(DateTimeOffset start, DateTimeOffset end, int? requested)
    {
        if (requested.HasValue) return Math.Clamp(requested.Value, 10, 86400);
        var span = end - start;
        return span.TotalHours <= 1  ? 30
             : span.TotalHours <= 24 ? 300    // 5 min
             : span.TotalDays  <= 7  ? 1800   // 30 min
             :                         7200;  // 2 h
    }

    private static object RangeMeta(DateTimeOffset start, DateTimeOffset end)
        => new { start = start.ToString("o"), end = end.ToString("o") };

    // -----------------------------------------------------------------------
    // Endpoints
    // -----------------------------------------------------------------------

    [HttpGet("history")]
    public async Task<IActionResult> GetHistory(
        [FromQuery] double?  hours         = null,
        [FromQuery] string?  start         = null,
        [FromQuery] string?  end           = null,
        [FromQuery(Name = "sensor_id")]      string? sensorId      = null,
        [FromQuery(Name = "sensor_ids")]     string? sensorIds     = null,
        [FromQuery(Name = "bucket_seconds")] int?    bucketSeconds = null,
        CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(sensorId) && !string.IsNullOrEmpty(sensorIds))
            return BadRequest(new { detail = "Use sensor_id or sensor_ids, not both" });

        var (startDt, endDt) = ResolveRange(hours ?? 24, start, end);
        var ids    = ParseSensorIds(sensorId, sensorIds);
        var bucket = AutoBucketSeconds(startDt, endDt, bucketSeconds);

        // Clamp to retention window
        var earliestAvail  = DateTimeOffset.UtcNow.AddHours(-_store.RetentionDays * 24.0);
        var effectiveStart = startDt < earliestAvail ? earliestAvail : startDt;
        var retentionLimited = effectiveStart > startDt;

        var bucketList = await _db.GetAnalyticsHistoryAsync(effectiveStart, endDt, ids, bucket, ct);

        // Build series dict: sensor_id → [ { timestamp, avg, min, max, count } ]
        var series = new Dictionary<string, List<object>>();
        foreach (var b in bucketList)
        {
            if (!series.TryGetValue(b.SensorId, out var pts))
                series[b.SensorId] = pts = [];
            pts.Add(new { timestamp = b.TimestampUtc, avg = b.AvgValue, min = b.MinValue, max = b.MaxValue, count = b.SampleCount });
        }

        // Actual returned range from data timestamps
        var returnedStart = effectiveStart;
        var returnedEnd   = endDt;
        if (bucketList.Count > 0)
        {
            var timestamps = bucketList.Select(b => b.TimestampUtc).ToList();
            if (DateTimeOffset.TryParse(timestamps.Min(), out var ts1)) returnedStart = ts1;
            if (DateTimeOffset.TryParse(timestamps.Max(), out var ts2)) returnedEnd   = ts2;
        }

        return Ok(new
        {
            buckets           = bucketList,
            series,
            bucket_seconds    = bucket,
            requested_range   = RangeMeta(startDt, endDt),
            returned_range    = RangeMeta(returnedStart, returnedEnd),
            retention_limited = retentionLimited,
        });
    }

    [HttpGet("stats")]
    public async Task<IActionResult> GetStats(
        [FromQuery] double?  hours     = null,
        [FromQuery] string?  start     = null,
        [FromQuery] string?  end       = null,
        [FromQuery(Name = "sensor_id")]  string? sensorId  = null,
        [FromQuery(Name = "sensor_ids")] string? sensorIds = null,
        CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(sensorId) && !string.IsNullOrEmpty(sensorIds))
            return BadRequest(new { detail = "Use sensor_id or sensor_ids, not both" });

        var (startDt, endDt) = ResolveRange(hours ?? 24, start, end);
        var ids = ParseSensorIds(sensorId, sensorIds);
        var (stats, actualStart, actualEnd) = await _db.GetAnalyticsStatsAsync(startDt, endDt, ids, ct);
        var retStart = actualStart ?? startDt;
        var retEnd   = actualEnd   ?? endDt;
        return Ok(new
        {
            stats,
            requested_range = RangeMeta(startDt, endDt),
            returned_range  = RangeMeta(retStart, retEnd),
        });
    }

    [HttpGet("anomalies")]
    public async Task<IActionResult> GetAnomalies(
        [FromQuery] double?  hours             = null,
        [FromQuery] string?  start             = null,
        [FromQuery] string?  end               = null,
        [FromQuery(Name = "sensor_id")]          string? sensorId          = null,
        [FromQuery(Name = "sensor_ids")]         string? sensorIds         = null,
        [FromQuery(Name = "z_score_threshold")]  double  zScoreThreshold   = 3.0,
        CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(sensorId) && !string.IsNullOrEmpty(sensorIds))
            return BadRequest(new { detail = "Use sensor_id or sensor_ids, not both" });

        var (startDt, endDt) = ResolveRange(hours ?? 24, start, end);
        var ids = ParseSensorIds(sensorId, sensorIds);
        zScoreThreshold = Math.Clamp(zScoreThreshold, 1.0, 10.0);
        var (anomalies, actualStart, actualEnd) = await _db.GetAnalyticsAnomaliesAsync(startDt, endDt, ids, zScoreThreshold, ct);
        var retStart = actualStart ?? startDt;
        var retEnd   = actualEnd   ?? endDt;
        return Ok(new
        {
            anomalies,
            z_score_threshold = zScoreThreshold,
            requested_range   = RangeMeta(startDt, endDt),
            returned_range    = RangeMeta(retStart, retEnd),
        });
    }

    [HttpGet("regression")]
    public async Task<IActionResult> GetRegression(
        [FromQuery(Name = "baseline_days")]   int    baselineDays   = 30,
        [FromQuery(Name = "recent_hours")]    double recentHours    = 24.0,
        [FromQuery(Name = "threshold_delta")] double thresholdDelta = 5.0,
        [FromQuery(Name = "sensor_id")]       string? sensorId      = null,
        [FromQuery(Name = "sensor_ids")]      string? sensorIds     = null,
        [FromQuery] string? start = null,
        [FromQuery] string? end   = null,
        CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(sensorId) && !string.IsNullOrEmpty(sensorIds))
            return BadRequest(new { detail = "Use sensor_id or sensor_ids, not both" });

        baselineDays   = Math.Clamp(baselineDays,   7,   90);
        recentHours    = Math.Clamp(recentHours,    1.0, 168.0);
        thresholdDelta = Math.Clamp(thresholdDelta, 1.0, 50.0);
        var ids = ParseSensorIds(sensorId, sensorIds);

        // When start/end are provided, use them to define the recent window
        DateTimeOffset? recentSinceOverride = null;
        DateTimeOffset? recentUntilOverride = null;
        DateTimeOffset? baselineSinceOverride = null;
        if (!string.IsNullOrEmpty(start) && !string.IsNullOrEmpty(end))
        {
            var (startDt, endDt) = ResolveRange(null, start, end);
            recentSinceOverride   = startDt;
            recentUntilOverride   = endDt;
            baselineSinceOverride = startDt.AddDays(-baselineDays);
        }

        var (regressions, loadBandAware) = await _db.GetAnalyticsRegressionAsync(
            baselineDays, recentHours, thresholdDelta, ids,
            recentSinceOverride, baselineSinceOverride, recentUntilOverride, ct);
        return Ok(new
        {
            regressions,
            baseline_period_days = baselineDays,
            recent_period_hours  = recentHours,
            threshold_delta      = thresholdDelta,
            load_band_aware      = loadBandAware,
        });
    }

    [HttpGet("correlation")]
    public async Task<IActionResult> GetCorrelation(
        [FromQuery(Name = "x_sensor_id")] string? xSensorId = null,   // preferred
        [FromQuery(Name = "y_sensor_id")] string? ySensorId = null,   // preferred
        [FromQuery(Name = "sensor_x")]    string? sensorX   = null,   // legacy alias
        [FromQuery(Name = "sensor_y")]    string? sensorY   = null,   // legacy alias
        [FromQuery] double? hours = null,
        [FromQuery] string? start = null,
        [FromQuery] string? end   = null,
        CancellationToken ct = default)
    {
        var sx = xSensorId ?? sensorX;
        var sy = ySensorId ?? sensorY;
        if (string.IsNullOrEmpty(sx) || string.IsNullOrEmpty(sy))
            return BadRequest(new { detail = "x_sensor_id and y_sensor_id are required" });

        var (startDt, endDt) = ResolveRange(hours ?? 24, start, end);

        var (coeff, sampleCount, samples) =
            await _db.GetAnalyticsCorrelationAsync(sx, sy, startDt, endDt, ct);

        return Ok(new
        {
            x_sensor_id             = sx,
            y_sensor_id             = sy,
            correlation_coefficient = coeff,
            sample_count            = sampleCount,
            samples,
            requested_range         = RangeMeta(startDt, endDt),
        });
    }

    [HttpGet("report")]
    public async Task<IActionResult> GetReport(
        [FromQuery] double?  hours     = null,
        [FromQuery] string?  start     = null,
        [FromQuery] string?  end       = null,
        [FromQuery(Name = "sensor_id")]  string? sensorId  = null,
        [FromQuery(Name = "sensor_ids")] string? sensorIds = null,
        CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(sensorId) && !string.IsNullOrEmpty(sensorIds))
            return BadRequest(new { detail = "Use sensor_id or sensor_ids, not both" });

        var (startDt, endDt) = ResolveRange(hours ?? 24, start, end);
        var windowHours = (endDt - startDt).TotalHours;
        var recentHours = Math.Clamp(windowHours, 1.0, 168.0);
        var ids         = ParseSensorIds(sensorId, sensorIds);

        var (stats, actualStart, actualEnd) = await _db.GetAnalyticsStatsAsync(startDt, endDt, ids, ct);
        var (anomalies, _, _)              = await _db.GetAnalyticsAnomaliesAsync(startDt, endDt, ids, 3.0, ct);
        var (regressions, _)               = await _db.GetAnalyticsRegressionAsync(30, recentHours, 5.0, ids, ct: ct);

        var retStart = actualStart ?? startDt;
        var retEnd   = actualEnd   ?? endDt;

        var topAnomalous = anomalies
            .GroupBy(a => a.SensorId)
            .Select(g => new { sensor_id = g.Key, sensor_name = g.First().SensorName, count = g.Count() })
            .OrderByDescending(x => x.count)
            .Take(3)
            .ToList();

        return Ok(new
        {
            generated_at          = DateTimeOffset.UtcNow.ToString("o"),
            window_hours          = windowHours,
            requested_range       = RangeMeta(startDt, endDt),
            returned_range        = RangeMeta(retStart, retEnd),
            stats,
            anomalies,
            top_anomalous_sensors = topAnomalous,
            regressions,
        });
    }
}
