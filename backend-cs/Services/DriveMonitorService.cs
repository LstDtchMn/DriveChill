using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DriveChill.Models;
using Microsoft.Data.Sqlite;

namespace DriveChill.Services;

/// <summary>
/// Discovers and monitors local drives using smartctl.
/// Publishes hdd_temp sensor readings into SensorService.
/// Polling schedule mirrors the Python backend.
/// </summary>
public sealed partial class DriveMonitorService : IDisposable
{
    private static readonly Regex _windowsDeviceRe = WindowsDeviceRegex();
    private static readonly Regex _linuxDeviceRe = LinuxDeviceRegex();

    [GeneratedRegex(@"^(\\\\\.\\PhysicalDrive\d+|/dev/sd[a-z]+|/dev/nvme\d+n?\d*)$")]
    private static partial Regex WindowsDeviceRegex();

    [GeneratedRegex(@"^(/dev/sd[a-z]+|/dev/hd[a-z]+|/dev/nvme\d+n\d+|/dev/disk/by-id/[A-Za-z0-9._:-]+)$")]
    private static partial Regex LinuxDeviceRegex();

    private readonly AppSettings _settings;
    private readonly ILogger<DriveMonitorService> _log;
    private readonly SensorService? _sensorService;
    private DriveHealthNormalizer? _normalizer;

    private readonly Dictionary<string, DriveRawData> _drives = new();
    private readonly object _drivesLock = new();

    private CancellationTokenSource? _cts;
    private Task? _tempTask;
    private Task? _healthTask;
    private Task? _rescanTask;

    private bool _smartctlAvailable;
    private string _connStr = "";

    public DriveMonitorService(
        AppSettings settings,
        ILogger<DriveMonitorService> log,
        SensorService sensorService)
    {
        _settings = settings;
        _log = log;
        _sensorService = sensorService;
    }

    // ── Public accessors ──────────────────────────────────────────────────────

    public IReadOnlyList<DriveRawData> GetAllDrives()
    {
        lock (_drivesLock) return [.. _drives.Values];
    }

    public DriveRawData? GetDrive(string driveId)
    {
        lock (_drivesLock) return _drives.GetValueOrDefault(driveId);
    }

    public bool SmartctlAvailable => _smartctlAvailable;

    // ── Startup / shutdown ────────────────────────────────────────────────────

    public async Task StartAsync(DriveSettings s, CancellationToken ct = default)
    {
        if (!s.Enabled) return;

        _connStr = $"Data Source={_settings.DbPath}";
        _normalizer = new DriveHealthNormalizer(s);
        _smartctlAvailable = await CheckSmartctlAsync(s.SmartctlPath, ct);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;

        // Initial scan (blocking — must complete before loops start)
        try { await RescanAsync(s, token); }
        catch (Exception ex) { _log.LogWarning(ex, "Drive initial scan failed"); }

        _tempTask    = RunLoop(() => PollTempsAsync(s, token), s.FastPollSeconds,   token);
        _healthTask  = RunLoop(() => PollHealthAsync(s, token), s.HealthPollSeconds, token);
        _rescanTask  = RunLoop(() => RescanAsync(s, token), s.RescanPollSeconds,   token);
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        var tasks = new[] { _tempTask, _healthTask, _rescanTask }
            .Where(t => t is not null).Cast<Task>();
        try { await Task.WhenAll(tasks); }
        catch (OperationCanceledException) { }
        catch (AggregateException aex) when (aex.InnerExceptions.All(e => e is OperationCanceledException)) { }
    }

    public void Dispose() { _cts?.Dispose(); }

    // ── Polling loops ─────────────────────────────────────────────────────────

