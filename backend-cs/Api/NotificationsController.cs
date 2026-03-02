using Microsoft.AspNetCore.Mvc;
using DriveChill.Services;
using DriveChill.Models;
using System.Text.Json;
using System.Net.Mail;

namespace DriveChill.Api;

[ApiController]
[Route("api/notifications")]
public sealed class NotificationsController : ControllerBase
{
    private readonly DbService _db;

    public NotificationsController(DbService db) => _db = db;

    // ---- Email ----

    [HttpGet("email")]
    public async Task<IActionResult> GetEmailSettings(CancellationToken ct)
    {
        var s = await _db.GetEmailSettingsAsync(ct);
        return Ok(new { settings = new {
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
        }});
    }

    [HttpPut("email")]
    public async Task<IActionResult> UpdateEmailSettings([FromBody] JsonElement body, CancellationToken ct)
    {
        var current = await _db.GetEmailSettingsAsync(ct);
        var s = new EmailNotificationSettingsRecord
        {
            Enabled       = body.TryGetProperty("enabled",        out var en)   ? en.GetBoolean()  : current.Enabled,
            SmtpHost      = body.TryGetProperty("smtp_host",      out var sh)   ? sh.GetString()!  : current.SmtpHost,
            SmtpPort      = body.TryGetProperty("smtp_port",      out var sp)   ? sp.GetInt32()    : current.SmtpPort,
            SmtpUsername  = body.TryGetProperty("smtp_username",  out var su)   ? su.GetString()!  : current.SmtpUsername,
            SmtpPassword  = body.TryGetProperty("smtp_password",  out var spw)  ? spw.GetString()! : current.SmtpPassword,
            SenderAddress = body.TryGetProperty("sender_address", out var sa)   ? sa.GetString()!  : current.SenderAddress,
            RecipientList = body.TryGetProperty("recipient_list", out var rl)   ? JsonSerializer.Serialize(rl) : current.RecipientList,
            UseTls        = body.TryGetProperty("use_tls",        out var tls)  ? tls.GetBoolean() : current.UseTls,
            UseSsl        = body.TryGetProperty("use_ssl",        out var ssl2) ? ssl2.GetBoolean(): current.UseSsl,
            UpdatedAt     = DateTimeOffset.UtcNow.ToString("o"),
        };
        await _db.UpdateEmailSettingsAsync(s, ct);
        var updated = await _db.GetEmailSettingsAsync(ct);
        return Ok(new { success = true, settings = new {
            enabled        = updated.Enabled,
            smtp_host      = updated.SmtpHost,
            smtp_port      = updated.SmtpPort,
            smtp_username  = updated.SmtpUsername,
            has_password   = updated.SmtpPassword.Length > 0,
            sender_address = updated.SenderAddress,
            recipient_list = JsonSerializer.Deserialize<string[]>(updated.RecipientList) ?? Array.Empty<string>(),
            use_tls        = updated.UseTls,
            use_ssl        = updated.UseSsl,
            updated_at     = updated.UpdatedAt,
        }});
    }

    [HttpPost("email/test")]
    public async Task<IActionResult> TestEmail(CancellationToken ct)
    {
        var s = await _db.GetEmailSettingsAsync(ct);
        if (!s.Enabled || string.IsNullOrEmpty(s.SmtpHost))
            return Ok(new { success = false, error = "Email notifications are not configured." });

        var recipients = JsonSerializer.Deserialize<string[]>(s.RecipientList) ?? Array.Empty<string>();
        if (recipients.Length == 0)
            return Ok(new { success = false, error = "No recipients configured." });

        try
        {
            using var client = new SmtpClient(s.SmtpHost, s.SmtpPort)
            {
                Credentials       = new System.Net.NetworkCredential(s.SmtpUsername, s.SmtpPassword),
                EnableSsl         = s.UseTls || s.UseSsl,
                DeliveryMethod    = SmtpDeliveryMethod.Network,
                Timeout           = 10000,
            };
            var msg = new MailMessage(s.SenderAddress, recipients[0],
                "DriveChill Test Notification",
                "This is a test email from DriveChill.");
            foreach (var r in recipients.Skip(1)) msg.To.Add(r);
            await client.SendMailAsync(msg, ct);
            return Ok(new { success = true, error = (string?)null });
        }
        catch (Exception ex)
        {
            return Ok(new { success = false, error = ex.Message });
        }
    }

    // ---- Push subscriptions (storage only; actual delivery requires pywebpush) ----

    [HttpGet("push-subscriptions")]
    public IActionResult ListPushSubscriptions()
    {
        // Push delivery is not implemented in the C# backend (requires pywebpush/VAPID).
        // Return an empty list so the frontend works without errors.
        return Ok(new { subscriptions = Array.Empty<object>() });
    }

    [HttpPost("push-subscriptions")]
    public IActionResult CreatePushSubscription()
    {
        return StatusCode(501, new { detail = "Web Push delivery is not available in the C# backend. Use the Python backend for push notifications." });
    }

    [HttpDelete("push-subscriptions/{id}")]
    public IActionResult DeletePushSubscription(string id)
    {
        return Ok(new { success = true });
    }

    [HttpPost("push-subscriptions/test")]
    public IActionResult TestPushSubscription()
    {
        return StatusCode(501, new { detail = "Web Push delivery is not available in the C# backend." });
    }
}
