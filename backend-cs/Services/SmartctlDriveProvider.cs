using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DriveChill.Models;

namespace DriveChill.Services;

/// <summary>
/// Drive provider that shells out to <c>smartctl</c> for all data acquisition.
/// </summary>
public sealed partial class SmartctlDriveProvider : IDriveProvider
{
    private static readonly Regex _windowsDeviceRe = WindowsDeviceRegex();
    private static readonly Regex _linuxDeviceRe   = LinuxDeviceRegex();

    [GeneratedRegex(@"^(\\\\\.\\PhysicalDrive\d+|/dev/sd[a-z]+|/dev/nvme\d+n?\d*)$")]
    private static partial Regex WindowsDeviceRegex();

    [GeneratedRegex(@"^(/dev/sd[a-z]+|/dev/hd[a-z]+|/dev/nvme\d+n\d+|/dev/disk/by-id/[A-Za-z0-9._:-]+)$")]
    private static partial Regex LinuxDeviceRegex();

    private readonly ILogger<SmartctlDriveProvider> _log;

    public SmartctlDriveProvider(ILogger<SmartctlDriveProvider> log) => _log = log;

    // ── IDriveProvider ────────────────────────────────────────────────────────

    public async Task<bool> CheckAvailableAsync(DriveSettings s, CancellationToken ct)
    {
        try
        {
            using var proc = new Process();
            proc.StartInfo = new ProcessStartInfo
            {
                FileName = s.SmartctlPath,
                Arguments = "--version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow = true,
            };
            proc.Start();
            await proc.WaitForExitAsync(ct);
            return proc.ExitCode == 0;
        }
        catch { return false; }
    }

