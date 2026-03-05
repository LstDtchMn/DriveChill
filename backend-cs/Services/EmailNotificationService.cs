using System.Net.Mail;
using System.Text.Json;
using DriveChill.Models;

namespace DriveChill.Services;

/// <summary>
/// Sends alert notification emails via SMTP.
/// Settings (host, port, credentials, recipients) are read from the DB at send time,
/// so runtime changes take effect on the next alert without a restart.
///
/// NOTE: System.Net.Mail.SmtpClient is used for simplicity but is obsolete.
/// It supports STARTTLS (port 587) via EnableSsl but does NOT support implicit
/// SSL/TLS (port 465).  Use port 587 with STARTTLS for encrypted SMTP.
/// A future version should migrate to MailKit for full port 465 support.
/// </summary>
public sealed class EmailNotificationService
{
    private readonly DbService _db;
    private readonly ILogger<EmailNotificationService> _log;

    public EmailNotificationService(DbService db, ILogger<EmailNotificationService> log)
    {
        _db  = db;
        _log = log;
    }

    /// <summary>
    /// Send an alert notification email. Silently no-ops when email is disabled or unconfigured.
    /// </summary>
    public async Task SendAlertAsync(AlertEvent evt, CancellationToken ct = default)
    {
        var s = await _db.GetEmailSettingsAsync(ct);
        if (!s.Enabled || string.IsNullOrEmpty(s.SmtpHost))
            return;

        var recipients = JsonSerializer.Deserialize<string[]>(s.RecipientList)
                         ?? Array.Empty<string>();
        if (recipients.Length == 0)
            return;

        var password = await _db.GetSmtpPasswordAsync(ct);

        try
        {
            using var client = new SmtpClient(s.SmtpHost, s.SmtpPort)
            {
                Credentials    = new System.Net.NetworkCredential(s.SmtpUsername, password),
                EnableSsl      = s.UseTls || s.UseSsl,
                DeliveryMethod = SmtpDeliveryMethod.Network,
                Timeout        = 15_000,
            };

            var subject = $"DriveChill Alert: {evt.SensorName} {evt.Condition} {evt.Threshold}";
            var body    = FormatBody(evt);

            using var msg = new MailMessage();
            msg.From    = new MailAddress(s.SenderAddress);
            msg.Subject = subject;
            msg.Body    = body;
            foreach (var r in recipients)
                msg.To.Add(r);

            await client.SendMailAsync(msg, ct);
            _log.LogInformation("Alert email sent for rule {RuleId}", evt.RuleId);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Alert email delivery failed for rule {RuleId}", evt.RuleId);
        }
    }

    /// <summary>
    /// Send a test email to verify SMTP configuration. Returns null on success, error message on failure.
    /// </summary>
    public async Task<string?> SendTestAsync(CancellationToken ct = default)
    {
        var s = await _db.GetEmailSettingsAsync(ct);
        if (!s.Enabled || string.IsNullOrEmpty(s.SmtpHost))
            return "Email notifications are not configured.";

        var recipients = JsonSerializer.Deserialize<string[]>(s.RecipientList)
                         ?? Array.Empty<string>();
        if (recipients.Length == 0)
            return "No recipients configured.";

        var password = await _db.GetSmtpPasswordAsync(ct);

        try
        {
            using var client = new SmtpClient(s.SmtpHost, s.SmtpPort)
            {
                Credentials    = new System.Net.NetworkCredential(s.SmtpUsername, password),
                EnableSsl      = s.UseTls || s.UseSsl,
                DeliveryMethod = SmtpDeliveryMethod.Network,
                Timeout        = 10_000,
            };

            using var msg = new MailMessage();
            msg.From    = new MailAddress(s.SenderAddress);
            msg.Subject = "DriveChill Test Notification";
            msg.Body    = "This is a test email from DriveChill to confirm that email notifications are working.";
            foreach (var r in recipients)
                msg.To.Add(r);

            await client.SendMailAsync(msg, ct);
            return null;   // success
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    // -----------------------------------------------------------------------

    private static string FormatBody(AlertEvent evt)
    {
        var direction = evt.Condition == "above" ? "exceeded" : "fell below";
        return $"""
            DriveChill alert triggered.

            Sensor  : {evt.SensorName}
            Value   : {evt.ActualValue:F1}
            Threshold: {direction} {evt.Threshold:F1}
            Fired at: {evt.FiredAt:u}

            {(string.IsNullOrWhiteSpace(evt.Message) ? "" : $"Note: {evt.Message}")}
            """;
    }
}
