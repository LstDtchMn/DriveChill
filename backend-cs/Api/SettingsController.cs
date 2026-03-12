using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using DriveChill.Models;
using DriveChill.Services;

namespace DriveChill.Api;

[ApiController]
[Route("api/settings")]
public sealed class SettingsController : ControllerBase
{
    private readonly SettingsStore              _store;
    private readonly AppSettings                _appSettings;
    private readonly DbService                  _db;
    private readonly WebhookService             _webhooks;
    private readonly NotificationChannelService _notifChannels;
    private readonly AlertService               _alerts;

    public SettingsController(SettingsStore store, AppSettings appSettings, DbService db,
                              WebhookService webhooks, NotificationChannelService notifChannels,
                              AlertService alerts)
    {
        _store         = store;
        _appSettings   = appSettings;
        _db            = db;
        _webhooks      = webhooks;
        _notifChannels = notifChannels;
        _alerts        = alerts;
    }

    /// <summary>GET /api/settings — returns same field names as Python backend.</summary>
    [HttpGet]
    public IActionResult GetSettings() => Ok(new
    {
        sensor_poll_interval      = _store.PollIntervalMs / 1000.0,
        history_retention_hours   = _store.RetentionDays * 24,
        temp_unit                 = _store.TempUnit,
        hardware_backend          = "lhm",
        backend_name              = _appSettings.AppName,
        fan_ramp_rate_pct_per_sec = _store.FanRampRatePctPerSec,
        deadband                  = _store.Deadband,
    });

    /// <summary>PUT /api/settings — accepts same field names as Python backend.</summary>
    [HttpPut]
    public IActionResult UpdateSettings([FromBody] SettingsUpdateRequest req)
    {
        if (req.SensorPollInterval.HasValue)
        {
            var intervalSec = Math.Clamp(req.SensorPollInterval.Value, 0.5, 30.0);
            _store.PollIntervalMs = (int)(intervalSec * 1000);
        }
        if (req.HistoryRetentionHours.HasValue)
        {
            var retentionHours = Math.Clamp(req.HistoryRetentionHours.Value, 1, 8760);
            _store.RetentionDays = Math.Max(1, retentionHours / 24);
        }
        if (req.TempUnit is "C" or "F")
            _store.TempUnit = req.TempUnit;
        if (req.FanRampRatePctPerSec.HasValue)
            _store.FanRampRatePctPerSec = Math.Clamp(req.FanRampRatePctPerSec.Value, 0.1, 100.0);
        if (req.Deadband.HasValue)
            _store.Deadband = Math.Clamp(req.Deadband.Value, 0.0, 20.0);

        return Ok(new
        {
            success = true,
            settings = new
            {
                sensor_poll_interval      = _store.PollIntervalMs / 1000.0,
                history_retention_hours   = _store.RetentionDays * 24,
                temp_unit                 = _store.TempUnit,
                fan_ramp_rate_pct_per_sec = _store.FanRampRatePctPerSec,
                deadband                  = _store.Deadband,
            },
        });
    }

    /// <summary>GET /api/settings/info — app version and build info.</summary>
    [HttpGet("info")]
    public IActionResult GetInfo() => Ok(new
    {
        app_name    = _appSettings.AppName,
        version     = _appSettings.AppVersion,
        data_dir    = _appSettings.DataDir,
        platform    = "windows",
        runtime     = "dotnet",
    });

    // -----------------------------------------------------------------------
    // Config export / import
    // -----------------------------------------------------------------------

