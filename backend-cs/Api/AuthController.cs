using Microsoft.AspNetCore.Mvc;
using DriveChill.Models;
using DriveChill.Services;

namespace DriveChill.Api;

[ApiController]
[Route("api/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly ApiKeyService   _apiKeys;
    private readonly SessionService  _sessions;
    private readonly AppSettings     _settings;

    private const string SessionCookieName = "drivechill_session";
    private const string CsrfCookieName    = "drivechill_csrf";

    public AuthController(ApiKeyService apiKeys, SessionService sessions, AppSettings settings)
    {
        _apiKeys  = apiKeys;
        _sessions = sessions;
        _settings = settings;
    }

    // -----------------------------------------------------------------------
    // Session auth
    // -----------------------------------------------------------------------

    /// <summary>GET /api/auth/status — whether auth is enabled.</summary>
    [HttpGet("status")]
    public IActionResult GetAuthStatus() => Ok(new { auth_enabled = _settings.AuthRequired });

    /// <summary>GET /api/auth/session — current session state.</summary>
    [HttpGet("session")]
    public async Task<IActionResult> GetSession(CancellationToken ct = default)
    {
        if (!_settings.AuthRequired)
            return Ok(new { auth_required = false, authenticated = true });

        var token = Request.Cookies[SessionCookieName];
        var session = await _sessions.ValidateSessionAsync(token, ct);
        if (session == null)
            return Ok(new { auth_required = true, authenticated = false });

        return Ok(new { auth_required = true, authenticated = true, username = session.Value.Username });
    }

    /// <summary>POST /api/auth/setup — create initial admin user (first-time only).</summary>
    [HttpPost("setup")]
    public async Task<IActionResult> Setup([FromBody] LoginRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.Username) || req.Username.Length > 128)
            return BadRequest(new { error = "username must be 1-128 characters" });
        if (string.IsNullOrWhiteSpace(req.Password) || req.Password.Length < 8 || req.Password.Length > 256)
            return BadRequest(new { error = "password must be 8-256 characters" });

        var created = await _sessions.SetupAsync(req.Username.Trim(), req.Password, ct);
        if (!created)
            return Conflict(new { error = "Setup already completed. User exists." });

        // Auto-login after setup
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var ua = Request.Headers.UserAgent.ToString();
        var tokens = await _sessions.LoginAsync(req.Username.Trim(), req.Password, ip, ua, ct);
        if (tokens != null)
            SetSessionCookies(tokens.Value.SessionToken, tokens.Value.CsrfToken);

        return Ok(new { success = true, username = req.Username.Trim() });
    }

    /// <summary>POST /api/auth/login — authenticate and create session.</summary>
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest req, CancellationToken ct = default)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (!_sessions.CheckRateLimit(ip))
            return StatusCode(429, new { error = "Too many requests. Try again later." });

        if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
            return BadRequest(new { error = "username and password are required" });

        var ua = Request.Headers.UserAgent.ToString();
        var tokens = await _sessions.LoginAsync(req.Username.Trim(), req.Password, ip, ua, ct);
        if (tokens == null)
            return Unauthorized(new { error = "Invalid credentials or account locked" });

        SetSessionCookies(tokens.Value.SessionToken, tokens.Value.CsrfToken);
        return Ok(new { success = true, username = req.Username.Trim() });
    }

    /// <summary>POST /api/auth/logout — destroy session.</summary>
    [HttpPost("logout")]
    public async Task<IActionResult> Logout(CancellationToken ct = default)
    {
        var token = Request.Cookies[SessionCookieName];
        await _sessions.LogoutAsync(token, ct);
        Response.Cookies.Delete(SessionCookieName);
        Response.Cookies.Delete(CsrfCookieName);
        return Ok(new { success = true });
    }

    // -----------------------------------------------------------------------
    // API keys
    // -----------------------------------------------------------------------

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
        (ApiKeyRecord Metadata, string PlaintextKey) created;
        try
        {
            created = _apiKeys.Create(req.Name, req.Scopes);
        }
        catch (ArgumentException ex)
        {
            return UnprocessableEntity(new { error = ex.Message });
        }
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

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private bool IsSecureRequest()
    {
        // Match Python _is_secure_request(): also honour X-Forwarded-Proto
        // so that cookies get the Secure flag behind a TLS-terminating proxy.
        if (Request.IsHttps)
            return true;
        return string.Equals(
            Request.Headers["X-Forwarded-Proto"].FirstOrDefault(),
            "https",
            StringComparison.OrdinalIgnoreCase);
    }

    private void SetSessionCookies(string sessionToken, string csrfToken)
    {
        var secure = IsSecureRequest();
        var options = new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure   = secure,
            Path     = "/",
        };
        Response.Cookies.Append(SessionCookieName, sessionToken, options);
        // CSRF cookie is readable by JS (not HttpOnly)
        Response.Cookies.Append(CsrfCookieName, csrfToken, new CookieOptions
        {
            HttpOnly = false,
            SameSite = SameSiteMode.Strict,
            Secure   = secure,
            Path     = "/",
        });
    }
}

public sealed class LoginRequest
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
}
