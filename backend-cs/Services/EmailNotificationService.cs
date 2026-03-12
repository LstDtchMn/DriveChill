using System.Text.Json;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using DriveChill.Models;

namespace DriveChill.Services;

/// <summary>
/// Sends alert notification emails via SMTP using MailKit.
/// Supports STARTTLS (port 587) via use_tls and implicit TLS (port 465) via use_ssl.
/// Settings (host, port, credentials, recipients) are read from the DB at send time,
/// so runtime changes take effect on the next alert without a restart.
/// </summary>
public class EmailNotificationService
{
    private readonly DbService _db;
    private readonly ILogger<EmailNotificationService> _log;

    // ── Integration health tracking ──────────────────────────────────────
    private long _successCount;
    private long _failureCount;
    private DateTimeOffset? _lastSentAt;
    private string? _lastError;

    public long SuccessCount => Interlocked.Read(ref _successCount);
    public long FailureCount => Interlocked.Read(ref _failureCount);
    public DateTimeOffset? LastSentAt => _lastSentAt;
    public string? LastError => _lastError;

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
            var subject = $"DriveChill Alert: {evt.SensorName} {evt.Condition} {evt.Threshold}";
            var body    = FormatBody(evt);

            var message = BuildMessage(s.SenderAddress, recipients, subject, body, isHtml: false);
            await SendViaMailKitAsync(s, password, message, ct);
            _lastSentAt = DateTimeOffset.UtcNow;
            Interlocked.Increment(ref _successCount);
            _lastError = null;
            _log.LogInformation("Alert email sent for rule {RuleId}", evt.RuleId);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _failureCount);
            _lastError = ex.Message;
            _log.LogWarning(ex, "Email delivery failed: {Service} rule={RuleId} smtp_host={SmtpHost}",
                "email", evt.RuleId, s.SmtpHost);
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
            var message = BuildMessage(s.SenderAddress, recipients,
                "DriveChill Test Notification",
                "This is a test email from DriveChill to confirm that email notifications are working.",
                isHtml: false);
            await SendViaMailKitAsync(s, password, message, ct);
            _lastSentAt = DateTimeOffset.UtcNow;
            Interlocked.Increment(ref _successCount);
            _lastError = null;
            return null;   // success
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _failureCount);
            _lastError = ex.Message;
            return ex.Message;
        }
    }

    /// <summary>
    /// Send an HTML analytics report email. Returns true on successful delivery.
    /// </summary>
    public virtual async Task<bool> SendHtmlReportAsync(string subject, string htmlBody, CancellationToken ct = default)
    {
        var s = await _db.GetEmailSettingsAsync(ct);
        if (!s.Enabled || string.IsNullOrEmpty(s.SmtpHost))
            return false;

        var recipients = JsonSerializer.Deserialize<string[]>(s.RecipientList)
                         ?? Array.Empty<string>();
        if (recipients.Length == 0)
            return false;

        var password = await _db.GetSmtpPasswordAsync(ct);

        try
        {
            var message = BuildMessage(s.SenderAddress, recipients, subject, htmlBody, isHtml: true);
            await SendViaMailKitAsync(s, password, message, ct);
            _lastSentAt = DateTimeOffset.UtcNow;
            Interlocked.Increment(ref _successCount);
            _lastError = null;
            return true;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _failureCount);
            _lastError = ex.Message;
            _log.LogWarning(ex, "Email delivery failed: {Service} subject={Subject} smtp_host={SmtpHost}",
                "email", subject, s.SmtpHost);
            return false;
        }
    }

    // -----------------------------------------------------------------------

    private static MimeMessage BuildMessage(string sender, string[] recipients, string subject, string body, bool isHtml)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(sender));
        foreach (var r in recipients)
            message.To.Add(MailboxAddress.Parse(r));
        message.Subject = subject;

        message.Body = isHtml
            ? new TextPart("html")  { Text = body }
            : new TextPart("plain") { Text = body };

        return message;
    }

    /// <summary>
    /// Connects to the SMTP server using MailKit with the correct security option:
    ///   use_ssl  = true  → SecureSocketOptions.SslOnConnect  (implicit TLS, typically port 465)
    ///   use_tls  = true  → SecureSocketOptions.StartTls      (STARTTLS, typically port 587)
    ///   neither          → SecureSocketOptions.Auto           (let MailKit negotiate)
    /// </summary>
    private static async Task SendViaMailKitAsync(
        EmailNotificationSettingsRecord s, string password, MimeMessage message, CancellationToken ct)
    {
        SecureSocketOptions secureOption;
        if (s.UseSsl)
            secureOption = SecureSocketOptions.SslOnConnect;
        else if (s.UseTls)
            secureOption = SecureSocketOptions.StartTls;
        else
            secureOption = SecureSocketOptions.Auto;

        using var client = new SmtpClient();
        client.Timeout = 15_000;
        await client.ConnectAsync(s.SmtpHost, s.SmtpPort, secureOption, ct);

        if (!string.IsNullOrEmpty(s.SmtpUsername))
            await client.AuthenticateAsync(s.SmtpUsername, password, ct);

        await client.SendAsync(message, ct);
        await client.DisconnectAsync(quit: true, ct);
    }

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
