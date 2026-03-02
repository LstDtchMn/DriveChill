using Microsoft.AspNetCore.Mvc;
using DriveChill.Services;
using DriveChill.Models;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace DriveChill.Api;

[ApiController]
[Route("api/machines")]
public sealed class MachinesController : ControllerBase
{
    private readonly DbService   _db;
    private readonly AppSettings _settings;

    public MachinesController(DbService db, AppSettings settings)
    {
        _db       = db;
        _settings = settings;
    }

    [HttpGet]
    public async Task<IActionResult> GetMachines(CancellationToken ct)
    {
        var machines = await _db.GetMachinesAsync(ct);
        return Ok(new { machines = machines.Select(ToView) });
    }

    [HttpPost]
    public async Task<IActionResult> CreateMachine([FromBody] JsonElement body, CancellationToken ct)
    {
        var name    = body.TryGetProperty("name",     out var n) ? n.GetString() ?? "" : "";
        var baseUrl = body.TryGetProperty("base_url", out var u) ? u.GetString() ?? "" : "";
        var apiKey  = body.TryGetProperty("api_key",  out var k) ? k.GetString() : null;

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(baseUrl))
            return BadRequest(new { detail = "name and base_url are required" });

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
            return BadRequest(new { detail = "base_url must be a valid http(s) URL" });

