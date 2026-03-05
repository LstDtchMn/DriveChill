using System.Text.RegularExpressions;
using DriveChill.Models;
using DriveChill.Services;
using Microsoft.AspNetCore.Mvc;

namespace DriveChill.Api;

[ApiController]
[Route("api/drives")]
public sealed partial class DrivesController : ControllerBase
{
    private static readonly Regex _driveIdRe = DriveIdRegex();
    private static readonly Regex _runIdRe   = RunIdRegex();

    [GeneratedRegex(@"^[a-f0-9]{24}$")]
    private static partial Regex DriveIdRegex();
    [GeneratedRegex(@"^[a-f0-9]{16}$")]
    private static partial Regex RunIdRegex();

    private readonly DriveMonitorService _monitor;
    private readonly DbService _db;
    private readonly ILogger<DrivesController> _log;

    public DrivesController(
        DriveMonitorService monitor,
        DbService db,
        ILogger<DrivesController> log)
    {
        _monitor = monitor;
        _db = db;
        _log = log;
    }

    private IActionResult BadDriveId() => BadRequest(new { detail = "Invalid drive_id" });
    private IActionResult BadRunId()   => BadRequest(new { detail = "Invalid run_id" });

    // ── GET /api/drives ───────────────────────────────────────────────────────

    [HttpGet("")]
    public IActionResult ListDrives()
    {
        var drives = _monitor.GetAllDrives();
        return Ok(new
        {
            drives = drives.Select(d => _monitor.ToSummary(d)),
            smartctl_available = _monitor.SmartctlAvailable,
            total = drives.Count,
        });
    }

    // ── POST /api/drives/rescan ───────────────────────────────────────────────

    [HttpPost("rescan")]
    public async Task<IActionResult> RescanDrives(CancellationToken ct)
    {
        var s = await _db.LoadDriveSettingsAsync(ct);
        var count = await _monitor.RescanNowAsync(s, ct);
        return Ok(new { drives_found = count });
    }

    // ── GET/PUT /api/drives/settings ─────────────────────────────────────────

    [HttpGet("settings")]
    public async Task<IActionResult> GetSettings(CancellationToken ct)
        => Ok(await _db.LoadDriveSettingsAsync(ct));

    [HttpPut("settings")]
    public async Task<IActionResult> UpdateSettings(
        [FromBody] System.Text.Json.JsonElement body, CancellationToken ct)
    {
        // Mirror Python: only write fields that are explicitly present in the payload
        // so a partial PUT from the frontend does not silently reset omitted settings.
        if (body.TryGetProperty("enabled", out var vEnabled))
            await _db.SetSettingAsync("drive_monitoring_enabled", vEnabled.GetBoolean() ? "1" : "0", ct);
        if (body.TryGetProperty("smartctl_provider_enabled", out var vSmartctl))
            await _db.SetSettingAsync("drive_smartctl_provider_enabled", vSmartctl.GetBoolean() ? "1" : "0", ct);
        if (body.TryGetProperty("native_provider_enabled", out var vNative))
            await _db.SetSettingAsync("drive_native_provider_enabled", vNative.GetBoolean() ? "1" : "0", ct);
        if (body.TryGetProperty("smartctl_path", out var vPath))
            await _db.SetSettingAsync("drive_smartctl_path", vPath.GetString() ?? "smartctl", ct);
        if (body.TryGetProperty("fast_poll_seconds", out var vFast))
            await _db.SetSettingAsync("drive_fast_poll_seconds", vFast.GetInt32().ToString(), ct);
        if (body.TryGetProperty("health_poll_seconds", out var vHealth))
            await _db.SetSettingAsync("drive_health_poll_seconds", vHealth.GetInt32().ToString(), ct);
        if (body.TryGetProperty("rescan_poll_seconds", out var vRescan))
            await _db.SetSettingAsync("drive_rescan_poll_seconds", vRescan.GetInt32().ToString(), ct);
        if (body.TryGetProperty("hdd_temp_warning_c", out var vHddW))
            await _db.SetSettingAsync("drive_hdd_temp_warning_c", vHddW.GetDouble().ToString(), ct);
        if (body.TryGetProperty("hdd_temp_critical_c", out var vHddC))
            await _db.SetSettingAsync("drive_hdd_temp_critical_c", vHddC.GetDouble().ToString(), ct);
        if (body.TryGetProperty("ssd_temp_warning_c", out var vSsdW))
            await _db.SetSettingAsync("drive_ssd_temp_warning_c", vSsdW.GetDouble().ToString(), ct);
        if (body.TryGetProperty("ssd_temp_critical_c", out var vSsdC))
            await _db.SetSettingAsync("drive_ssd_temp_critical_c", vSsdC.GetDouble().ToString(), ct);
        if (body.TryGetProperty("nvme_temp_warning_c", out var vNvmeW))
            await _db.SetSettingAsync("drive_nvme_temp_warning_c", vNvmeW.GetDouble().ToString(), ct);
        if (body.TryGetProperty("nvme_temp_critical_c", out var vNvmeC))
            await _db.SetSettingAsync("drive_nvme_temp_critical_c", vNvmeC.GetDouble().ToString(), ct);
        if (body.TryGetProperty("wear_warning_percent_used", out var vWearW))
            await _db.SetSettingAsync("drive_wear_warning_percent_used", vWearW.GetDouble().ToString(), ct);
        if (body.TryGetProperty("wear_critical_percent_used", out var vWearC))
            await _db.SetSettingAsync("drive_wear_critical_percent_used", vWearC.GetDouble().ToString(), ct);
        return Ok(await _db.LoadDriveSettingsAsync(ct));
    }

