using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DriveChill.Models;
using DriveChill.Utils;
using Prometheus;

namespace DriveChill.Services;

public sealed class WebhookService
{
    private readonly SettingsStore _store;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AppSettings _appSettings;
    private readonly object _lock = new();

    // ── Integration health tracking ──────────────────────────────────────
    private long _successCount;
    private long _failureCount;
    private volatile string? _lastError;
    private readonly object _deliveryTimeLock = new();
    private DateTimeOffset? _lastDeliveryAt;

    public long SuccessCount => Interlocked.Read(ref _successCount);
    public long FailureCount => Interlocked.Read(ref _failureCount);
    public DateTimeOffset? LastDeliveryAt { get { lock (_deliveryTimeLock) return _lastDeliveryAt; } }
    public string? LastError => _lastError;

    public WebhookService(SettingsStore store, IHttpClientFactory httpClientFactory, AppSettings appSettings)
    {
        _store = store;
        _httpClientFactory = httpClientFactory;
        _appSettings = appSettings;
    }

    public WebhookConfig GetConfigRaw()
    {
        lock (_lock) return _store.GetAll().Webhook;
    }

    public WebhookConfigView GetConfig()
    {
        lock (_lock) return WebhookConfigView.FromConfig(_store.GetAll().Webhook);
    }

    public async Task<WebhookConfig> UpdateConfigAsync(WebhookConfig cfg)
    {
        cfg.TargetUrl = cfg.TargetUrl?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(cfg.TargetUrl))
        {
            var (valid, reason) = await UrlSecurity.TryValidateOutboundHttpUrlAsync(
                cfg.TargetUrl,
                _appSettings.AllowPrivateOutboundTargets);
            if (!valid)
                throw new ArgumentException(reason ?? "target_url is not allowed");
        }

        cfg.UpdatedAt = DateTimeOffset.UtcNow.ToString("o");
        cfg.TimeoutSeconds = Math.Clamp(cfg.TimeoutSeconds, 0.5, 30.0);
        cfg.MaxRetries = Math.Clamp(cfg.MaxRetries, 0, 10);
        cfg.RetryBackoffSeconds = Math.Clamp(cfg.RetryBackoffSeconds, 0.1, 30.0);

        lock (_lock)
        {
            var data = _store.GetAll();
            data.Webhook = cfg;
            _store.SetAll(data);
            return data.Webhook;
        }
    }

    public IReadOnlyList<WebhookDelivery> GetDeliveries(int limit = 100, int offset = 0)
    {
        var safeLimit  = Math.Clamp(limit, 1, 500);
        var safeOffset = Math.Max(0, offset);
        lock (_lock)
        {
            return _store.GetAll().WebhookDeliveries
                .OrderByDescending(d => d.Timestamp)
                .Skip(safeOffset)
                .Take(safeLimit)
                .ToList();
        }
    }

    public async Task DispatchAlertEventsAsync(IEnumerable<AlertEvent> events, CancellationToken ct = default)
    {
        var eventList = events.ToList();
        if (eventList.Count == 0) return;

        var cfg = GetConfigRaw();
        if (!cfg.Enabled || string.IsNullOrWhiteSpace(cfg.TargetUrl)) return;

        var payload = new
        {
            event_type = "alert_triggered",
            timestamp = DateTimeOffset.UtcNow.ToString("o"),
            events = eventList.Select(e => new
            {
                rule_id = e.RuleId,
                sensor_id = e.SensorId,
                sensor_name = e.SensorName,
                threshold = e.Threshold,
                actual_value = e.ActualValue,
                timestamp = e.FiredAt,
                message = e.Message,
            }).ToList(),
        };

        var json = JsonSerializer.Serialize(payload);
        var body = Encoding.UTF8.GetBytes(json);

        var client = _httpClientFactory.CreateClient("webhooks");

        for (var attempt = 1; attempt <= cfg.MaxRetries + 1; attempt++)
        {
            var sw = Stopwatch.StartNew();
            int? statusCode = null;
            string? error = null;
            var success = false;

            // Generate fresh timestamp + nonce per attempt so that receivers
            // implementing replay protection accept retries (mirrors Python behaviour).
            var signedTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(cfg.TimeoutSeconds));

                using var req = new HttpRequestMessage(HttpMethod.Post, cfg.TargetUrl);
                req.Content = new ByteArrayContent(body);
                req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                req.Headers.Add("X-DriveChill-Timestamp", signedTimestamp);
                req.Headers.Add("X-DriveChill-Nonce", nonce);
                if (!string.IsNullOrWhiteSpace(cfg.SigningSecret))
                {
                    // Signature format: HMAC-SHA256(key, "{timestamp}.{nonce}.{body}")
                    // Matches Python: hmac.new(secret, f"{ts}.{nonce}.".encode() + body, sha256).hexdigest()
                    var prefix = Encoding.UTF8.GetBytes($"{signedTimestamp}.{nonce}.");
                    var message = new byte[prefix.Length + body.Length];
                    prefix.CopyTo(message, 0);
                    body.CopyTo(message, prefix.Length);
                    var sig = "sha256=" + Convert.ToHexString(
                        HMACSHA256.HashData(Encoding.UTF8.GetBytes(cfg.SigningSecret), message)
                    ).ToLowerInvariant();
                    req.Headers.Add("X-DriveChill-Signature", sig);
                }
                using var resp = await client.SendAsync(req, timeoutCts.Token);
                statusCode = (int)resp.StatusCode;
                success = resp.IsSuccessStatusCode;
                if (!success) error = $"HTTP {(int)resp.StatusCode}";
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                error = $"Request timed out after {cfg.TimeoutSeconds}s";
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            sw.Stop();
            DriveChillMetrics.WebhookDeliveriesTotal.WithLabels(success ? "true" : "false").Inc();
            AddDelivery(new WebhookDelivery
            {
                Timestamp = DateTimeOffset.UtcNow.ToString("o"),
                EventType = "alert_triggered",
                TargetUrl = UrlSecurity.RedactUrlForLog(cfg.TargetUrl),
                Attempt = attempt,
                Success = success,
                HttpStatus = statusCode,
                LatencyMs = (int)sw.ElapsedMilliseconds,
                Error = error,
            });

            if (success)
            {
                lock (_deliveryTimeLock) _lastDeliveryAt = DateTimeOffset.UtcNow;
                Interlocked.Increment(ref _successCount);
                _lastError = null;
                return;
            }
            else
            {
                Interlocked.Increment(ref _failureCount);
                _lastError = error;
            }
            if (attempt <= cfg.MaxRetries)
                await Task.Delay(
                    TimeSpan.FromSeconds(Math.Min(cfg.RetryBackoffSeconds * Math.Pow(2, attempt - 1), 60.0)),
                    ct
                );
        }
    }

    private void AddDelivery(WebhookDelivery delivery)
    {
        lock (_lock)
        {
            var data = _store.GetAll();
            data.WebhookDeliveries.Add(delivery);
            while (data.WebhookDeliveries.Count > 1000) data.WebhookDeliveries.RemoveAt(0);
            _store.SetAll(data);
        }
    }
}
