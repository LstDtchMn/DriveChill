using Microsoft.AspNetCore.Mvc;
using DriveChill.Models;
using DriveChill.Services;

namespace DriveChill.Api;

[ApiController]
[Route("api/profiles")]
public sealed class ProfilesController : ControllerBase
{
    private readonly DbService     _db;
    private readonly FanService    _fans;
    private readonly AlertService  _alerts;

    public ProfilesController(DbService db, FanService fans, AlertService alerts)
    {
        _db     = db;
        _fans   = fans;
        _alerts = alerts;
    }

    /// <summary>GET /api/profiles</summary>
    [HttpGet]
    public async Task<IActionResult> GetProfiles(CancellationToken ct = default)
        => Ok(new { profiles = await _db.ListProfilesAsync(ct) });

    /// <summary>GET /api/profiles/{id}</summary>
    [HttpGet("{id}")]
    public async Task<IActionResult> GetProfile(string id, CancellationToken ct = default)
    {
        var profile = await _db.GetProfileAsync(id, ct);
        return profile is not null ? Ok(profile) : NotFound(new { detail = "Profile not found" });
    }

    /// <summary>POST /api/profiles</summary>
    [HttpPost]
    public async Task<IActionResult> CreateProfile([FromBody] CreateProfileRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(new { detail = "name is required" });

        var profile = new Profile
        {
            Name        = req.Name,
            Description = req.Description,
            Curves      = req.Curves,
        };

        await _db.CreateProfileAsync(profile, ct);
        return CreatedAtAction(nameof(GetProfile), new { id = profile.Id }, profile);
    }

    /// <summary>PUT /api/profiles/{id}</summary>
    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateProfile(string id, [FromBody] Profile updated, CancellationToken ct = default)
    {
        var existing = await _db.GetProfileAsync(id, ct);
        if (existing == null) return NotFound(new { detail = "Profile not found" });

        updated.Id        = id;
        updated.CreatedAt = existing.CreatedAt;
        updated.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.UpdateProfileAsync(updated, ct);
        return Ok(updated);
    }

    /// <summary>DELETE /api/profiles/{id}</summary>
    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteProfile(string id, CancellationToken ct = default)
    {
        var profile = await _db.GetProfileAsync(id, ct);
        if (profile is null)
            return NotFound(new { detail = "Profile not found" });
        if (profile.IsActive)
            return Conflict(new { detail = "Cannot delete the active profile. Activate a different profile first." });
        await _db.DeleteProfileAsync(id, ct);
        return Ok(new { success = true });
    }

    /// <summary>PUT /api/profiles/{id}/activate -- load curves into FanService.</summary>
    [HttpPut("{id}/activate")]
    public async Task<IActionResult> ActivateProfile(string id, CancellationToken ct = default)
    {
        var profile = await _db.GetProfileAsync(id, ct);
        if (profile == null) return NotFound(new { detail = "Profile not found" });

        // Mark active flag in DB
        await _db.ActivateProfileAsync(id, ct);
        profile.IsActive = true;

        // Replace all active curves atomically so orphaned curves from the
        // previous profile don't linger.
        _fans.SetCurves(profile.Curves);

        // Update alert service's pre-alert profile so future alert-triggered
        // switches know which profile to revert to.
        _alerts.SetPreAlertProfile(id);

        return Ok(profile);
    }

    /// <summary>GET /api/profiles/{id}/export -- portable JSON snapshot, export_version: 1.</summary>
    [HttpGet("{id}/export")]
    public async Task<IActionResult> ExportProfile(string id, CancellationToken ct = default)
    {
        var profile = await _db.GetProfileAsync(id, ct);
        if (profile is null) return NotFound(new { detail = "Profile not found" });

        return Ok(new
        {
            export_version = 1,
            profile = new
            {
                name   = profile.Name,
                preset = profile.Description,
                curves = profile.Curves,
            },
        });
    }

    /// <summary>POST /api/profiles/import -- create a new profile from a portable snapshot.</summary>
    [HttpPost("import")]
    public async Task<IActionResult> ImportProfile([FromBody] ImportProfileRequest req, CancellationToken ct = default)
    {
        var profileName = string.IsNullOrWhiteSpace(req.Name)
            ? $"Imported {RandomHex(3)}"
            : req.Name;

        var profile = new Profile
        {
            Name        = profileName,
            Description = req.Preset,
            Curves      = req.Curves,
        };

        await _db.CreateProfileAsync(profile, ct);
        return StatusCode(201, new { success = true, profile });
    }

    /// <summary>GET /api/profiles/preset-curves -- built-in curve templates.</summary>
    [HttpGet("preset-curves")]
    public IActionResult GetPresets() => Ok(PresetCurves.All);

    private static string RandomHex(int bytes)
    {
        Span<byte> buf = stackalloc byte[bytes];
        System.Security.Cryptography.RandomNumberGenerator.Fill(buf);
        return Convert.ToHexStringLower(buf);
    }
}