    // ── GET /api/drives/{id} ──────────────────────────────────────────────────

    [HttpGet("{driveId}")]
    public async Task<IActionResult> GetDrive(string driveId, CancellationToken ct)
    {
        if (!_driveIdRe.IsMatch(driveId)) return BadDriveId();
        var raw = _monitor.GetDrive(driveId);
        if (raw is null) return NotFound(new { detail = "Drive not found" });
        var runs = await _db.GetSelfTestRunsAsync(driveId, 1, ct);
        DriveSelfTestRun? lastRun = null;
        if (runs.Count > 0)
        {
            var r = runs[0];
            lastRun = new DriveSelfTestRun
            {
                Id = (string)r["id"]!, DriveId = (string)r["drive_id"]!,
                Type = (string)r["type"]!, Status = (string)r["status"]!,
                ProgressPercent = (double?)r["progress_percent"],
                StartedAt = (string)r["started_at"]!,
                FinishedAt = (string?)r["finished_at"],
                FailureMessage = (string?)r["failure_message"],
            };
        }
        return Ok(_monitor.ToDetail(raw, lastRun));
    }

    // ── GET /api/drives/{id}/attributes ──────────────────────────────────────

    [HttpGet("{driveId}/attributes")]
    public IActionResult GetAttributes(string driveId)
    {
        if (!_driveIdRe.IsMatch(driveId)) return BadDriveId();
        var raw = _monitor.GetDrive(driveId);
        if (raw is null) return NotFound(new { detail = "Drive not found" });
        return Ok(new { drive_id = driveId, attributes = raw.RawAttributes });
    }

    // ── GET /api/drives/{id}/history ──────────────────────────────────────────

    [HttpGet("{driveId}/history")]
    public async Task<IActionResult> GetHistory(
        string driveId, [FromQuery] double hours = 168.0, CancellationToken ct = default)
    {
        if (!_driveIdRe.IsMatch(driveId)) return BadDriveId();
        if (hours <= 0 || hours > 8760)
            return BadRequest(new { detail = "hours must be between 0 and 8760" });
        var retentionStr = await _db.GetSettingAsync("history_retention_hours", ct);
        var retention = double.TryParse(retentionStr, out var r) ? r : 168.0;
        var effectiveHours = Math.Min(hours, retention);
        var history = await _db.GetHealthHistoryAsync(driveId, effectiveHours, ct);
        return Ok(new
        {
            drive_id = driveId,
            history,
            retention_limited = effectiveHours < hours,
        });
    }

    // ── POST /api/drives/{id}/refresh ─────────────────────────────────────────

    [HttpPost("{driveId}/refresh")]
    public async Task<IActionResult> RefreshDrive(string driveId, CancellationToken ct)
    {
        if (!_driveIdRe.IsMatch(driveId)) return BadDriveId();
        var cached = _monitor.GetDrive(driveId);
        if (cached is null) return NotFound(new { detail = "Drive not found" });
        var s = await _db.LoadDriveSettingsAsync(ct);
        var refreshed = await _monitor.RefreshDriveAsync(driveId, s, ct);
        // Mirror Python: fall back to last cached drive when refresh fails.
        return Ok(_monitor.ToSummary(refreshed ?? cached));
    }

    // ── Self-test endpoints ───────────────────────────────────────────────────

    public sealed class StartTestRequest { public SelfTestType Type { get; set; } = SelfTestType.Short; }

