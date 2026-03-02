using Microsoft.AspNetCore.Mvc;
using DriveChill.Services;
using DriveChill.Models;
using System.Text.Json;

namespace DriveChill.Api;

[ApiController]
[Route("api/notifications")]
public sealed class NotificationsController : ControllerBase
{
    private readonly DbService                   _db;
    private readonly EmailNotificationService    _email;
    private readonly PushNotificationService     _push;

    public NotificationsController(DbService db,
        EmailNotificationService email, PushNotificationService push)
    {
        _db    = db;
        _email = email;
        _push  = push;
    }

    // ---- Email ----

    [HttpGet("email")]
    public async Task<IActionResult> GetEmailSettings(CancellationToken ct)
    {
        var s = await _db.GetEmailSettingsAsync(ct);
        return Ok(new { settings = ToEmailView(s) });
    }

    [HttpPut("email")]
    public async Task<IActionResult> UpdateEmailSettings([FromBody] JsonElement body, CancellationToken ct)
    {
        var current = await _db.GetEmailSettingsAsync(ct);
        var smtpPort = body.TryGetProperty("smtp_port", out var sp) ? sp.GetInt32() : current.SmtpPort;
        if (smtpPort is < 1 or > 65535)
            return BadRequest(new { detail = "smtp_port must be between 1 and 65535" });

        var s = new EmailNotificationSettingsRecord
        {
            Enabled       = body.TryGetProperty("enabled",        out var en)   ? en.GetBoolean()  : current.Enabled,
            SmtpHost      = body.TryGetProperty("smtp_host",      out var sh)   ? sh.GetString()!  : current.SmtpHost,
            SmtpPort      = smtpPort,
            SmtpUsername  = body.TryGetProperty("smtp_username",  out var su)   ? su.GetString()!  : current.SmtpUsername,
            // Send empty string to preserve existing password; send actual value to update.
            SmtpPassword  = body.TryGetProperty("smtp_password",  out var spw)  ? spw.GetString() ?? "" : "",
            SenderAddress = body.TryGetProperty("sender_address", out var sa)   ? sa.GetString()!  : current.SenderAddress,
            RecipientList = body.TryGetProperty("recipient_list", out var rl)   ? JsonSerializer.Serialize(rl) : current.RecipientList,
            UseTls        = body.TryGetProperty("use_tls",        out var tls)  ? tls.GetBoolean() : current.UseTls,
            UseSsl        = body.TryGetProperty("use_ssl",        out var ssl2) ? ssl2.GetBoolean() : current.UseSsl,
            UpdatedAt     = DateTimeOffset.UtcNow.ToString("o"),
        };
        await _db.UpdateEmailSettingsAsync(s, ct);
        var updated = await _db.GetEmailSettingsAsync(ct);
        return Ok(new { success = true, settings = ToEmailView(updated) });
    }

    [HttpPost("email/test")]
    public async Task<IActionResult> TestEmail(CancellationToken ct)
    {
        var error = await _email.SendTestAsync(ct);
        return Ok(new { success = error is null, error });
    }

    // ---- Push subscriptions ----

    [HttpGet("push-subscriptions")]
    public async Task<IActionResult> ListPushSubscriptions(CancellationToken ct)
    {
        var subs = await _db.GetAllPushSubscriptionsAsync(ct);
        return Ok(new { subscriptions = subs.Select(ToPushView) });
    }

    [HttpPost("push-subscriptions")]
    public async Task<IActionResult> CreatePushSubscription(
        [FromBody] JsonElement body, CancellationToken ct)
    {
        // Frontend sends flat: { endpoint, p256dh, auth, user_agent? }
        if (!body.TryGetProperty("endpoint", out var epEl) || string.IsNullOrEmpty(epEl.GetString()))
            return BadRequest(new { detail = "endpoint is required" });

        // Accept both flat (p256dh/auth at top level) and nested (keys.p256dh/keys.auth).
        string? p256dh = null, auth = null;
        if (body.TryGetProperty("p256dh", out var p256Flat))
            p256dh = p256Flat.GetString();
        if (body.TryGetProperty("auth", out var authFlat))
            auth = authFlat.GetString();
        // Fallback: nested keys object (Web Push spec shape)
        if ((p256dh is null || auth is null) && body.TryGetProperty("keys", out var keysEl))
        {
            if (p256dh is null && keysEl.TryGetProperty("p256dh", out var p256Nested))
                p256dh = p256Nested.GetString();
            if (auth is null && keysEl.TryGetProperty("auth", out var authNested))
                auth = authNested.GetString();
        }

        if (string.IsNullOrEmpty(p256dh) || string.IsNullOrEmpty(auth))
            return BadRequest(new { detail = "p256dh and auth are required" });

        var sub = new PushSubscriptionRecord
        {
            Endpoint  = epEl.GetString()!,
            P256dh    = p256dh,
            AuthKey   = auth,
            UserAgent = body.TryGetProperty("user_agent", out var ua) ? ua.GetString() : null,
        };

        var created = await _db.CreatePushSubscriptionAsync(sub, ct);
        return Ok(new { success = true, subscription = ToPushView(created) });
    }

    [HttpDelete("push-subscriptions/{id}")]
    public async Task<IActionResult> DeletePushSubscription(string id, CancellationToken ct)
    {
        var deleted = await _db.DeletePushSubscriptionAsync(id, ct);
        if (!deleted) return NotFound(new { detail = "Subscription not found" });
        return Ok(new { success = true });
    }

    [HttpPost("push-subscriptions/test")]
    public async Task<IActionResult> TestPushSubscription(
        [FromBody] JsonElement body, CancellationToken ct)
    {
        var subscriptionId = body.TryGetProperty("subscription_id", out var idEl)
            ? idEl.GetString() ?? ""
            : "";

        if (string.IsNullOrEmpty(subscriptionId))
            return BadRequest(new { detail = "subscription_id is required" });

        var error = await _push.SendTestAsync(subscriptionId, ct);
        return Ok(new { success = error is null, error });
    }

    // -----------------------------------------------------------------------

    private static object ToEmailView(EmailNotificationSettingsRecord s) => new
    {
        enabled        = s.Enabled,
        smtp_host      = s.SmtpHost,
        smtp_port      = s.SmtpPort,
        smtp_username  = s.SmtpUsername,
        has_password   = s.SmtpPassword.Length > 0,
        sender_address = s.SenderAddress,
        recipient_list = JsonSerializer.Deserialize<string[]>(s.RecipientList) ?? Array.Empty<string>(),
        use_tls        = s.UseTls,
        use_ssl        = s.UseSsl,
        updated_at     = s.UpdatedAt,
    };

    private static object ToPushView(PushSubscriptionRecord s) => new
    {
        id          = s.Id,
        endpoint    = s.Endpoint,
        user_agent  = s.UserAgent,
        created_at  = s.CreatedAt,
        last_used_at = s.LastUsedAt,
    };
}
