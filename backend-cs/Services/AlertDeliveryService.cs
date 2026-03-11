using DriveChill.Models;

namespace DriveChill.Services;

/// <summary>
/// Fans out alert events to all notification channels (webhooks, email, push, MQTT).
/// Extracted from SensorWorker so delivery logic is testable independently.
/// </summary>
public sealed class AlertDeliveryService
{
    private readonly WebhookService              _webhooks;
    private readonly EmailNotificationService    _email;
    private readonly PushNotificationService     _push;
    private readonly NotificationChannelService  _channels;
    private readonly ILogger<AlertDeliveryService> _log;

    public AlertDeliveryService(
        WebhookService webhooks,
        EmailNotificationService email,
        PushNotificationService push,
        NotificationChannelService channels,
        ILogger<AlertDeliveryService> log)
    {
        _webhooks = webhooks;
        _email    = email;
        _push     = push;
        _channels = channels;
        _log      = log;
    }

    /// <summary>
    /// Fire-and-forget: dispatch alert events to all channels.
    /// Individual failures are logged but never propagate.
    /// </summary>
    public void DispatchAsync(IReadOnlyList<AlertEvent> events)
    {
        if (events.Count == 0) return;

        _ = Task.Run(async () =>
        {
            try
            {
                var tasks = new List<Task>
                {
                    _webhooks.DispatchAlertEventsAsync(events, CancellationToken.None),
                };
                foreach (var evt in events)
                {
                    tasks.Add(SafeRun(() => _email.SendAlertAsync(evt, CancellationToken.None)));
                    tasks.Add(SafeRun(() => _push.SendAlertAsync(evt, CancellationToken.None)));
                    tasks.Add(SafeRun(() => _channels.SendAlertAllAsync(
                        evt.SensorName, evt.ActualValue, evt.Threshold,
                        CancellationToken.None)));
                }
                await Task.WhenAll(tasks);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Alert delivery fan-out error");
            }
        }, CancellationToken.None);
    }

    /// <summary>Wrap an async action so individual failures are logged, not thrown.</summary>
    private async Task SafeRun(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { _log.LogWarning(ex, "Individual alert delivery failed"); }
    }
}