    public async Task<IReadOnlyList<DriveRawData>> DiscoverDrivesAsync(
        DriveSettings s, CancellationToken ct)
    {
        using var scanDoc = await RunSmartctlAsync(
            s.SmartctlPath, ["--scan-open", "--json"], 10, ct);
        if (scanDoc is null) return [];

        if (!scanDoc.RootElement.TryGetProperty("devices", out var devices)) return [];

        var result = new List<DriveRawData>();
        foreach (var dev in devices.EnumerateArray())
        {
            var devPath = dev.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(devPath) || !ValidateDevicePath(devPath)) continue;

            using var detailDoc = await RunSmartctlAsync(
                s.SmartctlPath, ["-i", "-H", "-A", "-l", "selftest", "-j", devPath], 10, ct);
            if (detailDoc is null) continue;

            var raw = ParseDrive(detailDoc.RootElement, devPath);
            if (raw is not null) result.Add(raw);
        }
        return result;
    }

    public async Task<DriveRawData?> GetDriveDataAsync(
        string devicePath, DriveSettings s, CancellationToken ct)
    {
        if (!ValidateDevicePath(devicePath)) return null;
        using var doc = await RunSmartctlAsync(
            s.SmartctlPath, ["-a", "-j", devicePath], 10, ct);
        return doc is null ? null : ParseDrive(doc.RootElement, devicePath);
    }

    public async Task<string?> StartSelfTestAsync(
        string devicePath, string testType, DriveSettings s, CancellationToken ct)
    {
        if (!ValidateDevicePath(devicePath)) return null;
        using var doc = await RunSmartctlAsync(
            s.SmartctlPath, ["-t", testType, "-j", devicePath], 15, ct);
        return doc is not null ? $"smartctl_{devicePath}_{testType}" : null;
    }

    public async Task<bool> AbortSelfTestAsync(
        string devicePath, DriveSettings s, CancellationToken ct)
    {
        if (!ValidateDevicePath(devicePath)) return false;
        using var doc = await RunSmartctlAsync(
            s.SmartctlPath, ["-X", "-j", devicePath], 15, ct);
        return doc is not null;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task<JsonDocument?> RunSmartctlAsync(
        string smartctlPath, string[] args, int timeoutSeconds, CancellationToken ct)
    {
        using var proc = new Process();
        proc.StartInfo = new ProcessStartInfo
        {
            FileName = smartctlPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
            proc.StartInfo.ArgumentList.Add(arg);

        try
        {
            proc.Start();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = proc.StandardError.ReadToEndAsync(timeoutCts.Token);
            await Task.WhenAll(stdoutTask, stderrTask);
            var stdout = stdoutTask.Result;
            var stderr = stderrTask.Result;
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

    internal static bool ValidateDevicePath(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return _windowsDeviceRe.IsMatch(path);
        return _linuxDeviceRe.IsMatch(path);
    }

    internal static string DriveId(string serial, string model, string busType, string devicePath)
    {
        var key = string.IsNullOrEmpty(serial)
            ? $"noserial|{model}|{devicePath}"
            : $"{serial}|{model}|{busType}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hash)[..24].ToLowerInvariant();
    }

    internal static DriveRawData? ParseDrive(JsonElement root, string devicePath)
    {
        try
        {
            var serial   = root.TryGetProperty("serial_number",    out var sn) ? sn.GetString() ?? "" : "";
            var model    = root.TryGetProperty("model_name",       out var mn) ? mn.GetString() ?? "" : "";
            var firmware = root.TryGetProperty("firmware_version", out var fv) ? fv.GetString() ?? "" : "";

            var busType   = DetectBusType(root);
            var mediaType = DetectMediaType(root, busType);
            var driveId   = DriveId(serial, model, busType, devicePath);

            long capacity = 0;
            if (root.TryGetProperty("user_capacity", out var uc) && uc.TryGetProperty("bytes", out var ucb))
                capacity = ucb.GetInt64();

            double? temp = null;
            if (root.TryGetProperty("temperature", out var tempObj) &&
                tempObj.TryGetProperty("current", out var tempCur))
                temp = tempCur.GetDouble();

            var caps     = BuildCapabilities(root, busType, temp);
            var ataAttrs = ParseAtaAttributes(root).ToList();

            return new DriveRawData
            {
                Id              = driveId,
                Name            = string.IsNullOrEmpty(model) ? $"Drive {devicePath}" : model,
                Model           = model,
                Serial          = serial,
                DevicePath      = devicePath,
                BusType         = busType,
                MediaType       = mediaType,
                CapacityBytes   = capacity,
                FirmwareVersion = firmware,
                TemperatureC    = temp,
                Capabilities    = caps,
                SmartOverallHealth    = GetSmartHealth(root),
                PredictedFailure      = GetSmartHealth(root) == "FAILED",
                WearPercentUsed       = GetNvmeField(root, "percentage_used"),
                AvailableSparePercent = GetNvmeField(root, "available_spare"),
                UnsafeShutdowns       = GetNvmeLong(root, "unsafe_shutdowns"),
                MediaErrors           = GetNvmeLong(root, "media_errors"),
                PowerOnHours          = GetPowerOnHours(root),
                PowerCycleCount       = GetNvmeLong(root, "power_cycles"),
                ReallocatedSectors    = AttrRawInt(ataAttrs, "reallocated_sector", "reallocated sector"),
                PendingSectors        = AttrRawInt(ataAttrs, "current_pending", "current pending"),
                UncorrectableErrors   = AttrRawInt(ataAttrs, "uncorrectable", "offline uncorrectable"),
                RawAttributes         = ataAttrs,
            };
        }
        catch { return null; }
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
            type.Contains("nvme",     StringComparison.OrdinalIgnoreCase)) return "nvme";
        if (protocol.Contains("usb", StringComparison.OrdinalIgnoreCase)) return "usb";
        if (protocol.Contains("sata", StringComparison.OrdinalIgnoreCase) ||
            type.Contains("ata",      StringComparison.OrdinalIgnoreCase)) return "sata";
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
        bool hasSelfTest = false, hasAbort = false, hasConveyance = false;
        if (root.TryGetProperty("ata_smart_data", out var ataData) &&
            ataData.TryGetProperty("capabilities", out var caps))
        {
            if (caps.TryGetProperty("self_tests_supported",            out var st) && st.GetBoolean()) hasSelfTest  = true;
            if (caps.TryGetProperty("conveyance_self_test_supported",  out var cv) && cv.GetBoolean()) hasConveyance = true;
        }
        if (busType == "nvme" && root.TryGetProperty("nvme_self_test_log", out _))
            hasSelfTest = true;
        if (hasSelfTest) hasAbort = true;

        var smartHealth = GetSmartHealth(root);
        return new DriveCapabilitySet
        {
            SmartRead               = smartHealth is not null,
            SmartSelfTestShort      = hasSelfTest,
            SmartSelfTestExtended   = hasSelfTest,
            SmartSelfTestConveyance = hasConveyance,
            SmartSelfTestAbort      = hasAbort,
            TemperatureSource       = temp.HasValue ? "smartctl" : "none",
            HealthSource            = smartHealth is not null ? "smartctl" : "none",
        };
    }

    private static long? AttrRawInt(IList<DriveRawAttribute> attrs, params string[] nameFragments)
    {
        foreach (var frag in nameFragments)
            foreach (var a in attrs)
                if (a.Name.Contains(frag, StringComparison.OrdinalIgnoreCase))
                {
                    var part = a.RawValue.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                    if (long.TryParse(part, out var v)) return v;
                }
        return null;
    }

    private static IEnumerable<DriveRawAttribute> ParseAtaAttributes(JsonElement root)
    {
        if (!root.TryGetProperty("ata_smart_attributes", out var ata)) yield break;
        if (!ata.TryGetProperty("table", out var table)) yield break;
        foreach (var row in table.EnumerateArray())
        {
            var key   = row.TryGetProperty("id",   out var id)  ? id.ToString()        : "";
            var name  = row.TryGetProperty("name", out var nm)  ? nm.GetString() ?? "" : "";
            string rawVal = "";
            if (row.TryGetProperty("raw", out var raw) && raw.TryGetProperty("value", out var rv))
                rawVal = rv.ToString();
            int? val    = row.TryGetProperty("value",  out var v)  && v.ValueKind  == JsonValueKind.Number ? v.GetInt32()  : null;
            int? worst  = row.TryGetProperty("worst",  out var w)  && w.ValueKind  == JsonValueKind.Number ? w.GetInt32()  : null;
            int? thresh = row.TryGetProperty("thresh", out var th) && th.ValueKind == JsonValueKind.Number ? th.GetInt32() : null;
            var whenFailed = row.TryGetProperty("when_failed", out var wf) ? wf.GetString() ?? "" : "";
            var status = whenFailed == "now"  ? "critical"
                       : whenFailed == "past" ? "warning"
                       : (val.HasValue && thresh.HasValue && val.Value < thresh.Value) ? "critical"
                       : "ok";
            yield return new DriveRawAttribute
            {
                Key = key, Name = name,
                NormalizedValue = val, WorstValue = worst, Threshold = thresh,
                RawValue = rawVal, Status = status, SourceKind = "ata_smart",
            };
        }
    }
}
