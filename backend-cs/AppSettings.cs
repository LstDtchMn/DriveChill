namespace DriveChill;

public sealed class AppSettings
{
    private static readonly HashSet<string> _localhostHosts =
    [
        "127.0.0.1",
        "localhost",
        "::1",
    ];

    public string AppName    { get; set; } = "DriveChill";
    public string AppVersion { get; set; } = "3.1.0";

    public double SensorPollInterval    { get; set; } = 1.0;
    /// <summary>Poll interval in milliseconds (derived from SensorPollInterval).</summary>
    public int    PollIntervalMs        => (int)(SensorPollInterval * 1000);
    public int    HistoryRetentionHours { get; set; } = 720;
    public string TempUnit              { get; set; } = "C";

    public string DataDir
    {
        get
        {
            var v = Environment.GetEnvironmentVariable("DRIVECHILL_DATA_DIR")?.Trim();
            return string.IsNullOrEmpty(v)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DriveChill")
                : v;
        }
    }

    public string DbPath => Path.Combine(DataDir, "drivechill.db");

    public string Host =>
        (Environment.GetEnvironmentVariable("DRIVECHILL_HOST") ?? "127.0.0.1").Trim();

    public int Port
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable("DRIVECHILL_PORT");
            return int.TryParse(raw, out var port) && port is > 0 and <= 65535
                ? port
                : 8085;
        }
    }

    /// <summary>
    /// Auto-creates an admin user at startup (matches Python DRIVECHILL_PASSWORD).
    /// Required when binding to non-localhost without existing users.
    /// </summary>
    public string? Password
    {
        get
        {
            var v = Environment.GetEnvironmentVariable("DRIVECHILL_PASSWORD")?.Trim();
            return string.IsNullOrEmpty(v) ? null : v;
        }
    }

    public bool ForceAuth =>
        string.Equals(
            Environment.GetEnvironmentVariable("DRIVECHILL_FORCE_AUTH"),
            "true",
            StringComparison.OrdinalIgnoreCase
        );

    public bool AuthRequired
    {
        get
        {
            if (ForceAuth)
                return true;
            var host = Host.Trim().ToLowerInvariant();
            return !_localhostHosts.Contains(host);
        }
    }

    public bool AllowPrivateOutboundTargets =>
        string.Equals(
            Environment.GetEnvironmentVariable("DRIVECHILL_ALLOW_PRIVATE_OUTBOUND_TARGETS"),
            "true",
            StringComparison.OrdinalIgnoreCase
        );

    public bool AllowPrivateBrokerTargets =>
        string.Equals(
            Environment.GetEnvironmentVariable("DRIVECHILL_ALLOW_PRIVATE_BROKER_TARGETS"),
            "true",
            StringComparison.OrdinalIgnoreCase
        );

    public string? SslCertFile
    {
        get
        {
            var v = Environment.GetEnvironmentVariable("DRIVECHILL_SSL_CERTFILE")?.Trim();
            return string.IsNullOrEmpty(v) ? null : v;
        }
    }

    public string? SslKeyFile
    {
        get
        {
            var v = Environment.GetEnvironmentVariable("DRIVECHILL_SSL_KEYFILE")?.Trim();
            return string.IsNullOrEmpty(v) ? null : v;
        }
    }

    public bool SslGenerateSelfSigned =>
        string.Equals(
            Environment.GetEnvironmentVariable("DRIVECHILL_SSL_GENERATE_SELF_SIGNED"),
            "true",
            StringComparison.OrdinalIgnoreCase
        );

    /// <summary>
    /// Deployment secret for encrypting sensitive credentials (e.g. SMTP password) at rest.
    /// Generate with: python -c "import secrets; print(secrets.token_hex(32))"
    /// If unset, credentials are stored in plaintext with a log warning.
    /// </summary>
    public string? SecretKey
    {
        get
        {
            var v = Environment.GetEnvironmentVariable("DRIVECHILL_SECRET_KEY")?.Trim();
            return string.IsNullOrEmpty(v) ? null : v;
        }
    }

    // VAPID keys for Web Push notifications (matches Python backend env vars)
    public string VapidPublicKey =>
        (Environment.GetEnvironmentVariable("DRIVECHILL_VAPID_PUBLIC_KEY") ?? "").Trim();

    public string VapidPrivateKey =>
        (Environment.GetEnvironmentVariable("DRIVECHILL_VAPID_PRIVATE_KEY") ?? "").Trim();

    public string VapidContactEmail =>
        (Environment.GetEnvironmentVariable("DRIVECHILL_VAPID_CONTACT_EMAIL") ?? "admin@localhost").Trim();

    // Panic thresholds — configurable via settings
    public double PanicCpuTemp    { get; set; } = 95.0;
    public double PanicGpuTemp    { get; set; } = 90.0;
    public double PanicHysteresis { get; set; } = 5.0;

    public bool PrometheusEnabled =>
        string.Equals(
            Environment.GetEnvironmentVariable("DRIVECHILL_PROMETHEUS_ENABLED"),
            "true",
            StringComparison.OrdinalIgnoreCase
        );
}