    private async Task RunLoop(Func<Task> work, int intervalSeconds, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await work(); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _log.LogWarning(ex, "Drive monitor loop iteration failed"); }
            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), ct);
        }
    }

    // ── Device path validation ────────────────────────────────────────────────

    private static bool ValidateDevicePath(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return _windowsDeviceRe.IsMatch(path);
        return _linuxDeviceRe.IsMatch(path);
    }

    // ── smartctl execution ────────────────────────────────────────────────────

    private static async Task<bool> CheckSmartctlAsync(string smartctlPath, CancellationToken ct)
    {
        try
        {
            using var proc = new Process();
            proc.StartInfo = new ProcessStartInfo
            {
                FileName = smartctlPath,
                Arguments = "--version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            proc.Start();
            await proc.WaitForExitAsync(ct);
            return proc.ExitCode == 0;
        }
        catch { return false; }
    }

    private async Task<JsonDocument?> RunSmartctlAsync(
        string smartctlPath, string[] args, int timeoutSeconds, CancellationToken ct)
    {
        using var proc = new Process();
        proc.StartInfo = new ProcessStartInfo
        {
            FileName = smartctlPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
            proc.StartInfo.ArgumentList.Add(arg);

        try
        {
            proc.Start();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            var stdout = await proc.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderr = await proc.StandardError.ReadToEndAsync(timeoutCts.Token);
            await proc.WaitForExitAsync(timeoutCts.Token);

            if (!string.IsNullOrWhiteSpace(stderr))
                _log.LogDebug("smartctl stderr: {Stderr}", stderr[..Math.Min(500, stderr.Length)]);

            if (string.IsNullOrWhiteSpace(stdout)) return null;
            return JsonDocument.Parse(stdout);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(); } catch { }
            return null;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "smartctl execution failed");
            return null;
        }
    }

    // ── Drive parsing ─────────────────────────────────────────────────────────

    private static string DriveId(string serial, string model, string busType, string devicePath)
    {
        var key = string.IsNullOrEmpty(serial)
            ? $"noserial|{model}|{devicePath}"
            : $"{serial}|{model}|{busType}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hash)[..24].ToLowerInvariant();
    }

    private static string MaskSerial(string serial) =>
        string.IsNullOrEmpty(serial) ? "****" : "****" + serial[^Math.Min(4, serial.Length)..];

    private static string MaskDevicePath(string path)
    {
        var idx = path.LastIndexOf('/');
        return idx >= 0 ? path[(idx + 1)..] : path;
    }

    private DriveRawData? ParseDrive(JsonElement root, string devicePath)
    {
        try
        {
            var serial   = root.TryGetProperty("serial_number", out var sn) ? sn.GetString() ?? "" : "";
            var model    = root.TryGetProperty("model_name",    out var mn) ? mn.GetString() ?? "" : "";
            var firmware = root.TryGetProperty("firmware_version", out var fv) ? fv.GetString() ?? "" : "";

            var busType = DetectBusType(root);
            var mediaType = DetectMediaType(root, busType);
            var driveId = DriveId(serial, model, busType, devicePath);

            long capacity = 0;
            if (root.TryGetProperty("user_capacity", out var uc) && uc.TryGetProperty("bytes", out var ucb))
                capacity = ucb.GetInt64();

            double? temp = null;
            if (root.TryGetProperty("temperature", out var tempObj) &&
                tempObj.TryGetProperty("current", out var tempCur))
                temp = tempCur.GetDouble();

            var caps = BuildCapabilities(root, busType, temp);
            var ataAttrs = ParseAtaAttributes(root).ToList();

            return new DriveRawData
            {
                Id = driveId,
                Name = string.IsNullOrEmpty(model) ? $"Drive {devicePath}" : model,
                Model = model,
                Serial = serial,
                DevicePath = devicePath,
                BusType = busType,
                MediaType = mediaType,
                CapacityBytes = capacity,
                FirmwareVersion = firmware,
                TemperatureC = temp,
                Capabilities = caps,
                SmartOverallHealth = GetSmartHealth(root),
                PredictedFailure = GetSmartHealth(root) == "FAILED",
                WearPercentUsed = GetNvmeField(root, "percentage_used"),
                AvailableSparePercent = GetNvmeField(root, "available_spare"),
                UnsafeShutdowns = GetNvmeLong(root, "unsafe_shutdowns"),
                MediaErrors = GetNvmeLong(root, "media_errors"),
                PowerOnHours = GetPowerOnHours(root),
                PowerCycleCount = GetNvmeLong(root, "power_cycles"),
                ReallocatedSectors = AttrRawInt(ataAttrs, "reallocated_sector", "reallocated sector"),
                PendingSectors = AttrRawInt(ataAttrs, "current_pending", "current pending"),
                UncorrectableErrors = AttrRawInt(ataAttrs, "uncorrectable", "offline uncorrectable"),
                RawAttributes = ataAttrs,
            };
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Drive parse failed for {DevicePath}", devicePath);
            return null;
        }
    }

    private static string DetectBusType(JsonElement root)
    {
        var protocol = "";
        var type = "";
        if (root.TryGetProperty("device", out var dev))
        {
            if (dev.TryGetProperty("protocol", out var p)) protocol = p.GetString() ?? "";
            if (dev.TryGetProperty("type",     out var t)) type     = t.GetString() ?? "";
        }
        if (protocol.Contains("nvme", StringComparison.OrdinalIgnoreCase) ||
            type.Contains("nvme", StringComparison.OrdinalIgnoreCase)) return "nvme";
        if (protocol.Contains("usb", StringComparison.OrdinalIgnoreCase)) return "usb";
        if (protocol.Contains("sata", StringComparison.OrdinalIgnoreCase) ||
            type.Contains("ata", StringComparison.OrdinalIgnoreCase)) return "sata";
        return "unknown";
    }

    private static string DetectMediaType(JsonElement root, string busType)
    {
        if (busType == "nvme") return "nvme";
        if (root.TryGetProperty("rotation_rate", out var rr))
        {
            if (rr.ValueKind == JsonValueKind.Number)
            {
                var rpm = rr.GetInt32();
                if (rpm == 0) return "ssd";
                if (rpm > 0) return "hdd";
            }
            else if (rr.ValueKind == JsonValueKind.String)
            {
                var rpmStr = rr.GetString() ?? "";
                if (rpmStr.Contains("Solid State", StringComparison.OrdinalIgnoreCase)) return "ssd";
            }
        }
        return "unknown";
    }

    private static string? GetSmartHealth(JsonElement root)
    {
        if (root.TryGetProperty("smart_status", out var ss) &&
            ss.TryGetProperty("passed", out var p))
            return p.GetBoolean() ? "PASSED" : "FAILED";
        return null;
    }

    private static double? GetNvmeField(JsonElement root, string key)
    {
        if (!root.TryGetProperty("nvme_smart_health_information_log", out var nvme)) return null;
        if (!nvme.TryGetProperty(key, out var v)) return null;
        return v.ValueKind == JsonValueKind.Number ? v.GetDouble() : (double?)null;
    }

    private static long? GetNvmeLong(JsonElement root, string key)
    {
        if (!root.TryGetProperty("nvme_smart_health_information_log", out var nvme)) return null;
        if (!nvme.TryGetProperty(key, out var v)) return null;
        return v.ValueKind == JsonValueKind.Number ? v.GetInt64() : (long?)null;
    }

    private static long? GetPowerOnHours(JsonElement root)
    {
        var nvme = GetNvmeLong(root, "power_on_hours");
        if (nvme.HasValue) return nvme;
        if (root.TryGetProperty("power_on_time", out var pot) &&
            pot.TryGetProperty("hours", out var h))
            return h.GetInt64();
        return null;
    }

    private static DriveCapabilitySet BuildCapabilities(JsonElement root, string busType, double? temp)
    {
        bool hasSelfTest = false;
        bool hasAbort = false;
        bool hasConveyance = false;

        if (root.TryGetProperty("ata_smart_data", out var ataData) &&
            ataData.TryGetProperty("capabilities", out var caps))
        {
            if (caps.TryGetProperty("self_tests_supported", out var st) && st.GetBoolean())
                hasSelfTest = true;
            if (caps.TryGetProperty("conveyance_self_test_supported", out var cv) && cv.GetBoolean())
                hasConveyance = true;
        }
        if (busType == "nvme" && root.TryGetProperty("nvme_self_test_log", out _))
            hasSelfTest = true;
        if (hasSelfTest) hasAbort = true;

        var smartHealth = GetSmartHealth(root);
        bool smartRead = smartHealth is not null;

        return new DriveCapabilitySet
        {
            SmartRead = smartRead,
            SmartSelfTestShort = hasSelfTest,
            SmartSelfTestExtended = hasSelfTest,
            SmartSelfTestConveyance = hasConveyance,
            SmartSelfTestAbort = hasAbort,
            TemperatureSource = temp.HasValue ? "smartctl" : "none",
            HealthSource = smartRead ? "smartctl" : "none",
        };
    }

    private static long? AttrRawInt(IList<DriveRawAttribute> attrs, params string[] nameFragments)
    {
        foreach (var frag in nameFragments)
        {
            foreach (var a in attrs)
            {
                if (a.Name.Contains(frag, StringComparison.OrdinalIgnoreCase))
                {
                    var part = a.RawValue.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                    if (long.TryParse(part, out var v)) return v;
                }
            }
        }
        return null;
    }

    private static IEnumerable<DriveRawAttribute> ParseAtaAttributes(JsonElement root)
    {
        if (!root.TryGetProperty("ata_smart_attributes", out var ata)) yield break;
        if (!ata.TryGetProperty("table", out var table)) yield break;
        foreach (var row in table.EnumerateArray())
        {
            var key = row.TryGetProperty("id",   out var id)  ? id.ToString()       : "";
            var name = row.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "";
            string rawVal = "";
            if (row.TryGetProperty("raw", out var raw) && raw.TryGetProperty("value", out var rv))
                rawVal = rv.ToString();
            int? val   = row.TryGetProperty("value",  out var v)  && v.ValueKind == JsonValueKind.Number ? v.GetInt32()  : null;
            int? worst = row.TryGetProperty("worst",  out var w)  && w.ValueKind == JsonValueKind.Number ? w.GetInt32()  : null;
            int? thresh = row.TryGetProperty("thresh", out var th) && th.ValueKind == JsonValueKind.Number ? th.GetInt32() : null;

            var whenFailed = row.TryGetProperty("when_failed", out var wf) ? wf.GetString() ?? "" : "";
            var status = whenFailed == "now" ? "critical"
                       : whenFailed == "past" ? "warning"
                       : (val.HasValue && thresh.HasValue && val.Value < thresh.Value) ? "critical"
                       : "ok";

            yield return new DriveRawAttribute
            {
                Key = key, Name = name,
                NormalizedValue = val, WorstValue = worst, Threshold = thresh,
                RawValue = rawVal, Status = status,
                SourceKind = "ata_smart",
            };
        }
    }

    // ── Scan / poll implementations ───────────────────────────────────────────

    private async Task RescanAsync(DriveSettings s, CancellationToken ct)
    {
        // Re-check availability each rescan so installing smartctl after startup is picked up.
        _smartctlAvailable = await CheckSmartctlAsync(s.SmartctlPath, ct);
        if (!_smartctlAvailable) return;

        using var scanDoc = await RunSmartctlAsync(
            s.SmartctlPath, ["--scan-open", "--json"], 10, ct);
        if (scanDoc is null) return;

        if (!scanDoc.RootElement.TryGetProperty("devices", out var devices)) return;

        var newDrives = new Dictionary<string, DriveRawData>();
        foreach (var dev in devices.EnumerateArray())
        {
            var devPath = dev.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(devPath) || !ValidateDevicePath(devPath)) continue;

            using var detailDoc = await RunSmartctlAsync(
                s.SmartctlPath, ["-i", "-H", "-A", "-l", "selftest", "-j", devPath], 10, ct);
            if (detailDoc is null) continue;

            var raw = ParseDrive(detailDoc.RootElement, devPath);
            if (raw is not null) newDrives[raw.Id] = raw;
        }

        lock (_drivesLock)
        {
            // Remove drives that are no longer present in the new scan
            var toRemove = _drives.Keys.Except(newDrives.Keys).ToList();
            foreach (var id in toRemove) _drives.Remove(id);
            foreach (var (id, raw) in newDrives)
                _drives[id] = raw;
        }

        await UpsertDrivesAsync(newDrives.Values, ct);
        await PublishDriveSensorsAsync(s, ct);
        _log.LogDebug("Drive rescan complete: {Count} drives", newDrives.Count);
    }

    private async Task PollTempsAsync(DriveSettings s, CancellationToken ct)
    {
        bool updated = false;
        List<DriveRawData> snapshot;
        lock (_drivesLock) snapshot = [.. _drives.Values];

        foreach (var raw in snapshot)
        {
            if (!ValidateDevicePath(raw.DevicePath)) continue;
            using var doc = await RunSmartctlAsync(
                s.SmartctlPath, ["-a", "-j", raw.DevicePath], 10, ct);
            if (doc is null) continue;
            var refreshed = ParseDrive(doc.RootElement, raw.DevicePath);
            if (refreshed is not null)
            {
                lock (_drivesLock) _drives[raw.Id] = refreshed;
                if (refreshed.TemperatureC.HasValue) updated = true;
            }
        }
        if (updated) await PublishDriveSensorsAsync(s, ct);
    }

    private async Task PollHealthAsync(DriveSettings s, CancellationToken ct)
    {
        List<DriveRawData> snapshot;
        lock (_drivesLock) snapshot = [.. _drives.Values];

        foreach (var raw in snapshot)
        {
            if (!ValidateDevicePath(raw.DevicePath)) continue;
            using var doc = await RunSmartctlAsync(
                s.SmartctlPath, ["-a", "-j", raw.DevicePath], 10, ct);
            if (doc is null) continue;
            var refreshed = ParseDrive(doc.RootElement, raw.DevicePath);
            if (refreshed is null) continue;

            lock (_drivesLock) _drives[raw.Id] = refreshed;
            if (_normalizer is not null)
                await InsertHealthSnapshotAsync(refreshed, _normalizer, ct);
        }
        await PublishDriveSensorsAsync(s, ct);
    }

    // ── Sensor injection ──────────────────────────────────────────────────────

    private Task PublishDriveSensorsAsync(DriveSettings s, CancellationToken _)
    {
        if (_sensorService is null || _normalizer is null) return Task.CompletedTask;
        List<DriveRawData> drives;
        lock (_drivesLock) drives = [.. _drives.Values];

        var readings = drives
            .Where(d => d.TemperatureC.HasValue)
            .Select(d => new SensorReading
            {
                Id = $"hdd_temp_{d.Id}",
                Name = d.Name,
                SensorType = "hdd_temp",
                Value = d.TemperatureC!.Value,
                MinValue = 0,
                MaxValue = _normalizer.TempCriticalC(d) + 20.0,
                Unit = "°C",
                DriveId = d.Id,
                EntityName = d.Name,
                SourceKind = d.Capabilities.TemperatureSource == "smartctl" ? "smartctl" : "native",
            })
            .ToList();

        _sensorService.UpdateDriveReadings(readings);
        return Task.CompletedTask;
    }

    // ── Public manual actions ─────────────────────────────────────────────────

    public async Task<int> RescanNowAsync(DriveSettings s, CancellationToken ct = default)
    {
        await RescanAsync(s, ct);
        lock (_drivesLock) return _drives.Count;
    }

    public async Task<DriveRawData?> RefreshDriveAsync(
        string driveId, DriveSettings s, CancellationToken ct = default)
    {
        DriveRawData? raw;
        lock (_drivesLock) raw = _drives.GetValueOrDefault(driveId);
        if (raw is null || !ValidateDevicePath(raw.DevicePath)) return raw;

        using var doc = await RunSmartctlAsync(
            s.SmartctlPath, ["-a", "-j", raw.DevicePath], 10, ct);
        if (doc is null) return raw;

        var refreshed = ParseDrive(doc.RootElement, raw.DevicePath);
        if (refreshed is not null)
        {
            lock (_drivesLock) _drives[driveId] = refreshed;
            await PublishDriveSensorsAsync(s, ct);
        }
        return refreshed ?? raw;
    }

    public async Task<string?> StartSelfTestAsync(
        string driveId, SelfTestType testType, DriveSettings s, CancellationToken ct = default)
    {
        DriveRawData? raw;
        lock (_drivesLock) raw = _drives.GetValueOrDefault(driveId);
        if (raw is null || !ValidateDevicePath(raw.DevicePath)) return null;

        var typeFlag = testType switch
        {
            SelfTestType.Short      => "short",
            SelfTestType.Extended   => "long",
            SelfTestType.Conveyance => "conveyance",
            _ => "short",
        };

        using var doc = await RunSmartctlAsync(
            s.SmartctlPath, ["-t", typeFlag, "-j", raw.DevicePath], 15, ct);
        return doc is not null ? $"smartctl_{raw.DevicePath}_{typeFlag}" : null;
    }

    public async Task<bool> AbortSelfTestAsync(
        string driveId, DriveSettings s, CancellationToken ct = default)
    {
        DriveRawData? raw;
        lock (_drivesLock) raw = _drives.GetValueOrDefault(driveId);
        if (raw is null || !ValidateDevicePath(raw.DevicePath)) return false;

        using var doc = await RunSmartctlAsync(
            s.SmartctlPath, ["-X", "-j", raw.DevicePath], 15, ct);
        return doc is not null;
    }

    // ── DB helpers ────────────────────────────────────────────────────────────

    private async Task UpsertDrivesAsync(
        IEnumerable<DriveRawData> drives, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        var now = DateTimeOffset.UtcNow.ToString("o");
        await using var tx = await conn.BeginTransactionAsync(ct);
        foreach (var d in drives)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = (SqliteTransaction)tx;
            cmd.CommandText = """
                INSERT INTO drives
                    (id, name, model, serial_full, device_path, bus_type, media_type,
                     capacity_bytes, firmware_version, smart_available, native_available,
                     supports_self_test, supports_abort, last_seen_at, last_updated_at)
                VALUES ($id,$name,$model,$serial,$devpath,$bus,$media,
                        $cap,$fw,$smart,$native,$selftest,$abort,$now,$now)
                ON CONFLICT(id) DO UPDATE SET
                    name=excluded.name, model=excluded.model,
                    serial_full=excluded.serial_full,
                    device_path=excluded.device_path, bus_type=excluded.bus_type,
                    media_type=excluded.media_type, capacity_bytes=excluded.capacity_bytes,
                    firmware_version=excluded.firmware_version,
                    smart_available=excluded.smart_available,
                    native_available=excluded.native_available,
                    supports_self_test=excluded.supports_self_test,
                    supports_abort=excluded.supports_abort,
                    last_seen_at=excluded.last_seen_at,
                    last_updated_at=excluded.last_updated_at
                """;
            cmd.Parameters.AddWithValue("$id",       d.Id);
            cmd.Parameters.AddWithValue("$name",     d.Name);
            cmd.Parameters.AddWithValue("$model",    d.Model);
            cmd.Parameters.AddWithValue("$serial",   d.Serial);
            cmd.Parameters.AddWithValue("$devpath",  d.DevicePath);
            cmd.Parameters.AddWithValue("$bus",      d.BusType);
            cmd.Parameters.AddWithValue("$media",    d.MediaType);
            cmd.Parameters.AddWithValue("$cap",      d.CapacityBytes);
            cmd.Parameters.AddWithValue("$fw",       d.FirmwareVersion);
            cmd.Parameters.AddWithValue("$smart",    d.Capabilities.SmartRead ? 1 : 0);
            cmd.Parameters.AddWithValue("$native",   0);
            cmd.Parameters.AddWithValue("$selftest", d.Capabilities.SmartSelfTestShort ? 1 : 0);
            cmd.Parameters.AddWithValue("$abort",    d.Capabilities.SmartSelfTestAbort ? 1 : 0);
            cmd.Parameters.AddWithValue("$now",      now);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    private async Task InsertHealthSnapshotAsync(
        DriveRawData d, DriveHealthNormalizer normalizer, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO drive_health_snapshots
                (drive_id, recorded_at, temperature_c, health_status, health_percent,
                 predicted_failure, wear_percent_used, available_spare_percent,
                 reallocated_sectors, pending_sectors, uncorrectable_errors,
                 media_errors, power_on_hours, unsafe_shutdowns)
            VALUES ($did,$now,$temp,$health,$healthpct,$pf,$wear,$spare,
                    $realloc,$pending,$uncorr,$media,$poh,$unsafe)
            """;
        var now = DateTimeOffset.UtcNow.ToString("o");
        cmd.Parameters.AddWithValue("$did",       d.Id);
        cmd.Parameters.AddWithValue("$now",       now);
        cmd.Parameters.AddWithValue("$temp",      (object?)d.TemperatureC ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$health",    normalizer.HealthStatus(d));
        cmd.Parameters.AddWithValue("$healthpct", (object?)normalizer.HealthPercent(d) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pf",        d.PredictedFailure ? 1 : 0);
        cmd.Parameters.AddWithValue("$wear",      (object?)d.WearPercentUsed ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$spare",     (object?)d.AvailableSparePercent ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$realloc",   (object?)d.ReallocatedSectors ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pending",   (object?)d.PendingSectors ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$uncorr",    (object?)d.UncorrectableErrors ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$media",     (object?)d.MediaErrors ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$poh",       (object?)d.PowerOnHours ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$unsafe",    (object?)d.UnsafeShutdowns ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Helper to build summary/detail shapes ─────────────────────────────────

    public DriveSummary ToSummary(DriveRawData d)
    {
        var health = _normalizer?.HealthStatus(d) ?? "unknown";
        var healthPct = _normalizer?.HealthPercent(d);
        return new DriveSummary
        {
            Id = d.Id, Name = d.Name, Model = d.Model,
            SerialMasked = MaskSerial(d.Serial),
            DevicePathMasked = MaskDevicePath(d.DevicePath),
            BusType = d.BusType, MediaType = d.MediaType,
            CapacityBytes = d.CapacityBytes,
            TemperatureC = d.TemperatureC,
            HealthStatus = health, HealthPercent = healthPct,
            SmartAvailable = d.Capabilities.SmartRead,
            NativeAvailable = d.Capabilities.HealthSource != "none",
            SupportsSelfTest = d.Capabilities.SmartSelfTestShort,
            SupportsAbort = d.Capabilities.SmartSelfTestAbort,
        };
    }

    public DriveDetail ToDetail(DriveRawData d, DriveSelfTestRun? lastTest = null)
    {
        var summary = ToSummary(d);
        var warnC = _normalizer?.TempWarningC(d) ?? 45.0;
        var critC = _normalizer?.TempCriticalC(d) ?? 50.0;
        return new DriveDetail
        {
            Id = summary.Id, Name = summary.Name, Model = summary.Model,
            SerialMasked = summary.SerialMasked,
            DevicePathMasked = summary.DevicePathMasked,
            BusType = summary.BusType, MediaType = summary.MediaType,
            CapacityBytes = summary.CapacityBytes,
            TemperatureC = summary.TemperatureC,
            HealthStatus = summary.HealthStatus, HealthPercent = summary.HealthPercent,
            SmartAvailable = summary.SmartAvailable,
            NativeAvailable = summary.NativeAvailable,
            SupportsSelfTest = summary.SupportsSelfTest,
            SupportsAbort = summary.SupportsAbort,
            SerialFull = d.Serial, DevicePath = d.DevicePath,
            FirmwareVersion = d.FirmwareVersion,
            InterfaceSpeed = d.InterfaceSpeed,
            RotationRateRpm = d.RotationRateRpm,
            PowerOnHours = d.PowerOnHours,
            PowerCycleCount = d.PowerCycleCount,
            UnsafeShutdowns = d.UnsafeShutdowns,
            WearPercentUsed = d.WearPercentUsed,
            AvailableSparePercent = d.AvailableSparePercent,
            ReallocatedSectors = d.ReallocatedSectors,
            PendingSectors = d.PendingSectors,
            UncorrectableErrors = d.UncorrectableErrors,
            MediaErrors = d.MediaErrors,
            PredictedFailure = d.PredictedFailure,
            TemperatureWarningC = warnC,
            TemperatureCriticalC = critC,
            Capabilities = d.Capabilities,
            LastSelfTest = lastTest,
            RawAttributes = d.RawAttributes,
        };
    }
}
