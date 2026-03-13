using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using DriveChill.Models;
using DriveChill.Services;
using System.Security.Cryptography;

namespace DriveChill.Api;

[ApiController]
[Route("api/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly ApiKeyService   _apiKeys;
    private readonly SessionService  _sessions;
    private readonly AppSettings     _settings;
    private readonly DbService       _db;
    private readonly ILogger<AuthController> _logger;

    private const string SessionCookieName = "drivechill_session";
    private const string CsrfCookieName    = "drivechill_csrf";

    public AuthController(ApiKeyService apiKeys, SessionService sessions, AppSettings settings, DbService db,
                          ILogger<AuthController> logger)
    {
        _apiKeys  = apiKeys;
        _sessions = sessions;
        _settings = settings;
        _db       = db;
        _logger   = logger;
    }

    /// <summary>Log an auth event without blocking the request. Failures are logged, not swallowed.</summary>
    private void LogAuthEvent(string action, string? ip, string? username, string outcome, string? detail = null)
    {
        _ = Task.Run(async () =>
        {
            try { await _db.LogAuthEventAsync(action, ip, username, outcome, detail); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to log auth event {Action} for {User}", action, username); }
        });
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

        return Ok(new { auth_required = true, authenticated = true, username = session.Value.Username, role = session.Value.Role });
    }

    /// <summary>POST /api/auth/setup — create initial admin user (first-time only).</summary>
    [HttpPost("setup")]
    public async Task<IActionResult> Setup([FromBody] LoginRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.Username) || req.Username.Length > 128)
            return BadRequest(new { detail = "username must be 1-128 characters" });
        if (string.IsNullOrWhiteSpace(req.Password) || req.Password.Length < 8 || req.Password.Length > 256)
            return BadRequest(new { detail = "password must be 8-256 characters" });

        var created = await _sessions.SetupAsync(req.Username.Trim(), req.Password, ct);
        if (!created)
            return Conflict(new { detail = "Setup already completed. User exists." });

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
            return StatusCode(429, new { detail = "Too many requests. Try again later." });

        if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
            return BadRequest(new { detail = "username and password are required" });

        var ua = Request.Headers.UserAgent.ToString();
        var tokens = await _sessions.LoginAsync(req.Username.Trim(), req.Password, ip, ua, ct);
        if (tokens == null)
            return Unauthorized(new { detail = "Invalid credentials or account locked" });

        SetSessionCookies(tokens.Value.SessionToken, tokens.Value.CsrfToken);
        return Ok(new { success = true, username = req.Username.Trim() });
    }

    /// <summary>POST /api/auth/logout — destroy session.</summary>
    [HttpPost("logout")]
    public async Task<IActionResult> Logout(CancellationToken ct = default)
    {
        var token = Request.Cookies[SessionCookieName];
        var session = await _sessions.ValidateSessionAsync(token, ct);
        await _sessions.LogoutAsync(token, ct);
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        LogAuthEvent("logout", ip, session?.Username, "success");
        Response.Cookies.Delete(SessionCookieName);
        Response.Cookies.Delete(CsrfCookieName);
        return Ok(new { success = true });
    }

    // -----------------------------------------------------------------------
    // User management (admin only)
    // -----------------------------------------------------------------------

    /// <summary>GET /api/auth/users — list all users.</summary>
    [HttpGet("users")]
    public async Task<IActionResult> ListUsers(CancellationToken ct = default)
    {
        if (!await IsAdminSessionAsync(ct)) return Forbid403("Admin role required");
        var users = await _db.ListUsersAsync(ct);
        return Ok(new { users = users.Select(u => new { id = u.Id, username = u.Username, role = u.Role, created_at = u.CreatedAt }) });
    }

    /// <summary>POST /api/auth/users — create a user.</summary>
    [HttpPost("users")]
    public async Task<IActionResult> CreateUser([FromBody] CreateUserRequest req, CancellationToken ct = default)
    {
        if (!await IsAdminSessionAsync(ct)) return Forbid403("Admin role required");
        if (string.IsNullOrWhiteSpace(req.Username) || req.Username.Length > 128)
            return BadRequest(new { detail = "username must be 1-128 characters" });
        if (string.IsNullOrWhiteSpace(req.Password) || req.Password.Length < 8 || req.Password.Length > 256)
            return BadRequest(new { detail = "password must be 8-256 characters" });
        if (req.Role != "admin" && req.Role != "viewer")
            return BadRequest(new { detail = "role must be 'admin' or 'viewer'" });

        var hash = HashPasswordInternal(req.Password);
        long userId;
        try
        {
            userId = await _db.CreateUserAsync(req.Username.Trim(), hash, req.Role, ct);
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            return Conflict(new { detail = "Username already exists" });
        }
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        LogAuthEvent("user_created", ip, req.Username.Trim(), "success", $"role={req.Role}");
        return Ok(new { success = true, user_id = userId, username = req.Username.Trim(), role = req.Role });
    }

    /// <summary>PUT /api/auth/users/{userId}/role — change role.</summary>
    [HttpPut("users/{userId:long}/role")]
    public async Task<IActionResult> SetUserRole(long userId, [FromBody] SetRoleRequest req, CancellationToken ct = default)
    {
        if (!await IsAdminSessionAsync(ct)) return Forbid403("Admin role required");
        if (req.Role != "admin" && req.Role != "viewer")
            return BadRequest(new { detail = "role must be 'admin' or 'viewer'" });

        // Atomic last-admin demotion guard to prevent TOCTOU race.
        if (req.Role == "viewer")
        {
            var demoted = await _db.DemoteUserIfNotLastAdminAsync(userId, ct);
            if (!demoted)
            {
                // Check if user exists to distinguish "not found" from "last admin"
                var user = await _db.GetUserByIdAsync(userId, ct);
                if (user is null) return NotFound(new { detail = "User not found" });
                return Conflict(new { detail = "Cannot demote the last admin user" });
            }
            var ip2 = HttpContext.Connection.RemoteIpAddress?.ToString();
            LogAuthEvent("user_role_changed", ip2, null, "success", $"user_id={userId} role=viewer");
            return Ok(new { success = true });
        }

        var updated = await _db.SetUserRoleAsync(userId, req.Role, ct);
        if (!updated) return NotFound(new { detail = "User not found" });
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        LogAuthEvent("user_role_changed", ip, null, "success", $"user_id={userId} role={req.Role}");
        return Ok(new { success = true });
    }

    /// <summary>PUT /api/auth/users/{userId}/password — change password.</summary>
    [HttpPut("users/{userId:long}/password")]
    public async Task<IActionResult> ChangeUserPassword(long userId, [FromBody] ChangePasswordRequest req, CancellationToken ct = default)
    {
        if (!await IsAdminSessionAsync(ct)) return Forbid403("Admin role required");
        if (string.IsNullOrWhiteSpace(req.Password) || req.Password.Length < 8 || req.Password.Length > 256)
            return BadRequest(new { detail = "password must be 8-256 characters" });
        var user = await _db.GetUserByIdAsync(userId, ct);
        if (user is null) return NotFound(new { detail = "User not found" });
        var hash = HashPasswordInternal(req.Password);
        await _db.SetUserPasswordAsync(userId, hash, ct);
        // Invalidate all existing sessions — a stolen session cannot persist after
        // an admin resets the password (GAP-2).
        await _db.DeleteUserSessionsByUsernameAsync(user.Username, ct);
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        LogAuthEvent("password_changed", ip, user.Username, "success", $"user_id={userId}");
        return Ok(new { success = true });
    }

    /// <summary>DELETE /api/auth/users/{userId} — delete a user.</summary>
    [HttpDelete("users/{userId:long}")]
    public async Task<IActionResult> DeleteUser(long userId, CancellationToken ct = default)
    {
        if (!await IsAdminSessionAsync(ct)) return Forbid403("Admin role required");
        var user = await _db.GetUserByIdAsync(userId, ct);
        if (user is null) return NotFound(new { detail = "User not found" });
        // Atomic last-admin guard: delete only if not the sole admin.
        // Prevents TOCTOU race between CountAdminUsersAsync and DeleteUserAsync.
        var deleted = user.Role == "admin"
            ? await _db.DeleteUserIfNotLastAdminAsync(userId, ct)
            : await _db.DeleteUserAsync(userId, ct);
        if (!deleted)
            return Conflict(new { detail = "Cannot delete the last admin user" });
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        LogAuthEvent("user_deleted", ip, user.Username, "success", $"user_id={userId}");
        return Ok(new { success = true });
    }

    // -----------------------------------------------------------------------
    // Self-service password change
    // -----------------------------------------------------------------------

    /// <summary>POST /api/auth/me/password — change own password with current-password verification.</summary>
    [HttpPost("me/password")]
    public async Task<IActionResult> ChangeMyPassword([FromBody] SelfPasswordChangeRequest req, CancellationToken ct = default)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (!_sessions.CheckRateLimit(ip))
            return StatusCode(429, new { detail = "Too many requests. Try again later." });

        var sessionToken = Request.Cookies[SessionCookieName];
        if (string.IsNullOrEmpty(sessionToken))
            return Unauthorized(new { detail = "Not authenticated" });

        var session = await _sessions.ValidateSessionAsync(sessionToken, ct);
        if (session is null)
            return Unauthorized(new { detail = "Invalid session" });

        if (string.IsNullOrWhiteSpace(req.CurrentPassword))
            return BadRequest(new { detail = "Current password is required" });

        if (string.IsNullOrWhiteSpace(req.NewPassword) || req.NewPassword.Length < 8 || req.NewPassword.Length > 256)
            return BadRequest(new { detail = "New password must be 8-256 characters" });

        var user = await _db.GetUserAsync(session.Value.Username, ct);
        if (user is null)
            return NotFound(new { detail = "User not found" });

        if (!_sessions.VerifyPasswordPublic(req.CurrentPassword, user.Value.PasswordHash))
            return StatusCode(403, new { detail = "Current password is incorrect" });

        var newHash = SessionService.HashPassword(req.NewPassword);
        await _db.SetUserPasswordByUsernameAsync(session.Value.Username, newHash, ct);

        // Invalidate all existing sessions then issue a fresh one (session rotation).
        await _db.DeleteUserSessionsByUsernameAsync(session.Value.Username, ct);

        var (newSessionToken, newCsrfToken) = await _sessions.CreateSessionDirectAsync(
            session.Value.Username, ip, Request.Headers.UserAgent.ToString(), ct);

        LogAuthEvent("self_password_changed", ip, session.Value.Username, "success");

        SetSessionCookies(newSessionToken, newCsrfToken);
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
    public async Task<IActionResult> CreateApiKey([FromBody] CreateApiKeyRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(new { detail = "name is required" });

        // Determine caller's role so the key's effective role can be capped.
        var callerRole = "admin";
        var callerUsername = (string?)null;
        if (_settings.AuthRequired)
        {
            var token = Request.Cookies[SessionCookieName];
            var session = await _sessions.ValidateSessionAsync(token, ct);
            if (session is not null)
            {
                callerRole = session.Value.Role;
                callerUsername = session.Value.Username;
            }
        }

        (ApiKeyRecord Metadata, string PlaintextKey) created;
        try
        {
            created = _apiKeys.Create(req.Name, req.Scopes,
                requestingRole: callerRole, createdByUsername: callerUsername);
        }
        catch (ArgumentException ex)
        {
            return UnprocessableEntity(new { detail = ex.Message });
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
            return NotFound(new { detail = "API key not found" });
        return Ok(new { success = true });
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private async Task<bool> IsAdminSessionAsync(CancellationToken ct)
    {
        if (!_settings.AuthRequired) return true;
        var token = Request.Cookies[SessionCookieName];
        var session = await _sessions.ValidateSessionAsync(token, ct);
        return session is not null && session.Value.Role == "admin";
    }

    private IActionResult Forbid403(string detail) =>
        StatusCode(403, new { detail });

    private static string HashPasswordInternal(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);
        return $"{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
    }

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

public sealed class SelfPasswordChangeRequest
{
    public string CurrentPassword { get; set; } = "";
    public string NewPassword     { get; set; } = "";
}