    /// <summary>GET /api/settings/export — export full config as portable JSON.</summary>
    [HttpGet("export")]
    public async Task<IActionResult> ExportConfig(CancellationToken ct = default)
    {
        // Profiles (from DB)
        var profiles = (await _db.ListProfilesAsync(ct)).Select(p => new
        {
            id        = p.Id,
            name      = p.Name,
            preset    = p.Description,
            is_active = p.IsActive,
            curves    = p.Curves,
        }).ToList();

        // Alert rules (from DB)
        var alertRules = await _db.ListAlertRulesAsync(ct);

        // Temperature targets (from DB)
        var tempTargets = await _db.ListTemperatureTargetsAsync(ct);

        // Quiet hours (from DB)
        var quietHours = await _db.GetQuietHoursAsync(ct);

        // Webhook config (from SettingsStore via WebhookService)
        var webhookCfg = _webhooks.GetConfig();

        // Notification channels (from DB — config_json preserved as-is)
        var notifChannels = await _notifChannels.ListAsync(ct);

        // Sensor labels (from DB)
        var sensorLabels = await _db.GetAllLabelsAsync(ct);

        // Settings
        var settingsData = new
        {
            sensor_poll_interval      = _store.PollIntervalMs / 1000.0,
            history_retention_hours   = _store.RetentionDays * 24,
            temp_unit                 = _store.TempUnit,
            fan_ramp_rate_pct_per_sec = _store.FanRampRatePctPerSec,
        };

        return Ok(new
        {
            export_version = 1,
            exported_at    = DateTimeOffset.UtcNow.ToString("o"),
            profiles,
            alert_rules         = alertRules,
            temperature_targets = tempTargets,
            quiet_hours = quietHours.Select(qh => new
            {
                id          = qh.Id,
                day_of_week = qh.DayOfWeek,
                start_time  = qh.StartTime,
                end_time    = qh.EndTime,
                profile_id  = qh.ProfileId,
                enabled     = qh.Enabled,
            }),
            webhook_config        = webhookCfg,
            notification_channels = notifChannels,
            sensor_labels         = sensorLabels,
            settings              = settingsData,
        });
    }

