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

    public ProfilesController(SettingsStore store, FanService fans)
    {
        _store = store;
        _fans  = fans;
    }

    /// <summary>GET /api/profiles</summary>
    [HttpGet]
    public IActionResult GetProfiles() => Ok(_store.LoadProfiles());

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

        // Apply all curves in the profile
        foreach (var curve in profile.Curves)
            _fans.SetCurve(curve);

        return Ok(profile);
    }

    /// <summary>GET /api/profiles/preset-curves — built-in curve templates.</summary>
    [HttpGet("preset-curves")]
    public IActionResult GetPresets() => Ok(PresetCurves.All);
}
