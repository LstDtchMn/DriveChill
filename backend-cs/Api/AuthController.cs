using Microsoft.AspNetCore.Mvc;
using DriveChill.Models;
using DriveChill.Services;

namespace DriveChill.Api;

[ApiController]
[Route("api/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly ApiKeyService _apiKeys;

    public AuthController(ApiKeyService apiKeys)
    {
        _apiKeys = apiKeys;
    }

    [HttpGet("api-keys")]
    public IActionResult ListApiKeys()
    {
        return Ok(new { api_keys = _apiKeys.List() });
    }

    [HttpPost("api-keys")]
    public IActionResult CreateApiKey([FromBody] CreateApiKeyRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(new { error = "name is required" });
        var created = _apiKeys.Create(req.Name);
        return Ok(new
        {
            api_key = created.Metadata,
            plaintext_key = created.PlaintextKey,
        });
    }

    [HttpDelete("api-keys/{keyId}")]
    public IActionResult RevokeApiKey(string keyId)
    {
        if (!_apiKeys.Revoke(keyId))
            return NotFound(new { error = "API key not found" });
        return Ok(new { success = true });
    }
}
