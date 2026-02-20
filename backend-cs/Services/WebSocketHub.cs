using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using DriveChill.Models;

namespace DriveChill.Services;

/// <summary>
/// Handles individual WebSocket connections.
///
/// Each connection:
///   1. Subscribes to SensorService.Channel
///   2. Waits for snapshots (with a 5 s timeout = heartbeat)
///   3. Serialises as flat { type, timestamp, readings, applied_speeds, alerts, active_alerts }
///      to match the TypeScript WSMessage interface exactly.
///   4. On timeout, sends {"type":"heartbeat"} to detect stale connections
///   5. Cleans up the subscription on disconnect
/// </summary>
public sealed class WebSocketHub
{
    private readonly SensorService  _sensors;
    private readonly FanService     _fans;
    private readonly AlertService   _alerts;
    private readonly FanTestService _fanTest;
    private readonly ILogger<WebSocketHub> _log;

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public WebSocketHub(SensorService sensors, FanService fans, AlertService alerts,
        FanTestService fanTest, ILogger<WebSocketHub> log)
    {
        _sensors = sensors;
        _fans    = fans;
        _alerts  = alerts;
        _fanTest = fanTest;
        _log     = log;
    }

    public async Task HandleAsync(HttpContext context)
    {
        using var ws = await context.WebSockets.AcceptWebSocketAsync();
        _log.LogDebug("WebSocket connected: {RemoteIp}", context.Connection.RemoteIpAddress);

        var channel = _sensors.Subscribe();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);

        try
        {
            // Send the current snapshot immediately on connect
            await SendSnapshotAsync(ws, _sensors.Latest, cts.Token);

            while (ws.State == WebSocketState.Open)
            {
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var linked     = CancellationTokenSource.CreateLinkedTokenSource(
                    cts.Token, timeoutCts.Token);

                SensorSnapshot? snap = null;
                try
                {
                    snap = await channel.Reader.ReadAsync(linked.Token);
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
                {
                    // Heartbeat — keeps the connection alive and lets the client detect drops
                    await SendRawAsync(ws, """{"type":"heartbeat"}""", cts.Token);
                    continue;
                }

                await SendSnapshotAsync(ws, snap, cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
        { }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "WebSocket error");
        }
        finally
        {
            _sensors.Unsubscribe(channel);
            if (ws.State == WebSocketState.Open)
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Server closing",
                    CancellationToken.None);
            _log.LogDebug("WebSocket disconnected");
        }
    }

    // -----------------------------------------------------------------------

    private Task SendSnapshotAsync(WebSocket ws, SensorSnapshot snap, CancellationToken ct)
    {
        // Build applied_speeds map from the latest fan status
        var fanStatuses   = _fans.GetAll(snap);
        var appliedSpeeds = fanStatuses.ToDictionary(f => f.FanId, f => f.SpeedPercent);

        // Active alert rule IDs
        var activeEvents = _alerts.GetActiveEvents();
        var activeIds    = activeEvents.Select(e => e.RuleId).ToList();
        var recentEvents = _alerts.GetEvents(20);

        // Fan test progress — null when no tests running (omitted from JSON by WhenWritingNull)
        var testProgress = _fanTest.GetActiveProgress();

        var msg = new WsMessage
        {
            Type          = "sensor_update",
            Timestamp     = snap.Timestamp,
            Readings      = snap.Readings,
            AppliedSpeeds = appliedSpeeds,
            Alerts        = recentEvents,
            ActiveAlerts  = activeIds,
            FanTest       = testProgress.Count > 0 ? testProgress : null,
        };

        var json = JsonSerializer.Serialize(msg, _json);
        return SendRawAsync(ws, json, ct);
    }

    private static Task SendRawAsync(WebSocket ws, string json, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        return ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text,
            endOfMessage: true, ct);
    }
}