    [HttpPost("{driveId}/self-tests")]
    public async Task<IActionResult> StartSelfTest(
        string driveId, [FromBody] StartTestRequest body, CancellationToken ct)
    {
        if (!_driveIdRe.IsMatch(driveId)) return BadDriveId();
        var raw = _monitor.GetDrive(driveId);
        if (raw is null) return NotFound(new { detail = "Drive not found" });
        if (!raw.Capabilities.SmartSelfTestShort)
            return BadRequest(new { detail = "Self-test not supported" });

        // Reject if a test is already running for this drive BEFORE launching smartctl
        var running = await _db.GetRunningSelfTestsAsync(ct);
        if (running.Any(r => (string?)r["drive_id"] == driveId))
            return Conflict(new { detail = "A self-test is already in progress for this drive" });

        var s = await _db.LoadDriveSettingsAsync(ct);
        var provRef = await _monitor.StartSelfTestAsync(driveId, body.Type, s, ct);
        if (provRef is null) return BadRequest(new { detail = "Self-test start failed" });

        var runId = await _db.CreateSelfTestRunAsync(driveId, body.Type.ToString().ToLower(), provRef, ct);
        var runs = await _db.GetSelfTestRunsAsync(driveId, 1, ct);
        var run = runs.FirstOrDefault();
        if (run is null) return StatusCode(500, new { detail = "Failed to retrieve created run" });
        return Ok(new DriveSelfTestRun
        {
            Id = (string)run["id"]!, DriveId = (string)run["drive_id"]!,
            Type = (string)run["type"]!, Status = (string)run["status"]!,
            ProgressPercent = run["progress_percent"] as double?,
            StartedAt = (string)run["started_at"]!,
            FinishedAt = run["finished_at"] as string,
            FailureMessage = run["failure_message"] as string,
        });
    }

    [HttpGet("{driveId}/self-tests")]
    public async Task<IActionResult> ListSelfTests(string driveId, CancellationToken ct)
    {
        if (!_driveIdRe.IsMatch(driveId)) return BadDriveId();
        var runs = await _db.GetSelfTestRunsAsync(driveId, 10, ct);
        return Ok(new { drive_id = driveId, runs });
    }

    [HttpPost("{driveId}/self-tests/{runId}/abort")]
    public async Task<IActionResult> AbortSelfTest(
        string driveId, string runId, CancellationToken ct)
    {
        if (!_driveIdRe.IsMatch(driveId)) return BadDriveId();
        if (!_runIdRe.IsMatch(runId)) return BadRunId();
        if (_monitor.GetDrive(driveId) is null) return NotFound(new { detail = "Drive not found" });

        // Verify the run belongs to this drive
        var run = await _db.GetSelfTestRunAsync(runId, ct);
        if (run is null || (string?)run["drive_id"] != driveId)
            return NotFound(new { detail = "Self-test run not found for this drive" });

        var s = await _db.LoadDriveSettingsAsync(ct);
        var aborted = await _monitor.AbortSelfTestAsync(driveId, s, ct);
        if (!aborted) return Conflict(new { detail = "Abort failed or not supported" });
        await _db.UpdateSelfTestRunAsync(runId, "aborted", ct: ct);
        return Ok(new { success = true });
    }

    // ── Per-drive settings ────────────────────────────────────────────────────

    [HttpGet("{driveId}/settings")]
    public async Task<IActionResult> GetDriveSettings(string driveId, CancellationToken ct)
    {
        if (!_driveIdRe.IsMatch(driveId)) return BadDriveId();
        var result = await _db.GetDriveSettingsOverrideAsync(driveId, ct);
        return Ok(result ?? new Dictionary<string, object?>
        {
            ["drive_id"]             = driveId,
            ["temp_warning_c"]       = null,
            ["temp_critical_c"]      = null,
            ["alerts_enabled"]       = null,
            ["curve_picker_enabled"] = null,
        });
    }

    [HttpPut("{driveId}/settings")]
    public async Task<IActionResult> UpdateDriveSettings(
        string driveId, [FromBody] DriveSettingsOverride body, CancellationToken ct)
    {
        if (!_driveIdRe.IsMatch(driveId)) return BadDriveId();
        if (_monitor.GetDrive(driveId) is null) return NotFound(new { detail = "Drive not found" });
        await _db.UpsertDriveSettingsOverrideAsync(driveId, body, ct);
        return Ok(await _db.GetDriveSettingsOverrideAsync(driveId, ct));
    }
}
