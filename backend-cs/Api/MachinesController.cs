using Microsoft.AspNetCore.Mvc;
using DriveChill.Services;
using DriveChill.Models;
using System.Text.Json;

namespace DriveChill.Api;

[ApiController]
[Route("api/machines")]
public sealed class MachinesController : ControllerBase
{
    private readonly DbService           _db;
    private readonly IHttpClientFactory  _httpFactory;
    private readonly AppSettings         _settings;

    public MachinesController(DbService db, IHttpClientFactory httpFactory, AppSettings settings)
    {
        _db          = db;
        _httpFactory = httpFactory;
        _settings    = settings;
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
        var name    = body.TryGetProperty("name",    out var n) ? n.GetString() ?? "" : "";
        var baseUrl = body.TryGetProperty("base_url", out var u) ? u.GetString() ?? "" : "";
        var apiKey  = body.TryGetProperty("api_key", out var k) ? k.GetString() : null;

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(baseUrl))
            return BadRequest(new { detail = "name and base_url are required" });

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
            return BadRequest(new { detail = "base_url must be a valid http(s) URL" });

        var record = new MachineRecord
        {
            Name       = name.Trim(),
            BaseUrl    = baseUrl.TrimEnd('/'),
            ApiKeyHash = apiKey,  // stored as plaintext API key for outbound auth
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
            Name                = body.TryGetProperty("name", out var n)    ? n.GetString() ?? existing.Name   : existing.Name,
            BaseUrl             = body.TryGetProperty("base_url", out var u) ? u.GetString() ?? existing.BaseUrl : existing.BaseUrl,
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
            using var client = CreateAgentClient(machine);
            var resp = await client.GetAsync($"{machine.BaseUrl}/api/health", ct);
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadAsStringAsync(ct);
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
            using var client = CreateAgentClient(machine);
            var profilesTask = client.GetStringAsync($"{machine.BaseUrl}/api/profiles", ct);
            var sensorsTask  = client.GetStringAsync($"{machine.BaseUrl}/api/sensors",  ct);
            await Task.WhenAll(profilesTask, sensorsTask);

            var profiles = JsonSerializer.Deserialize<JsonElement>(await profilesTask);
            var sensors  = JsonSerializer.Deserialize<JsonElement>(await sensorsTask);
            return Ok(new
            {
                state = new
                {
                    profiles = profiles.TryGetProperty("profiles", out var p) ? p : default,
                    fans     = Array.Empty<object>(),
                    sensors  = sensors.TryGetProperty("readings",  out var s) ? s : default,
                }
            });
        }
        catch (Exception ex)
        {
            return StatusCode(502, new { detail = $"Failed to fetch remote state: {ex.Message}" });
        }
    }

    [HttpPost("{machineId}/profiles/{profileId}/activate")]
    public async Task<IActionResult> ActivateRemoteProfile(string machineId, string profileId, CancellationToken ct)
    {
        var machine = await _db.GetMachineAsync(machineId, ct);
        if (machine is null) return NotFound(new { detail = "Machine not found" });

        try
        {
            using var client = CreateAgentClient(machine);
            var resp = await client.PutAsync($"{machine.BaseUrl}/api/profiles/{Uri.EscapeDataString(profileId)}/activate",
                new StringContent("{}", System.Text.Encoding.UTF8, "application/json"), ct);
            resp.EnsureSuccessStatusCode();
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
            using var client = CreateAgentClient(machine);
            var resp = await client.PostAsync($"{machine.BaseUrl}/api/fans/release",
                new StringContent("{}", System.Text.Encoding.UTF8, "application/json"), ct);
            resp.EnsureSuccessStatusCode();
            return Ok(new { success = true });
        }
        catch (Exception ex)
        {
            return StatusCode(502, new { detail = ex.Message });
        }
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private HttpClient CreateAgentClient(MachineRecord machine)
    {
        var client = _httpFactory.CreateClient("machines");
        client.Timeout = TimeSpan.FromMilliseconds(machine.TimeoutMs > 0 ? machine.TimeoutMs : 5000);
        if (!string.IsNullOrEmpty(machine.ApiKeyHash))
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {machine.ApiKeyHash}");
        return client;
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
