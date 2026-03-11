using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using DriveChill.Api;
using DriveChill.Models;
using DriveChill.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DriveChill.Tests;

public sealed class NotificationsControllerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AppSettings _settings;
    private readonly DbService _db;
    private readonly NotificationsController _ctrl;

    public NotificationsControllerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
        Environment.SetEnvironmentVariable("DRIVECHILL_DATA_DIR", _tempDir);

        _settings = new AppSettings();
        _db = new DbService(_settings, NullLogger<DbService>.Instance);

        // Email and push services — push has no VAPID keys, so it's a no-op client.
        var emailSvc = new EmailNotificationService(_db, NullLogger<EmailNotificationService>.Instance);
        var pushSvc = new PushNotificationService(_db, _settings, NullLogger<PushNotificationService>.Instance);
        _ctrl = new NotificationsController(_db, emailSvc, pushSvc);
    }

    public void Dispose()
    {
        _db.Dispose();
        Environment.SetEnvironmentVariable("DRIVECHILL_DATA_DIR", null);
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // -----------------------------------------------------------------------
    // GET /api/notifications/email — defaults
    // -----------------------------------------------------------------------

    [Fact]
    public async Task GetEmailSettings_ReturnsDefaults()
    {
        var result = await _ctrl.GetEmailSettings(default);
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value);
        Assert.Contains("\"enabled\":false", json);
        Assert.Contains("\"smtp_port\":587", json);
        Assert.Contains("\"has_password\":false", json);
    }

    // -----------------------------------------------------------------------
    // PUT /api/notifications/email — update
    // -----------------------------------------------------------------------

    [Fact]
    public async Task UpdateEmailSettings_ReturnsUpdatedValues()
    {
        var body = JsonSerializer.Deserialize<JsonElement>("""
        {
            "enabled": true,
            "smtp_host": "smtp.example.com",
            "smtp_port": 465,
            "smtp_username": "user@example.com",
            "smtp_password": "s3cret",
            "sender_address": "noreply@example.com",
            "recipient_list": ["admin@example.com"],
            "use_tls": true,
            "use_ssl": false
        }
        """);

        var result = await _ctrl.UpdateEmailSettings(body, default);
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value);
        Assert.Contains("\"success\":true", json);
        Assert.Contains("\"smtp_host\":\"smtp.example.com\"", json);
        Assert.Contains("\"smtp_port\":465", json);
        Assert.Contains("\"has_password\":true", json);
        Assert.Contains("\"enabled\":true", json);
    }

    [Fact]
    public async Task UpdateEmailSettings_ValidatesSmtpPortRange_TooHigh()
    {
        var body = JsonSerializer.Deserialize<JsonElement>("""{ "smtp_port": 70000 }""");
        var result = await _ctrl.UpdateEmailSettings(body, default);
        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var json = JsonSerializer.Serialize(bad.Value);
        Assert.Contains("smtp_port", json);
    }

    [Fact]
    public async Task UpdateEmailSettings_ValidatesSmtpPortRange_Zero()
    {
        var body = JsonSerializer.Deserialize<JsonElement>("""{ "smtp_port": 0 }""");
        var result = await _ctrl.UpdateEmailSettings(body, default);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task UpdateEmailSettings_ValidatesBooleanEnabled()
    {
        var body = JsonSerializer.Deserialize<JsonElement>("""{ "enabled": "true" }""");
        var result = await _ctrl.UpdateEmailSettings(body, default);
        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var json = JsonSerializer.Serialize(bad.Value);
        Assert.Contains("enabled must be a boolean", json);
    }

    [Fact]
    public async Task UpdateEmailSettings_ValidatesBooleanUseTls()
    {
        var body = JsonSerializer.Deserialize<JsonElement>("""{ "use_tls": "yes" }""");
        var result = await _ctrl.UpdateEmailSettings(body, default);
        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var json = JsonSerializer.Serialize(bad.Value);
        Assert.Contains("use_tls must be a boolean", json);
    }

    [Fact]
    public async Task UpdateEmailSettings_ValidatesBooleanUseSsl()
    {
        var body = JsonSerializer.Deserialize<JsonElement>("""{ "use_ssl": 1 }""");
        var result = await _ctrl.UpdateEmailSettings(body, default);
        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var json = JsonSerializer.Serialize(bad.Value);
        Assert.Contains("use_ssl must be a boolean", json);
    }

    [Fact]
    public async Task UpdateEmailSettings_ValidatesRecipientListIsArray()
    {
        var body = JsonSerializer.Deserialize<JsonElement>("""{ "recipient_list": "not-an-array" }""");
        var result = await _ctrl.UpdateEmailSettings(body, default);
        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var json = JsonSerializer.Serialize(bad.Value);
        Assert.Contains("recipient_list must be an array", json);
    }

    [Fact]
    public async Task UpdateEmailSettings_PreservesPasswordWhenNotInPayload()
    {
        // First set a password
        var body1 = JsonSerializer.Deserialize<JsonElement>("""
        { "smtp_password": "original_secret", "smtp_host": "mail.test.com" }
        """);
        await _ctrl.UpdateEmailSettings(body1, default);

        // Then update without smtp_password — it should be preserved
        var body2 = JsonSerializer.Deserialize<JsonElement>("""
        { "smtp_host": "mail2.test.com" }
        """);
        await _ctrl.UpdateEmailSettings(body2, default);

        // Verify password is still set
        var result = await _ctrl.GetEmailSettings(default);
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value);
        Assert.Contains("\"has_password\":true", json);
        Assert.Contains("\"smtp_host\":\"mail2.test.com\"", json);
    }

    // -----------------------------------------------------------------------
    // POST /api/notifications/push-subscriptions — create
    // -----------------------------------------------------------------------

    [Fact]
    public async Task CreatePushSubscription_RequiresEndpoint()
    {
        var body = JsonSerializer.Deserialize<JsonElement>("""
        { "p256dh": "key1", "auth": "key2" }
        """);
        var result = await _ctrl.CreatePushSubscription(body, default);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task CreatePushSubscription_RequiresP256dhAndAuth()
    {
        var body = JsonSerializer.Deserialize<JsonElement>("""
        { "endpoint": "https://push.example.com/sub1" }
        """);
        var result = await _ctrl.CreatePushSubscription(body, default);
        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var json = JsonSerializer.Serialize(bad.Value);
        Assert.Contains("p256dh and auth are required", json);
    }

    [Fact]
    public async Task CreatePushSubscription_AcceptsFlatKeys()
    {
        var body = JsonSerializer.Deserialize<JsonElement>("""
        {
            "endpoint": "https://push.example.com/sub1",
            "p256dh": "publickey123",
            "auth": "authkey456"
        }
        """);
        var result = await _ctrl.CreatePushSubscription(body, default);
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value);
        Assert.Contains("\"success\":true", json);
        Assert.Contains("\"endpoint\":\"https://push.example.com/sub1\"", json);
    }

    [Fact]
    public async Task CreatePushSubscription_AcceptsNestedKeysFormat()
    {
        var body = JsonSerializer.Deserialize<JsonElement>("""
        {
            "endpoint": "https://push.example.com/sub2",
            "keys": {
                "p256dh": "nestedpubkey",
                "auth": "nestedauthkey"
            }
        }
        """);
        var result = await _ctrl.CreatePushSubscription(body, default);
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value);
        Assert.Contains("\"success\":true", json);
    }

    // -----------------------------------------------------------------------
    // GET /api/notifications/push-subscriptions — list
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ListPushSubscriptions_ReturnsEmptyListInitially()
    {
        var result = await _ctrl.ListPushSubscriptions(default);
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value);
        Assert.Contains("\"subscriptions\":[]", json);
    }

    // -----------------------------------------------------------------------
    // DELETE /api/notifications/push-subscriptions/{id}
    // -----------------------------------------------------------------------

    [Fact]
    public async Task DeletePushSubscription_ReturnsNotFound_ForMissingId()
    {
        var result = await _ctrl.DeletePushSubscription("nonexistent-id", default);
        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task DeletePushSubscription_ReturnsOk_AfterCreate()
    {
        // Create a subscription first
        var body = JsonSerializer.Deserialize<JsonElement>("""
        {
            "endpoint": "https://push.example.com/delete-test",
            "p256dh": "pk",
            "auth": "ak"
        }
        """);
        var createResult = await _ctrl.CreatePushSubscription(body, default);
        var ok = Assert.IsType<OkObjectResult>(createResult);
        var json = JsonSerializer.Serialize(ok.Value);
        var doc = JsonDocument.Parse(json);
        var subId = doc.RootElement
            .GetProperty("subscription")
            .GetProperty("id")
            .GetString()!;

        // Delete it
        var delResult = await _ctrl.DeletePushSubscription(subId, default);
        Assert.IsType<OkObjectResult>(delResult);
    }

    // -----------------------------------------------------------------------
    // POST /api/notifications/push-subscriptions/test
    // -----------------------------------------------------------------------

    [Fact]
    public async Task TestPush_RequiresSubscriptionId()
    {
        var body = JsonSerializer.Deserialize<JsonElement>("""{}""");
        var result = await _ctrl.TestPushSubscription(body, default);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task TestPush_Returns502_WhenVapidNotConfigured()
    {
        // Create a subscription so the ID is valid
        var subBody = JsonSerializer.Deserialize<JsonElement>("""
        {
            "endpoint": "https://push.example.com/test-push",
            "p256dh": "pk",
            "auth": "ak"
        }
        """);
        var createResult = await _ctrl.CreatePushSubscription(subBody, default);
        var createOk = Assert.IsType<OkObjectResult>(createResult);
        var json = JsonSerializer.Serialize(createOk.Value);
        var doc = JsonDocument.Parse(json);
        var subId = doc.RootElement.GetProperty("subscription").GetProperty("id").GetString()!;

        // Test push — VAPID not configured, so push service returns error
        var testBody = JsonSerializer.Deserialize<JsonElement>(
            $$"""{ "subscription_id": "{{subId}}" }""");
        var result = await _ctrl.TestPushSubscription(testBody, default);
        // Push service has no VAPID keys, so SendTestAsync returns an error string -> 502
        var statusResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(502, statusResult.StatusCode);
    }
}
