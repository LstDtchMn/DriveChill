using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;

namespace DriveChill.Api;

[ApiController]
[Route("api/update")]
public sealed class UpdateController : ControllerBase
{
    private const string GitHubRepo = "LstDtchMn/DriveChill";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

    // Strict semver pattern — prevents command injection in PowerShell arguments.
    private static readonly Regex SemverRe =
        new(@"^\d+\.\d+\.\d+(?:[-+][a-zA-Z0-9._-]+)?$", RegexOptions.Compiled);

    // Shared HttpClient avoids socket exhaustion from per-request instantiation.
    private static readonly HttpClient _httpClient = new(
        new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) });

    private static readonly SemaphoreSlim _cacheLock = new(1, 1);
    private static DateTimeOffset? _cachedAt;
    private static CheckResult?    _cachedResult;

    private readonly AppSettings _settings;
    private readonly ILogger<UpdateController> _log;

    public UpdateController(AppSettings settings, ILogger<UpdateController> log)
    {
        _settings = settings;
        _log      = log;
    }

    // ── GET /api/update/check ────────────────────────────────────────────────
    [HttpGet("check")]
    public async Task<IActionResult> CheckUpdate(CancellationToken ct)
    {
        try
        {
            var result = await FetchLatestAsync(ct);
            return Ok(result);
        }
        catch (HttpRequestException ex)
        {
            _log.LogWarning(ex, "Could not reach GitHub releases API");
            return StatusCode(503, new { detail = $"Could not reach GitHub releases API: {ex.Message}" });
        }
    }

    // ── POST /api/update/apply ───────────────────────────────────────────────
    [HttpPost("apply")]
    public async Task<IActionResult> ApplyUpdate(CancellationToken ct)
    {
        var info    = await FetchLatestAsync(ct);
        var version = info.Latest;
        var deploy  = info.Deployment;

        if (deploy == "docker")
        {
            return Ok(new
            {
                status  = "manual_required",
                message = "Docker containers update via image pull.",
                command = "docker compose pull && docker compose up -d",
            });
        }

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return Ok(new
            {
                status  = "manual_required",
                message = "Automated update is only supported on Windows.",
            });
        }

        // Guard against command injection: version must be strict semver.
        if (!SemverRe.IsMatch(version))
        {
            _log.LogError("GitHub returned unexpected version string: {Version}", version);
            return StatusCode(500, new { detail = "Unexpected version string from GitHub." });
        }

        // Locate update_windows.ps1 via fallback chain.
        var scriptName = "update_windows.ps1";
        var exeDir     = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("DRIVECHILL_SCRIPTS_DIR"),
            Path.Combine(exeDir, "scripts"),
            Path.Combine(exeDir, "..", "scripts"),
        };
        var psScript = candidates
            .Where(d => d != null)
            .Select(d => Path.Combine(d!, scriptName))
            .FirstOrDefault(System.IO.File.Exists);

        if (psScript is null)
        {
            _log.LogError("Update script not found. Searched: {Candidates}",
                string.Join(", ", candidates.Where(d => d != null)));
            return StatusCode(500, new { detail = "Update script not found. Check server logs." });
        }

        try
        {
            // Launch PowerShell with UAC elevation (ShellExecute "runas" verb).
            // Version is validated as semver above and double-quoted in arguments.
            var psi = new ProcessStartInfo
            {
                FileName        = "powershell.exe",
                Arguments       = $"-NoProfile -ExecutionPolicy Bypass -File \"{psScript}\" -Version \"{version}\" -Artifact windows -InstallDir \"{exeDir.TrimEnd(Path.DirectorySeparatorChar)}\"",
                Verb            = "runas",
                UseShellExecute = true,
            };
            var proc = Process.Start(psi);
            if (proc is null)
            {
                _log.LogError("Process.Start returned null for update script");
                return StatusCode(500, new { detail = "Failed to start update process." });
            }
            _log.LogInformation("Update to v{Version} triggered via update_windows.ps1", version);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to launch update script");
            return StatusCode(500, new { detail = $"Failed to launch updater: {ex.Message}" });
        }

        return Ok(new
        {
            status  = "update_started",
            version,
            message = "Update is running. The service will restart automatically. "
                    + "Reconnect to the dashboard in ~30 seconds.",
        });
    }

    // ── Internal helpers ─────────────────────────────────────────────────────

    private async Task<CheckResult> FetchLatestAsync(CancellationToken ct)
    {
        await _cacheLock.WaitAsync(ct);
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (_cachedAt is not null && _cachedResult is not null && (now - _cachedAt.Value) < CacheTtl)
                return _cachedResult;

            var http = _httpClient;
            http.DefaultRequestHeaders.UserAgent.Clear();
            http.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("DriveChill", _settings.AppVersion));

            var url      = $"https://api.github.com/repos/{GitHubRepo}/releases/latest";
            var response = await http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            using var doc      = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var root           = doc.RootElement;
            var latestTag      = root.GetProperty("tag_name").GetString()?.TrimStart('v') ?? "";
            var rawReleaseUrl  = root.GetProperty("html_url").GetString()  ?? "";
            // Sanitize release URL — only allow https:// scheme to prevent open redirect
            var releaseUrl     = Uri.TryCreate(rawReleaseUrl, UriKind.Absolute, out var releaseUri)
                                 && releaseUri.Scheme == "https"
                                 ? rawReleaseUrl : "";
            var updateAvail    = CompareVersions(latestTag, _settings.AppVersion) > 0;

            _cachedResult = new CheckResult(
                Current:         _settings.AppVersion,
                Latest:          latestTag,
                UpdateAvailable: updateAvail,
                ReleaseUrl:      releaseUrl,
                Deployment:      DetectDeployment());
            _cachedAt = now;
            return _cachedResult;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    private static string DetectDeployment()
    {
        if (System.IO.File.Exists("/.dockerenv")) return "docker";
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "other";

        try
        {
            var result = Process.Start(new ProcessStartInfo
            {
                FileName               = "sc.exe",
                Arguments              = "query DriveChill",
                RedirectStandardOutput = true,
                UseShellExecute        = false,
            });
            result?.WaitForExit(3000);
            return result?.ExitCode == 0 ? "windows_service" : "windows_standalone";
        }
        catch { return "windows_standalone"; }
    }

    private static int CompareVersions(string a, string b)
    {
        static (int, int, int) Parse(string v)
        {
            var parts = v.Split('-')[0].Split('.');
            return parts.Length >= 3
                ? (int.TryParse(parts[0], out var ma) ? ma : 0,
                   int.TryParse(parts[1], out var mi) ? mi : 0,
                   int.TryParse(parts[2], out var pa) ? pa : 0)
                : (0, 0, 0);
        }
        var (aMa, aMi, aPa) = Parse(a);
        var (bMa, bMi, bPa) = Parse(b);
        return (aMa, aMi, aPa).CompareTo((bMa, bMi, bPa));
    }

    private sealed record CheckResult(
        string Current, string Latest, bool UpdateAvailable,
        string ReleaseUrl, string Deployment);
}
