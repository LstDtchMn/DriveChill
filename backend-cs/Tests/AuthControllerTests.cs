using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using DriveChill.Api;
using DriveChill.Models;
using DriveChill.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DriveChill.Tests;

public sealed class AuthControllerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DbService _db;
    private readonly SessionService _sessions;
    private readonly ApiKeyService _apiKeys;
    private readonly SettingsStore _store;
    private readonly AppSettings _settings;
    private readonly AuthController _ctrl;

    public AuthControllerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
        Environment.SetEnvironmentVariable("DRIVECHILL_DATA_DIR", _tempDir);
        Environment.SetEnvironmentVariable("DRIVECHILL_FORCE_AUTH", "true");

        _settings = new AppSettings();
        _db       = new DbService(_settings, NullLogger<DbService>.Instance);
        _sessions = new SessionService(_db, _settings);
        _store    = new SettingsStore(_settings);
        _apiKeys  = new ApiKeyService(_store);
        _ctrl     = new AuthController(_apiKeys, _sessions, _settings, _db,
                        NullLogger<AuthController>.Instance);

        // Give the controller a default HttpContext (no cookies, loopback IP).
        _ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = CreateHttpContext()
        };
    }

    public void Dispose()
    {
        _db.Dispose();
        Environment.SetEnvironmentVariable("DRIVECHILL_DATA_DIR", null);
        Environment.SetEnvironmentVariable("DRIVECHILL_FORCE_AUTH", null);
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static DefaultHttpContext CreateHttpContext(string? sessionCookie = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
        if (sessionCookie != null)
            ctx.Request.Headers.Cookie = $"drivechill_session={sessionCookie}";
        return ctx;
    }

    private void SetSessionCookie(string token) =>
        _ctrl.ControllerContext.HttpContext = CreateHttpContext(token);

    private async Task<string> SetupAndLogin(string username = "admin", string password = "Password123!")
    {
        await _sessions.SetupAsync(username, password);
        var tokens = await _sessions.LoginAsync(username, password, "127.0.0.1", "test-agent");
        Assert.NotNull(tokens);
        SetSessionCookie(tokens!.Value.SessionToken);
        return tokens.Value.SessionToken;
    }

    private static T GetJsonProp<T>(IActionResult result, string prop)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value);
        using var doc = JsonDocument.Parse(json);
        if (typeof(T) == typeof(bool))
            return (T)(object)doc.RootElement.GetProperty(prop).GetBoolean();
        if (typeof(T) == typeof(string))
            return (T)(object)(doc.RootElement.GetProperty(prop).GetString() ?? "");
        if (typeof(T) == typeof(int))
            return (T)(object)doc.RootElement.GetProperty(prop).GetInt32();
        throw new NotSupportedException($"GetJsonProp<{typeof(T).Name}> not supported");
    }

    private static int GetStatusCode(IActionResult result) => result switch
    {
        OkObjectResult ok => ok.StatusCode ?? 200,
        ObjectResult obj => obj.StatusCode ?? 500,
        _ => 200,
    };

    // -----------------------------------------------------------------------
    // Setup
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Setup_CreatesAdminUser_AndAutoLogins()
    {
        var result = await _ctrl.Setup(new LoginRequest { Username = "admin", Password = "Password123!" });
        Assert.IsType<OkObjectResult>(result);
        Assert.True(GetJsonProp<bool>(result, "success"));
    }

    [Fact]
    public async Task Setup_ReturnsConflict_WhenAlreadySetup()
    {
        await _sessions.SetupAsync("admin", "Password123!");
        var result = await _ctrl.Setup(new LoginRequest { Username = "admin", Password = "Password123!" });
        Assert.IsType<ConflictObjectResult>(result);
    }

    [Fact]
    public async Task Setup_RejectsBadUsername()
    {
        var result = await _ctrl.Setup(new LoginRequest { Username = "", Password = "Password123!" });
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Setup_RejectsShortPassword()
    {
        var result = await _ctrl.Setup(new LoginRequest { Username = "admin", Password = "short" });
        Assert.IsType<BadRequestObjectResult>(result);
    }

    // -----------------------------------------------------------------------
    // Login
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Login_Success_ReturnsOk()
    {
        await _sessions.SetupAsync("admin", "Password123!");
        var result = await _ctrl.Login(new LoginRequest { Username = "admin", Password = "Password123!" });
        Assert.IsType<OkObjectResult>(result);
        Assert.True(GetJsonProp<bool>(result, "success"));
    }

    [Fact]
    public async Task Login_WrongPassword_ReturnsUnauthorized()
    {
        await _sessions.SetupAsync("admin", "Password123!");
        var result = await _ctrl.Login(new LoginRequest { Username = "admin", Password = "WrongPass!" });
        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public async Task Login_MissingUser_ReturnsUnauthorized()
    {
        var result = await _ctrl.Login(new LoginRequest { Username = "nobody", Password = "Password123!" });
        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public async Task Login_EmptyCredentials_ReturnsBadRequest()
    {
        var result = await _ctrl.Login(new LoginRequest { Username = "", Password = "" });
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Login_RateLimited_Returns429()
    {
        await _sessions.SetupAsync("admin", "Password123!");

        // Exhaust rate limit (10 per minute)
        for (int i = 0; i < 10; i++)
            _sessions.CheckRateLimit("127.0.0.1");

        var result = await _ctrl.Login(new LoginRequest { Username = "admin", Password = "Password123!" });
        Assert.Equal(429, GetStatusCode(result));
    }

    // -----------------------------------------------------------------------
    // Logout
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Logout_InvalidatesSession()
    {
        var token = await SetupAndLogin();
        var result = await _ctrl.Logout();
        Assert.IsType<OkObjectResult>(result);

        // Session should be invalid now
        var session = await _sessions.ValidateSessionAsync(token);
        Assert.Null(session);
    }

    // -----------------------------------------------------------------------
    // Session
    // -----------------------------------------------------------------------

    [Fact]
    public async Task GetSession_Authenticated_ReturnsUserInfo()
    {
        await SetupAndLogin();
        var result = await _ctrl.GetSession();
        Assert.IsType<OkObjectResult>(result);
        Assert.True(GetJsonProp<bool>(result, "authenticated"));
        Assert.Equal("admin", GetJsonProp<string>(result, "username"));
        Assert.Equal("admin", GetJsonProp<string>(result, "role"));
    }

    [Fact]
    public async Task GetSession_NoSession_ReturnsUnauthenticated()
    {
        await _sessions.SetupAsync("admin", "Password123!");
        // No session cookie set
        var result = await _ctrl.GetSession();
        Assert.IsType<OkObjectResult>(result);
        Assert.False(GetJsonProp<bool>(result, "authenticated"));
    }

    // -----------------------------------------------------------------------
    // User Management (admin-only)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ListUsers_AsAdmin_ReturnsUsers()
    {
        await SetupAndLogin();
        var result = await _ctrl.ListUsers();
        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task ListUsers_AsViewer_Returns403()
    {
        // Setup admin, create viewer, login as viewer
        await SetupAndLogin();
        await _db.CreateUserAsync("viewer1", SessionService.HashPassword("Password123!"), "viewer");
        var viewerTokens = await _sessions.LoginAsync("viewer1", "Password123!", "127.0.0.1", "ua");
        Assert.NotNull(viewerTokens);
        SetSessionCookie(viewerTokens!.Value.SessionToken);

        var result = await _ctrl.ListUsers();
        Assert.Equal(403, GetStatusCode(result));
    }

    [Fact]
    public async Task CreateUser_AsAdmin_Succeeds()
    {
        await SetupAndLogin();
        var result = await _ctrl.CreateUser(new CreateUserRequest
        {
            Username = "newuser",
            Password = "Password123!",
            Role = "viewer",
        });
        Assert.IsType<OkObjectResult>(result);
        Assert.True(GetJsonProp<bool>(result, "success"));
    }

    [Fact]
    public async Task CreateUser_DuplicateUsername_ReturnsConflict()
    {
        await SetupAndLogin();
        await _db.CreateUserAsync("dup", SessionService.HashPassword("Password123!"), "admin");

        var result = await _ctrl.CreateUser(new CreateUserRequest
        {
            Username = "dup",
            Password = "Password123!",
            Role = "admin",
        });
        Assert.IsType<ConflictObjectResult>(result);
    }

    [Fact]
    public async Task CreateUser_InvalidRole_ReturnsBadRequest()
    {
        await SetupAndLogin();
        var result = await _ctrl.CreateUser(new CreateUserRequest
        {
            Username = "user2",
            Password = "Password123!",
            Role = "superadmin",
        });
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task CreateUser_ShortPassword_ReturnsBadRequest()
    {
        await SetupAndLogin();
        var result = await _ctrl.CreateUser(new CreateUserRequest
        {
            Username = "user2",
            Password = "short",
            Role = "admin",
        });
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task CreateUser_AsViewer_Returns403()
    {
        await SetupAndLogin();
        await _db.CreateUserAsync("viewer1", SessionService.HashPassword("Password123!"), "viewer");
        var vt = await _sessions.LoginAsync("viewer1", "Password123!", "127.0.0.1", "ua");
        SetSessionCookie(vt!.Value.SessionToken);

        var result = await _ctrl.CreateUser(new CreateUserRequest
        {
            Username = "user3",
            Password = "Password123!",
            Role = "admin",
        });
        Assert.Equal(403, GetStatusCode(result));
    }

    // -----------------------------------------------------------------------
    // Role changes
    // -----------------------------------------------------------------------

    [Fact]
    public async Task SetUserRole_AsAdmin_Succeeds()
    {
        await SetupAndLogin();
        await _db.CreateUserAsync("user2", SessionService.HashPassword("Password123!"), "admin");
        var users = await _db.ListUsersAsync();
        var user2 = users.Find(u => u.Username == "user2");
        Assert.NotNull(user2);

        var result = await _ctrl.SetUserRole(user2!.Id, new SetRoleRequest { Role = "viewer" });
        Assert.IsType<OkObjectResult>(result);

        // Verify role changed in DB
        var updated = await _db.GetUserByIdAsync(user2.Id);
        Assert.Equal("viewer", updated!.Role);
    }

    [Fact]
    public async Task SetUserRole_LastAdmin_ReturnsConflict()
    {
        await SetupAndLogin();
        var users = await _db.ListUsersAsync();
        var admin = users[0];

        // Try to demote the only admin
        var result = await _ctrl.SetUserRole(admin.Id, new SetRoleRequest { Role = "viewer" });
        Assert.IsType<ConflictObjectResult>(result);
    }

    [Fact]
    public async Task SetUserRole_InvalidRole_ReturnsBadRequest()
    {
        await SetupAndLogin();
        var result = await _ctrl.SetUserRole(1, new SetRoleRequest { Role = "god" });
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task SetUserRole_UnknownUser_ReturnsNotFound()
    {
        await SetupAndLogin();
        var result = await _ctrl.SetUserRole(9999, new SetRoleRequest { Role = "viewer" });
        Assert.IsType<NotFoundObjectResult>(result);
    }

    // -----------------------------------------------------------------------
    // Password changes (admin resetting another user)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ChangeUserPassword_InvalidatesExistingSessions()
    {
        await SetupAndLogin();
        await _db.CreateUserAsync("user2", SessionService.HashPassword("OldPass123!"), "viewer");
        var user2Tokens = await _sessions.LoginAsync("user2", "OldPass123!", "127.0.0.1", "ua");
        Assert.NotNull(user2Tokens);

        var users = await _db.ListUsersAsync();
        var user2 = users.Find(u => u.Username == "user2");

        var result = await _ctrl.ChangeUserPassword(user2!.Id, new ChangePasswordRequest { Password = "NewPass123!" });
        Assert.IsType<OkObjectResult>(result);

        // Old session should be invalidated
        var session = await _sessions.ValidateSessionAsync(user2Tokens!.Value.SessionToken);
        Assert.Null(session);

        // New password should work
        var newTokens = await _sessions.LoginAsync("user2", "NewPass123!", "127.0.0.1", "ua");
        Assert.NotNull(newTokens);
    }

    [Fact]
    public async Task ChangeUserPassword_ShortPassword_ReturnsBadRequest()
    {
        await SetupAndLogin();
        var result = await _ctrl.ChangeUserPassword(1, new ChangePasswordRequest { Password = "short" });
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task ChangeUserPassword_UnknownUser_ReturnsNotFound()
    {
        await SetupAndLogin();
        var result = await _ctrl.ChangeUserPassword(9999, new ChangePasswordRequest { Password = "NewPass123!" });
        Assert.IsType<NotFoundObjectResult>(result);
    }

    // -----------------------------------------------------------------------
    // Self-service password change
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ChangeMyPassword_Success_RotatesSession()
    {
        var oldToken = await SetupAndLogin();

        var result = await _ctrl.ChangeMyPassword(new SelfPasswordChangeRequest
        {
            CurrentPassword = "Password123!",
            NewPassword = "NewPassword123!",
        });
        Assert.IsType<OkObjectResult>(result);

        // Old session should be invalidated
        var oldSession = await _sessions.ValidateSessionAsync(oldToken);
        Assert.Null(oldSession);

        // A new session cookie should have been set (response cookies)
        // New password should work for login
        var newTokens = await _sessions.LoginAsync("admin", "NewPassword123!", "127.0.0.1", "ua");
        Assert.NotNull(newTokens);
    }

    [Fact]
    public async Task ChangeMyPassword_WrongCurrentPassword_Returns403()
    {
        await SetupAndLogin();
        var result = await _ctrl.ChangeMyPassword(new SelfPasswordChangeRequest
        {
            CurrentPassword = "WrongPass!",
            NewPassword = "NewPassword123!",
        });
        Assert.Equal(403, GetStatusCode(result));
    }

    [Fact]
    public async Task ChangeMyPassword_ShortNewPassword_ReturnsBadRequest()
    {
        await SetupAndLogin();
        var result = await _ctrl.ChangeMyPassword(new SelfPasswordChangeRequest
        {
            CurrentPassword = "Password123!",
            NewPassword = "short",
        });
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task ChangeMyPassword_EmptyCurrentPassword_ReturnsBadRequest()
    {
        await SetupAndLogin();
        var result = await _ctrl.ChangeMyPassword(new SelfPasswordChangeRequest
        {
            CurrentPassword = "",
            NewPassword = "NewPassword123!",
        });
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task ChangeMyPassword_NoSession_ReturnsUnauthorized()
    {
        // No session cookie
        var result = await _ctrl.ChangeMyPassword(new SelfPasswordChangeRequest
        {
            CurrentPassword = "Password123!",
            NewPassword = "NewPassword123!",
        });
        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    // -----------------------------------------------------------------------
    // Delete user
    // -----------------------------------------------------------------------

    [Fact]
    public async Task DeleteUser_Succeeds()
    {
        await SetupAndLogin();
        await _db.CreateUserAsync("toDelete", SessionService.HashPassword("Password123!"), "viewer");
        var users = await _db.ListUsersAsync();
        var target = users.Find(u => u.Username == "toDelete");

        var result = await _ctrl.DeleteUser(target!.Id);
        Assert.IsType<OkObjectResult>(result);

        // User should be gone
        var afterDelete = await _db.GetUserByIdAsync(target.Id);
        Assert.Null(afterDelete);
    }

    [Fact]
    public async Task DeleteUser_LastAdmin_ReturnsConflict()
    {
        await SetupAndLogin();
        var users = await _db.ListUsersAsync();
        var admin = users[0];

        var result = await _ctrl.DeleteUser(admin.Id);
        Assert.IsType<ConflictObjectResult>(result);
    }

    [Fact]
    public async Task DeleteUser_UnknownUser_ReturnsNotFound()
    {
        await SetupAndLogin();
        var result = await _ctrl.DeleteUser(9999);
        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task DeleteUser_InvalidatesTheirSessions()
    {
        await SetupAndLogin();
        await _db.CreateUserAsync("user2", SessionService.HashPassword("Password123!"), "viewer");
        var u2Tokens = await _sessions.LoginAsync("user2", "Password123!", "127.0.0.1", "ua");
        Assert.NotNull(u2Tokens);

        // Validate session works before delete
        var before = await _sessions.ValidateSessionAsync(u2Tokens!.Value.SessionToken);
        Assert.NotNull(before);

        var users = await _db.ListUsersAsync();
        var target = users.Find(u => u.Username == "user2");
        await _ctrl.DeleteUser(target!.Id);

        // Session should be invalid after user deletion (INNER JOIN)
        var after = await _sessions.ValidateSessionAsync(u2Tokens.Value.SessionToken);
        Assert.Null(after);
    }

    // -----------------------------------------------------------------------
    // API Keys
    // -----------------------------------------------------------------------

    [Fact]
    public async Task CreateApiKey_ReturnsKeyWithPlaintext()
    {
        await SetupAndLogin();
        var result = await _ctrl.CreateApiKey(new CreateApiKeyRequest { Name = "Test Key" });
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("plaintext_key", out var keyEl));
        Assert.StartsWith("dc_live_", keyEl.GetString());
    }

    [Fact]
    public async Task CreateApiKey_EmptyName_ReturnsBadRequest()
    {
        await SetupAndLogin();
        var result = await _ctrl.CreateApiKey(new CreateApiKeyRequest { Name = "" });
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task RevokeApiKey_Success()
    {
        await SetupAndLogin();
        var createResult = await _ctrl.CreateApiKey(new CreateApiKeyRequest { Name = "ToRevoke" });
        var ok = Assert.IsType<OkObjectResult>(createResult);
        var json = JsonSerializer.Serialize(ok.Value);
        using var doc = JsonDocument.Parse(json);
        var keyId = doc.RootElement.GetProperty("api_key").GetProperty("Id").GetString();

        var revokeResult = _ctrl.RevokeApiKey(keyId!);
        Assert.IsType<OkObjectResult>(revokeResult);
    }

    [Fact]
    public void RevokeApiKey_UnknownId_ReturnsNotFound()
    {
        var result = _ctrl.RevokeApiKey("nonexistent-id");
        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task ListApiKeys_ReturnsCreatedKeys()
    {
        await SetupAndLogin();
        await _ctrl.CreateApiKey(new CreateApiKeyRequest { Name = "Key1" });
        await _ctrl.CreateApiKey(new CreateApiKeyRequest { Name = "Key2" });

        var result = _ctrl.ListApiKeys();
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value);
        using var doc = JsonDocument.Parse(json);
        var keys = doc.RootElement.GetProperty("api_keys");
        Assert.True(keys.GetArrayLength() >= 2);
    }

    // -----------------------------------------------------------------------
    // API Key scope enforcement (service-level)
    // -----------------------------------------------------------------------

    [Fact]
    public void ApiKey_Validate_ReturnsRecord_ForValidKey()
    {
        var (_, plaintext) = _apiKeys.Create("Valid Key");
        var record = _apiKeys.Validate(plaintext);
        Assert.NotNull(record);
        Assert.Equal("Valid Key", record!.Name);
    }

    [Fact]
    public void ApiKey_Validate_ReturnsNull_ForRevokedKey()
    {
        var (meta, plaintext) = _apiKeys.Create("Revoked Key");
        _apiKeys.Revoke(meta.Id);
        var record = _apiKeys.Validate(plaintext);
        Assert.Null(record);
    }

    [Fact]
    public void ApiKey_Validate_ReturnsNull_ForInvalidKey()
    {
        var record = _apiKeys.Validate("dc_live_totallyinvalid");
        Assert.Null(record);
    }

    [Fact]
    public void ApiKey_ViewerRole_CapsKeyRole()
    {
        var (meta, _) = _apiKeys.Create("Viewer Key", requestingRole: "viewer");
        Assert.Equal("viewer", meta.Role);
    }

    [Fact]
    public void ApiKey_AdminRole_GetsAdminKey()
    {
        var (meta, _) = _apiKeys.Create("Admin Key", requestingRole: "admin");
        Assert.Equal("admin", meta.Role);
    }

    [Fact]
    public void ApiKey_InvalidScope_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            _apiKeys.Create("Bad Scope", scopes: new[] { "read:nonexistent_scope" }));
    }

    // -----------------------------------------------------------------------
    // RBAC — last-admin guard (service-level)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task LastAdminGuard_DemotionBlocked()
    {
        await SetupAndLogin();
        var users = await _db.ListUsersAsync();
        Assert.Single(users);
        Assert.Equal("admin", users[0].Role);

        // Cannot demote the only admin
        var result = await _ctrl.SetUserRole(users[0].Id, new SetRoleRequest { Role = "viewer" });
        Assert.IsType<ConflictObjectResult>(result);
    }

    [Fact]
    public async Task LastAdminGuard_DeletionBlocked()
    {
        await SetupAndLogin();
        var users = await _db.ListUsersAsync();
        var result = await _ctrl.DeleteUser(users[0].Id);
        Assert.IsType<ConflictObjectResult>(result);
    }

    [Fact]
    public async Task LastAdminGuard_AllowsDemotionWithMultipleAdmins()
    {
        await SetupAndLogin();
        await _db.CreateUserAsync("admin2", SessionService.HashPassword("Password123!"), "admin");
        var users = await _db.ListUsersAsync();
        var admin2 = users.Find(u => u.Username == "admin2");

        var result = await _ctrl.SetUserRole(admin2!.Id, new SetRoleRequest { Role = "viewer" });
        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task LastAdminGuard_AllowsDeletionWithMultipleAdmins()
    {
        await SetupAndLogin();
        await _db.CreateUserAsync("admin2", SessionService.HashPassword("Password123!"), "admin");
        var users = await _db.ListUsersAsync();
        var admin2 = users.Find(u => u.Username == "admin2");

        var result = await _ctrl.DeleteUser(admin2!.Id);
        Assert.IsType<OkObjectResult>(result);
    }

    // -----------------------------------------------------------------------
    // Auth status
    // -----------------------------------------------------------------------

    [Fact]
    public void GetAuthStatus_ReturnsAuthEnabled()
    {
        var result = _ctrl.GetAuthStatus();
        Assert.IsType<OkObjectResult>(result);
        // With DRIVECHILL_FORCE_AUTH=true, auth_enabled should be true
        Assert.True(GetJsonProp<bool>(result, "auth_enabled"));
    }
}
