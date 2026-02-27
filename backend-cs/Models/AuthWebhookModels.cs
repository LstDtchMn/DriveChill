namespace DriveChill.Models;

public sealed class ApiKeyRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..16];
    public string Name { get; set; } = "";
    public string KeyPrefix { get; set; } = "";
    public string KeyHash { get; set; } = "";
    public string CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToString("o");
    public string? RevokedAt { get; set; }
    public string? LastUsedAt { get; set; }
}

public sealed class CreateApiKeyRequest
{
    public string Name { get; set; } = "";
}

public sealed class WebhookConfig
{
    public bool Enabled { get; set; } = false;
    public string TargetUrl { get; set; } = "";
    public string? SigningSecret { get; set; }
    public double TimeoutSeconds { get; set; } = 3.0;
    public int MaxRetries { get; set; } = 2;
    public double RetryBackoffSeconds { get; set; } = 1.0;
    public string UpdatedAt { get; set; } = DateTimeOffset.UtcNow.ToString("o");
}

public sealed class WebhookConfigView
{
    public bool Enabled { get; set; } = false;
    public string TargetUrl { get; set; } = "";
    public bool HasSigningSecret { get; set; } = false;
    public double TimeoutSeconds { get; set; } = 3.0;
    public int MaxRetries { get; set; } = 2;
    public double RetryBackoffSeconds { get; set; } = 1.0;
    public string UpdatedAt { get; set; } = DateTimeOffset.UtcNow.ToString("o");

    public static WebhookConfigView FromConfig(WebhookConfig cfg) => new()
    {
        Enabled = cfg.Enabled,
        TargetUrl = cfg.TargetUrl,
        HasSigningSecret = !string.IsNullOrWhiteSpace(cfg.SigningSecret),
        TimeoutSeconds = cfg.TimeoutSeconds,
        MaxRetries = cfg.MaxRetries,
        RetryBackoffSeconds = cfg.RetryBackoffSeconds,
        UpdatedAt = cfg.UpdatedAt,
    };
}

public sealed class WebhookDelivery
{
    public string Timestamp { get; set; } = DateTimeOffset.UtcNow.ToString("o");
    public string EventType { get; set; } = "";
    public string TargetUrl { get; set; } = "";
    public int Attempt { get; set; }
    public bool Success { get; set; }
    public int? HttpStatus { get; set; }
    public int? LatencyMs { get; set; }
    public string? Error { get; set; }
}