        var record = new MachineRecord
        {
            Name       = name.Trim(),
            BaseUrl    = baseUrl.TrimEnd('/'),
            ApiKeyHash = apiKey,
        };
        var created = await _db.CreateMachineAsync(record, ct);
        return Ok(new { machine = ToView(created) });
    }

    [HttpGet("{machineId}")]
    public async Task<IActionResult> GetMachine(string machineId, CancellationToken ct)
    {
        var machine = await _db.GetMachineAsync(machineId, ct);
        if (machine is null) return NotFound(new { detail = "Machine not found" });
        return Ok(new { machine = ToView(machine) });
    }

    [HttpPut("{machineId}")]
    public async Task<IActionResult> UpdateMachine(string machineId,
        [FromBody] JsonElement body, CancellationToken ct)
    {
        var existing = await _db.GetMachineAsync(machineId, ct);
        if (existing is null) return NotFound(new { detail = "Machine not found" });

        var patch = new MachineRecord
        {
            Name                = body.TryGetProperty("name",    out var n) ? n.GetString() ?? existing.Name   : existing.Name,
            BaseUrl             = body.TryGetProperty("base_url", out var u) ? u.GetString() ?? existing.BaseUrl : existing.BaseUrl,
            ApiKeyHash          = body.TryGetProperty("api_key", out var k) ? k.GetString() : existing.ApiKeyHash,
            Enabled             = body.TryGetProperty("enabled", out var e) ? (e.ValueKind == JsonValueKind.True) : existing.Enabled,
            PollIntervalSeconds = body.TryGetProperty("poll_interval_seconds", out var p) ? (p.TryGetDouble(out var pd) ? pd : existing.PollIntervalSeconds) : existing.PollIntervalSeconds,
            TimeoutMs           = body.TryGetProperty("timeout_ms", out var t) ? (t.TryGetInt32(out var ti) ? ti : existing.TimeoutMs) : existing.TimeoutMs,
        };
        await _db.UpdateMachineAsync(machineId, patch, ct);
        var updated = await _db.GetMachineAsync(machineId, ct);
        return Ok(new { machine = ToView(updated!) });
    }

    [HttpDelete("{machineId}")]
    public async Task<IActionResult> DeleteMachine(string machineId, CancellationToken ct)
    {
        var deleted = await _db.DeleteMachineAsync(machineId, ct);
        if (!deleted) return NotFound(new { detail = "Machine not found" });
        return Ok(new { success = true });
    }

    [HttpGet("{machineId}/snapshot")]
    public async Task<IActionResult> GetSnapshot(string machineId, CancellationToken ct)
    {
        var machine = await _db.GetMachineAsync(machineId, ct);
        if (machine is null) return NotFound(new { detail = "Machine not found" });
        var snapshot = machine.SnapshotJson is not null
            ? JsonSerializer.Deserialize<object>(machine.SnapshotJson)
            : null;
        return Ok(new { machine_id = machineId, snapshot });
    }

    [HttpPost("{machineId}/verify")]
    public async Task<IActionResult> VerifyMachine(string machineId, CancellationToken ct)
    {
        var machine = await _db.GetMachineAsync(machineId, ct);
        if (machine is null) return NotFound(new { detail = "Machine not found" });

        try
        {
            using var client = await BuildProxyClientAsync(machine, ct);
            var resp = await client.GetAsync($"{machine.BaseUrl}/api/health", ct);
            resp.EnsureSuccessStatusCode();
            await _db.UpdateMachineStatusAsync(machineId, "online",
                DateTimeOffset.UtcNow.ToString("o"), null, 0, null, ct);
            return Ok(new { success = true, status = "online" });
        }
        catch (Exception ex)
        {
            await _db.UpdateMachineStatusAsync(machineId, "offline",
                null, ex.Message, 1, null, ct);
            return Ok(new { success = false, status = "offline", error = ex.Message });
        }
    }

    [HttpGet("{machineId}/state")]
    public async Task<IActionResult> GetMachineState(string machineId, CancellationToken ct)
    {
        var machine = await _db.GetMachineAsync(machineId, ct);
        if (machine is null) return NotFound(new { detail = "Machine not found" });

        try
        {
            using var client = await BuildProxyClientAsync(machine, ct);
            var profilesTask = client.GetStringAsync($"{machine.BaseUrl}/api/profiles", ct);
            var sensorsTask  = client.GetStringAsync($"{machine.BaseUrl}/api/sensors",  ct);
            var fansTask     = client.GetStringAsync($"{machine.BaseUrl}/api/fans",     ct);
            await Task.WhenAll(profilesTask, sensorsTask, fansTask);

            var profiles = JsonSerializer.Deserialize<JsonElement>(await profilesTask);
            var sensors  = JsonSerializer.Deserialize<JsonElement>(await sensorsTask);
            var fans     = JsonSerializer.Deserialize<JsonElement>(await fansTask);
            return Ok(new
            {
                state = new
                {
                    profiles = profiles.TryGetProperty("profiles", out var p) ? p : default,
                    fans     = fans.TryGetProperty("fans", out var f) ? f : default,
                    sensors  = sensors.TryGetProperty("readings", out var s) ? s : default,
                }
            });
        }
        catch (Exception ex)
        {
            return StatusCode(502, new { detail = $"Failed to fetch remote state: {ex.Message}" });
        }
    }

    [HttpPost("{machineId}/profiles/{profileId}/activate")]
    public async Task<IActionResult> ActivateRemoteProfile(
        string machineId, string profileId, CancellationToken ct)
    {
        var machine = await _db.GetMachineAsync(machineId, ct);
        if (machine is null) return NotFound(new { detail = "Machine not found" });

        try
        {
            using var client = await BuildProxyClientAsync(machine, ct);
            var resp = await client.PutAsync(
                $"{machine.BaseUrl}/api/profiles/{Uri.EscapeDataString(profileId)}/activate",
                new StringContent("{}", Encoding.UTF8, "application/json"), ct);
            resp.EnsureSuccessStatusCode();
            await _db.SetMachineLastCommandAsync(machineId, ct);
            return Ok(new { success = true });
        }
        catch (Exception ex)
        {
            return StatusCode(502, new { detail = ex.Message });
        }
    }

    [HttpPost("{machineId}/fans/release")]
    public async Task<IActionResult> ReleaseRemoteFans(string machineId, CancellationToken ct)
    {
        var machine = await _db.GetMachineAsync(machineId, ct);
        if (machine is null) return NotFound(new { detail = "Machine not found" });

        try
        {
            using var client = await BuildProxyClientAsync(machine, ct);
            var resp = await client.PostAsync(
                $"{machine.BaseUrl}/api/fans/release",
                new StringContent("{}", Encoding.UTF8, "application/json"), ct);
            resp.EnsureSuccessStatusCode();
            await _db.SetMachineLastCommandAsync(machineId, ct);
            return Ok(new { success = true });
        }
        catch (Exception ex)
        {
            return StatusCode(502, new { detail = ex.Message });
        }
    }

    [HttpPut("{machineId}/fans/{fanId}/settings")]
    public async Task<IActionResult> SetRemoteFanSettings(
        string machineId, string fanId, [FromBody] JsonElement body, CancellationToken ct)
    {
        var machine = await _db.GetMachineAsync(machineId, ct);
        if (machine is null) return NotFound(new { detail = "Machine not found" });

        try
        {
            using var client = await BuildProxyClientAsync(machine, ct);
            var content = new StringContent(body.GetRawText(), Encoding.UTF8, "application/json");
            var resp = await client.PutAsync(
                $"{machine.BaseUrl}/api/fans/{Uri.EscapeDataString(fanId)}/settings",
                content, ct);
            resp.EnsureSuccessStatusCode();
            await _db.SetMachineLastCommandAsync(machineId, ct);
            var responseBody = await resp.Content.ReadAsStringAsync(ct);
            return Content(responseBody, "application/json");
        }
        catch (Exception ex)
        {
            return StatusCode(502, new { detail = ex.Message });
        }
    }

    // -----------------------------------------------------------------------
    // SSRF-safe proxy client
    // -----------------------------------------------------------------------

    /// <summary>
    /// Resolves the machine's hostname once, validates the resolved IP against SSRF rules,
    /// then returns an <see cref="HttpClient"/> whose <see cref="SocketsHttpHandler"/> routes
    /// TCP connections directly to that locked IP — closing the DNS-rebinding window.
    ///
    /// For HTTPS the original hostname is still used for TLS SNI (so the server certificate
    /// can be validated against the hostname the user configured), and the Host header is set
    /// automatically by HttpClient from the request URI.
    /// </summary>
    private async Task<HttpClient> BuildProxyClientAsync(MachineRecord machine, CancellationToken ct)
    {
        if (!Uri.TryCreate(machine.BaseUrl, UriKind.Absolute, out var uri))
            throw new InvalidOperationException("Invalid machine base_url.");

        var timeoutMs    = machine.TimeoutMs > 0 ? machine.TimeoutMs : 5000;
        var originalHost = uri.DnsSafeHost;
        var port         = uri.IsDefaultPort
            ? (uri.Scheme == "https" ? 443 : 80)
            : uri.Port;

        IPAddress lockedIp;
        if (_settings.AllowPrivateOutboundTargets)
        {
            var addrs = await Dns.GetHostAddressesAsync(originalHost, ct);
            lockedIp = addrs.FirstOrDefault()
                       ?? throw new InvalidOperationException($"Cannot resolve host '{originalHost}'.");
        }
        else
        {
            IPAddress[] addrs;
            try { addrs = await Dns.GetHostAddressesAsync(originalHost, ct); }
            catch (SocketException ex)
            {
                throw new InvalidOperationException(
                    $"Cannot resolve host '{originalHost}': {ex.Message}");
            }

            lockedIp = addrs.FirstOrDefault(ip => !IsPrivateOrLoopback(ip))
                       ?? throw new InvalidOperationException(
                           $"Outbound requests to private/loopback addresses are not allowed " +
                           $"(all resolved addresses for '{originalHost}' are private). " +
                           $"Set DRIVECHILL_ALLOW_PRIVATE_OUTBOUND_TARGETS=true to override.");
        }

        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            // Route every TCP connection to the pre-resolved IP — no second DNS lookup.
            ConnectCallback = async (ctx, cToken) =>
            {
                var socket = new Socket(lockedIp.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true,
                };
                await socket.ConnectAsync(new IPEndPoint(lockedIp, port), cToken);
                return new NetworkStream(socket, ownsSocket: true);
            },
            // For HTTPS, TargetHost tells SslStream which hostname to present in SNI
            // so the server certificate is validated against the original hostname.
            SslOptions = new SslClientAuthenticationOptions
            {
                TargetHost = originalHost,
            },
        };

        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromMilliseconds(timeoutMs),
        };

        if (!string.IsNullOrEmpty(machine.ApiKeyHash))
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", machine.ApiKeyHash);

        return client;
    }

    // -----------------------------------------------------------------------

    private static bool IsPrivateOrLoopback(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;

        if (ip.AddressFamily == AddressFamily.InterNetworkV6 && ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.Equals(IPAddress.IPv6Loopback);

        var b = ip.GetAddressBytes();
        return b[0] == 10                                          // 10.0.0.0/8
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)          // 172.16.0.0/12
            || (b[0] == 192 && b[1] == 168)                        // 192.168.0.0/16
            || (b[0] == 169 && b[1] == 254);                       // 169.254.0.0/16 link-local
    }

    private static object ToView(MachineRecord m) => new
    {
        id                    = m.Id,
        name                  = m.Name,
        base_url              = m.BaseUrl,
        has_api_key           = m.ApiKeyHash is not null,
        api_key_id            = (object?)null,
        enabled               = m.Enabled,
        poll_interval_seconds = m.PollIntervalSeconds,
        timeout_ms            = m.TimeoutMs,
        status                = m.Status,
        last_seen_at          = m.LastSeenAt,
        last_error            = m.LastError,
        consecutive_failures  = m.ConsecutiveFailures,
        created_at            = m.CreatedAt,
        updated_at            = m.UpdatedAt,
        freshness_seconds     = m.FreshnessSeconds,
        snapshot              = (object?)null,
        capabilities          = new string[0],
        last_command_at       = m.LastCommandAt,
    };
}
