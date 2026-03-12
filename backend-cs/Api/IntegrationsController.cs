using Microsoft.AspNetCore.Mvc;
using DriveChill.Services;

namespace DriveChill.Api;

[ApiController]
[Route("api/integrations")]
public sealed class IntegrationsController : ControllerBase
{
    private readonly WebhookService _webhooks;
    private readonly EmailNotificationService _email;
    private readonly PushNotificationService _push;
    private readonly NotificationChannelService _channels;
    private readonly DbService _db;

    public IntegrationsController(
        WebhookService webhooks,
        EmailNotificationService email,
        PushNotificationService push,
        NotificationChannelService channels,
        DbService db)
    {
        _webhooks = webhooks;
        _email    = email;
        _push     = push;
        _channels = channels;
        _db       = db;
    }

    /// <summary>GET /api/integrations/health — aggregated health of all integration services.</summary>
    [HttpGet("health")]
    public async Task<IActionResult> GetHealth(CancellationToken ct)
    {
        // Webhook status
        var webhookCfg = _webhooks.GetConfigRaw();
        var recentDeliveries = _webhooks.GetDeliveries(limit: 50);
        var recentFailures = recentDeliveries.Count(d => !d.Success);
        var lastDelivery = recentDeliveries.FirstOrDefault();

        // Email status
        var emailSettings = await _db.GetEmailSettingsAsync(ct);

        // Push subscription count
        var pushSubs = await _db.GetAllPushSubscriptionsAsync(ct);

        // MQTT status
        var mqttChannels = await _channels.GetMqttStatusAsync(ct);

        return Ok(new
        {
            mqtt = new
            {
                channels = mqttChannels.Select(m => new
                {
                    channel_id = m.ChannelId,
                    name       = m.Name,
                    broker_url = m.BrokerUrl,
                    connected  = m.Connected,
                    enabled    = m.Enabled,
                }),
                last_sent_at     = _channels.LastSentAt?.ToString("o"),
                last_error       = _channels.LastError,
                success_count    = _channels.SuccessCount,
                failure_count    = _channels.FailureCount,
            },
            webhooks = new
            {
                enabled          = webhookCfg.Enabled,
                target_url       = webhookCfg.TargetUrl,
                last_delivery_at = lastDelivery?.Timestamp,
                recent_failures  = recentFailures,
                last_error       = _webhooks.LastError,
                success_count    = _webhooks.SuccessCount,
                failure_count    = _webhooks.FailureCount,
            },
            email = new
            {
                configured       = emailSettings.Enabled && !string.IsNullOrEmpty(emailSettings.SmtpHost),
                smtp_host        = emailSettings.SmtpHost,
                last_sent_at     = _email.LastSentAt?.ToString("o"),
                last_error       = _email.LastError,
                success_count    = _email.SuccessCount,
                failure_count    = _email.FailureCount,
            },
            push = new
            {
                configured          = _push.IsConfigured,
                subscription_count  = pushSubs.Count,
                last_sent_at        = _push.LastSentAt?.ToString("o"),
                last_error          = _push.LastError,
                success_count       = _push.SuccessCount,
                failure_count       = _push.FailureCount,
            },
        });
    }
}