    private static readonly JsonSerializerOptions _importJsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>POST /api/settings/import — import config from exported JSON.</summary>
    [HttpPost("import")]
    public async Task<IActionResult> ImportConfig([FromBody] JsonElement body, CancellationToken ct = default)
    {
        if (!body.TryGetProperty("export_version", out var ver) ||
            ver.TryGetInt32(out var verInt) == false || verInt != 1)
        {
            return UnprocessableEntity(new { detail = "Unsupported export_version (expected 1)" });
        }

        var imported = new Dictionary<string, int>();

        // --- Profiles ---
        if (body.TryGetProperty("profiles", out var profilesNode) &&
            profilesNode.ValueKind == JsonValueKind.Array)
        {
            int count = 0;
            foreach (var pNode in profilesNode.EnumerateArray())
            {
                var id   = pNode.TryGetProperty("id", out var idN)     ? idN.GetString()     ?? Guid.NewGuid().ToString() : Guid.NewGuid().ToString();
                var name = pNode.TryGetProperty("name", out var nameN) ? nameN.GetString()   ?? "Imported"                : "Imported";
                var desc = pNode.TryGetProperty("preset", out var preN)? preN.GetString()    ?? ""                        : "";
                var isAct = pNode.TryGetProperty("is_active", out var actN) && actN.ValueKind == JsonValueKind.True;

                var curves = new List<FanCurve>();
                if (pNode.TryGetProperty("curves", out var curvesNode) &&
                    curvesNode.ValueKind == JsonValueKind.Array)
                {
                    foreach (var cNode in curvesNode.EnumerateArray())
                    {
                        var curve = JsonSerializer.Deserialize<FanCurve>(cNode.GetRawText(), _importJsonOpts);
                        if (curve != null) curves.Add(curve);
                    }
                }

                var existing = await _db.GetProfileAsync(id, ct);
                if (existing != null)
                {
                    existing.Name        = name;
                    existing.Description = desc;
                    existing.IsActive    = isAct;
                    existing.Curves      = curves;
                    existing.UpdatedAt   = DateTimeOffset.UtcNow;
                    await _db.UpdateProfileAsync(existing, ct);
                }
                else
                {
                    await _db.CreateProfileAsync(new Profile
                    {
                        Id          = id,
                        Name        = name,
                        Description = desc,
                        IsActive    = isAct,
                        Curves      = curves,
                    }, ct);
                }
                count++;
            }
            imported["profiles"] = count;
        }

        // --- Alert rules ---
        if (body.TryGetProperty("alert_rules", out var alertsNode) &&
            alertsNode.ValueKind == JsonValueKind.Array)
        {
            var rules = new List<AlertRule>();
            foreach (var rNode in alertsNode.EnumerateArray())
            {
                var rule = JsonSerializer.Deserialize<AlertRule>(rNode.GetRawText(), _importJsonOpts);
                if (rule != null) rules.Add(rule);
            }
            await _db.SaveAlertRulesAsync(rules, ct);
            await _alerts.ReloadRulesAsync(ct);
            imported["alert_rules"] = rules.Count;
        }

        // --- Temperature targets ---
        if (body.TryGetProperty("temperature_targets", out var targetsNode) &&
            targetsNode.ValueKind == JsonValueKind.Array)
        {
            // Clear existing
            var existingTargets = await _db.ListTemperatureTargetsAsync(ct);
            foreach (var t in existingTargets)
                await _db.DeleteTemperatureTargetAsync(t.Id, ct);

            int count = 0;
            foreach (var tNode in targetsNode.EnumerateArray())
            {
                var target = JsonSerializer.Deserialize<TemperatureTarget>(tNode.GetRawText(), _importJsonOpts);
                if (target != null)
                {
                    if (string.IsNullOrEmpty(target.Id))
                        target.Id = Guid.NewGuid().ToString();
                    // Clamp to match TemperatureTargetCreateRequest [Range] annotations
                    target.TargetTempC = Math.Clamp(target.TargetTempC, 20.0, 85.0);
                    target.ToleranceC  = Math.Clamp(target.ToleranceC,  1.0, 20.0);
                    target.MinFanSpeed = Math.Clamp(target.MinFanSpeed, 0.0, 100.0);
                    target.PidKp       = Math.Clamp(target.PidKp,       0.0, 100.0);
                    target.PidKi       = Math.Clamp(target.PidKi,       0.0, 10.0);
                    target.PidKd       = Math.Clamp(target.PidKd,       0.0, 100.0);
                    await _db.CreateTemperatureTargetAsync(target, ct);
                    count++;
                }
            }
            imported["temperature_targets"] = count;
        }

        // --- Quiet hours ---
        if (body.TryGetProperty("quiet_hours", out var qhNode) &&
            qhNode.ValueKind == JsonValueKind.Array)
        {
            // Clear existing
            var existingQh = await _db.GetQuietHoursAsync(ct);
            foreach (var qh in existingQh)
                await _db.DeleteQuietHoursAsync(qh.Id, ct);

            int count = 0;
            foreach (var qNode in qhNode.EnumerateArray())
            {
                var rule = new QuietHoursRule
                {
                    DayOfWeek = qNode.TryGetProperty("day_of_week", out var dow) ? dow.GetInt32()         : 0,
                    StartTime = qNode.TryGetProperty("start_time", out var st)   ? st.GetString() ?? "00:00" : "00:00",
                    EndTime   = qNode.TryGetProperty("end_time", out var et)     ? et.GetString() ?? "00:00" : "00:00",
                    ProfileId = qNode.TryGetProperty("profile_id", out var pid)  ? pid.GetString() ?? ""     : "",
                    Enabled   = !qNode.TryGetProperty("enabled", out var en) || en.ValueKind != JsonValueKind.False,
                };
                await _db.CreateQuietHoursAsync(rule, ct);
                count++;
            }
            imported["quiet_hours"] = count;
        }

        // --- Webhook config (never import signing secret) ---
        if (body.TryGetProperty("webhook_config", out var whNode) &&
            whNode.ValueKind == JsonValueKind.Object)
        {
            var current = _webhooks.GetConfigRaw();
            var cfg = new WebhookConfig
            {
                Enabled             = whNode.TryGetProperty("enabled", out var whEn) && whEn.ValueKind == JsonValueKind.True,
                TargetUrl           = whNode.TryGetProperty("target_url", out var whUrl) ? whUrl.GetString() ?? "" : "",
                SigningSecret       = current.SigningSecret, // never import signing secrets
                TimeoutSeconds      = whNode.TryGetProperty("timeout_seconds", out var whTs) && whTs.TryGetDouble(out var tsv) ? tsv : 3.0,
                MaxRetries          = whNode.TryGetProperty("max_retries", out var whMr) && whMr.TryGetInt32(out var mrv) ? mrv : 2,
                RetryBackoffSeconds = whNode.TryGetProperty("retry_backoff_seconds", out var whRb) && whRb.TryGetDouble(out var rbv) ? rbv : 1.0,
            };
            try { await _webhooks.UpdateConfigAsync(cfg); } catch { /* skip invalid config */ }
            imported["webhook_config"] = 1;
        }

        // --- Notification channels ---
        if (body.TryGetProperty("notification_channels", out var ncNode) &&
            ncNode.ValueKind == JsonValueKind.Array)
        {
            // Clear existing channels before replacing
            var existing = await _notifChannels.ListAsync(ct);
            foreach (var ch in existing)
                await _notifChannels.DeleteAsync(ch.Id, ct);

            int count = 0;
            string[] urlConfigKeys = ["url", "webhook_url"];
            foreach (var ncItem in ncNode.EnumerateArray())
            {
                var ch = JsonSerializer.Deserialize<NotificationChannel>(ncItem.GetRawText(), _importJsonOpts);
                if (ch is null || string.IsNullOrEmpty(ch.Id)) continue;
                // Apply the same SSRF validation that the create/update routes enforce so
                // import cannot persist URLs that the normal API would reject.
                bool ssrfBlocked = false;
                if (ch.Config is not null)
                {
                    foreach (var key in urlConfigKeys)
                    {
                        if (ch.Config.TryGetValue(key, out var el) &&
                            el.ValueKind == System.Text.Json.JsonValueKind.String)
                        {
                            var val = el.GetString() ?? "";
                            if (!string.IsNullOrEmpty(val))
                            {
                                var (urlValid, _) = await DriveChill.Utils.UrlSecurity.TryValidateOutboundHttpUrlAsync(
                                    val, allowPrivateTargets: false);
                                if (!urlValid)
                                {
                                    ssrfBlocked = true;
                                    break;
                                }
                            }
                        }
                    }
                }
                if (ssrfBlocked) continue;
                await _notifChannels.CreateAsync(ch.Id, ch.Type, ch.Name, ch.Enabled, ch.Config ?? new Dictionary<string, JsonElement>(), ct);
                count++;
            }
            imported["notification_channels"] = count;
        }

        // --- Sensor labels ---
        if (body.TryGetProperty("sensor_labels", out var labelsNode) &&
            labelsNode.ValueKind == JsonValueKind.Object)
        {
            int count = 0;
            foreach (var prop in labelsNode.EnumerateObject())
            {
                var label = prop.Value.GetString();
                if (!string.IsNullOrEmpty(label))
                {
                    await _db.SetLabelAsync(prop.Name, label, ct);
                    count++;
                }
            }
            imported["sensor_labels"] = count;
        }

        // --- Settings ---
        if (body.TryGetProperty("settings", out var sEl) &&
            sEl.ValueKind == JsonValueKind.Object)
        {
            int count = 0;
            if (sEl.TryGetProperty("sensor_poll_interval", out var spi) && spi.TryGetDouble(out var spiv))
            { _store.PollIntervalMs = (int)(Math.Clamp(spiv, 0.5, 30.0) * 1000); count++; }
            if (sEl.TryGetProperty("history_retention_hours", out var hrh) && hrh.TryGetInt32(out var hrhv))
            { _store.RetentionDays = Math.Max(1, Math.Clamp(hrhv, 1, 8760) / 24); count++; }
            if (sEl.TryGetProperty("temp_unit", out var tu))
            {
                var unit = tu.GetString();
                if (unit is "C" or "F") { _store.TempUnit = unit; count++; }
            }
            if (sEl.TryGetProperty("fan_ramp_rate_pct_per_sec", out var frr) && frr.TryGetDouble(out var frrv))
            { _store.FanRampRatePctPerSec = Math.Clamp(frrv, 0.1, 100.0); count++; }
            imported["settings"] = count;
        }

        return Ok(new { success = true, imported });
    }
}

/// <summary>Request body for PUT /api/settings — uses same field names as Python backend.</summary>
public sealed class SettingsUpdateRequest
{
    public double? SensorPollInterval    { get; set; }
    public int?    HistoryRetentionHours { get; set; }
    public string  TempUnit              { get; set; } = "C";
    public double? FanRampRatePctPerSec  { get; set; }
    public double? Deadband              { get; set; }
}
