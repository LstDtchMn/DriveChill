using System.Net;
using System.Text.Json;
using DriveChill.Models;
using Lib.Net.Http.WebPush;
using Lib.Net.Http.WebPush.Authentication;

namespace DriveChill.Services;

/// <summary>
/// Delivers Web Push alert notifications to all stored subscriptions using VAPID.
///
/// VAPID keys are read from environment variables at startup:
///   DRIVECHILL_VAPID_PUBLIC_KEY   — URL-safe base64-encoded public key
///   DRIVECHILL_VAPID_PRIVATE_KEY  — URL-safe base64-encoded private key
///   DRIVECHILL_VAPID_CONTACT_EMAIL — contact mailto: for the VAPID subject
///
/// When VAPID keys are not configured, the service is a no-op.
/// Subscriptions that return HTTP 410 Gone are automatically removed.
/// </summary>
public sealed class PushNotificationService
{
    private readonly DbService _db;
    private readonly AppSettings _settings;
    private readonly ILogger<PushNotificationService> _log;

    // Null when VAPID keys are not configured.
    private readonly PushServiceClient? _pushClient;

    public PushNotificationService(DbService db, AppSettings settings,
        ILogger<PushNotificationService> log)
    {
        _db       = db;
        _settings = settings;
        _log      = log;

        if (!string.IsNullOrEmpty(settings.VapidPublicKey) &&
            !string.IsNullOrEmpty(settings.VapidPrivateKey))
        {
            _pushClient = new PushServiceClient
            {
                DefaultAuthentication = new VapidAuthentication(
                    settings.VapidPublicKey,
                    settings.VapidPrivateKey)
                {
                    Subject = settings.VapidContactEmail,
                },
            };
        }
        else
        {
            _log.LogInformation(
                "VAPID keys not configured — Web Push notifications are disabled. " +
                "Set DRIVECHILL_VAPID_PUBLIC_KEY and DRIVECHILL_VAPID_PRIVATE_KEY to enable.");
        }
    }

    /// <summary>
    /// Send an alert push notification to all stored subscriptions.
    /// Silently no-ops when VAPID keys are not configured or there are no subscriptions.
    /// </summary>
    public async Task SendAlertAsync(AlertEvent evt, CancellationToken ct = default)
    {
        if (_pushClient is null)
            return;

        var subs = await _db.GetAllPushSubscriptionsAsync(ct);
        if (subs.Count == 0)
            return;

        var payload = JsonSerializer.Serialize(new
        {
            type      = "alert",
            rule_id   = evt.RuleId,
            sensor    = evt.SensorName,
            condition = evt.Condition,
            value     = evt.ActualValue,
            threshold = evt.Threshold,
            message   = evt.Message,
            fired_at  = evt.FiredAt.ToString("o"),
        });

        // Fan out deliveries — don't block on any single failure.
        var tasks = subs.Select(sub => DeliverAsync(sub, payload, ct));
        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Send a test push notification to a single subscription.
    /// Returns null on success, error message on failure.
    /// </summary>
    public async Task<string?> SendTestAsync(string subscriptionId, CancellationToken ct = default)
    {
        if (_pushClient is null)
            return "VAPID keys are not configured. Set DRIVECHILL_VAPID_PUBLIC_KEY and DRIVECHILL_VAPID_PRIVATE_KEY.";

        var sub = await _db.GetPushSubscriptionAsync(subscriptionId, ct);
        if (sub is null)
            return "Subscription not found.";

        var payload = JsonSerializer.Serialize(new
        {
            type    = "test",
            message = "DriveChill test push notification — if you see this, push is working.",
        });

        try
        {
            await DeliverCoreAsync(sub, payload, ct);
            return null;   // success
        }
        catch (PushServiceClientException ex) when (
            ex.StatusCode is HttpStatusCode.Gone or HttpStatusCode.NotFound)
        {
            _log.LogInformation("Removing expired push subscription {Id} after failed test send (HTTP {Status})",
                sub.Id, ex.StatusCode);
            await _db.DeletePushSubscriptionAsync(sub.Id, ct);
            return $"Push service returned HTTP {(int)ex.StatusCode}: {ex.Message}";
        }
        catch (PushServiceClientException ex)
        {
            return $"Push service returned HTTP {(int)ex.StatusCode}: {ex.Message}";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    // -----------------------------------------------------------------------

    /// <summary>
    /// Core delivery logic — throws on failure so callers can decide whether to
    /// surface or swallow the exception.
    /// </summary>
    private async Task DeliverCoreAsync(PushSubscriptionRecord sub, string payload, CancellationToken ct)
    {
        var subscription = new PushSubscription
        {
            Endpoint = sub.Endpoint,
        };
        subscription.SetKey(PushEncryptionKeyName.P256DH, sub.P256dh);
        subscription.SetKey(PushEncryptionKeyName.Auth,   sub.AuthKey);

        var message = new PushMessage(payload)
        {
            Topic      = "alert",
            Urgency    = PushMessageUrgency.High,
            TimeToLive = 3600,
        };

        await _pushClient!.RequestPushMessageDeliveryAsync(subscription, message, ct);
        await _db.TouchPushSubscriptionAsync(sub.Id, ct);
    }

    /// <summary>
    /// Fire-and-forget wrapper around <see cref="DeliverCoreAsync"/> that swallows errors
    /// (used in the alert broadcast path where delivery must not block the sensor loop).
    /// </summary>
    private async Task DeliverAsync(PushSubscriptionRecord sub, string payload, CancellationToken ct)
    {
        try
        {
            await DeliverCoreAsync(sub, payload, ct);
            _log.LogDebug("Push notification delivered to subscription {Id}", sub.Id);
        }
        catch (PushServiceClientException ex) when (
            ex.StatusCode is HttpStatusCode.Gone or HttpStatusCode.NotFound)
        {
            _log.LogInformation("Removing expired push subscription {Id} (HTTP {Status})",
                sub.Id, ex.StatusCode);
            await _db.DeletePushSubscriptionAsync(sub.Id, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Push delivery failed for subscription {Id}", sub.Id);
        }
    }
}
