using Microsoft.AspNetCore.Mvc;
using DriveChill.Models;
using DriveChill.Services;

namespace DriveChill.Api;

[ApiController]
[Route("api/profiles")]
public sealed class ProfilesController : ControllerBase
{
    private readonly SettingsStore _store;
    private readonly FanService    _fans;
    private readonly AlertService  _alerts;

    public ProfilesController(SettingsStore store, FanService fans, AlertService alerts)
    {
        _store  = store;
        _fans   = fans;
        _alerts = alerts;
    }

    /// <summary>GET /api/profiles</summary>
    [HttpGet]
    public IActionResult GetProfiles() => Ok(_store.LoadProfiles());

    /// <summary>GET /api/profiles/{id}</summary>
    [HttpGet("{id}")]
    public IActionResult GetProfile(string id)
    {
        var profile = _store.LoadProfiles().FirstOrDefault(p => p.Id == id);
        return profile is not null ? Ok(profile) : NotFound(new { detail = "Profile not found" });
    }

    /// <summary>POST /api/profiles</summary>
    [HttpPost]
    public IActionResult CreateProfile([FromBody] CreateProfileRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(new { detail = "name is required" });

        var profile = new Profile
        {
            Name        = req.Name,
            Description = req.Description,
            Curves      = req.Curves,
        };

        var profiles = _store.LoadProfiles().ToList();
        profiles.Add(profile);
        _store.SaveProfiles(profiles);
        return CreatedAtAction(nameof(GetProfiles), new { id = profile.Id }, profile);
    }

    /// <summary>PUT /api/profiles/{id}</summary>
    [HttpPut("{id}")]
    public IActionResult UpdateProfile(string id, [FromBody] Profile updated)
    {
        var profiles = _store.LoadProfiles().ToList();
        var idx = profiles.FindIndex(p => p.Id == id);
        if (idx < 0) return NotFound(new { detail = "Profile not found" });

        updated.Id        = id;
        updated.CreatedAt = profiles[idx].CreatedAt;
        updated.UpdatedAt = DateTimeOffset.UtcNow;
        profiles[idx]     = updated;
        _store.SaveProfiles(profiles);
        return Ok(updated);
    }

    /// <summary>DELETE /api/profiles/{id}</summary>
    [HttpDelete("{id}")]
    public IActionResult DeleteProfile(string id)
    {
        var profiles = _store.LoadProfiles().ToList();
        var profile  = profiles.FirstOrDefault(p => p.Id == id);
        if (profile == null) return NotFound(new { detail = "Profile not found" });

        profiles.Remove(profile);
        _store.SaveProfiles(profiles);
        return Ok(new { ok = true });
    }

    /// <summary>PUT /api/profiles/{id}/activate — load curves into FanService.</summary>
    [HttpPut("{id}/activate")]
    public IActionResult ActivateProfile(string id)
    {
        var profiles = _store.LoadProfiles().ToList();
        var profile  = profiles.FirstOrDefault(p => p.Id == id);
        if (profile == null) return NotFound(new { detail = "Profile not found" });

        // Mark active flag
        foreach (var p in profiles) p.IsActive = p.Id == id;
        _store.SaveProfiles(profiles);

        // Replace all active curves atomically so orphaned curves from the
        // previous profile don't linger.
        _fans.SetCurves(profile.Curves);

        // Update alert service's pre-alert profile so future alert-triggered
        // switches know which profile to revert to.
        _alerts.SetPreAlertProfile(id);

        return Ok(profile);
    }

    /// <summary>GET /api/profiles/{id}/export — portable JSON snapshot, export_version: 1.</summary>
    [HttpGet("{id}/export")]
    public IActionResult ExportProfile(string id)
    {
        var profile = _store.LoadProfiles().FirstOrDefault(p => p.Id == id);
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

    /// <summary>POST /api/profiles/import — create a new profile from a portable snapshot.</summary>
    [HttpPost("import")]
    public IActionResult ImportProfile([FromBody] ImportProfileRequest req)
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

        var profiles = _store.LoadProfiles().ToList();
        profiles.Add(profile);
        _store.SaveProfiles(profiles);
        return StatusCode(201, new { success = true, profile });
    }

    /// <summary>GET /api/profiles/preset-curves — built-in curve templates.</summary>
    [HttpGet("preset-curves")]
    public IActionResult GetPresets() => Ok(PresetCurves.All);

    private static string RandomHex(int bytes)
    {
        Span<byte> buf = stackalloc byte[bytes];
        System.Security.Cryptography.RandomNumberGenerator.Fill(buf);
        return Convert.ToHexStringLower(buf);
    }
}
