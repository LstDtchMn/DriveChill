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

    // --- Env-var backed properties (cached at construction) ---

    public string DataDir { get; }
    public string DbPath => Path.Combine(DataDir, "drivechill.db");
    public string Host { get; }
    public int Port { get; }
    public string? Password { get; }
    public bool ForceAuth { get; }
    public bool AllowPrivateOutboundTargets { get; }
    public bool AllowPrivateBrokerTargets { get; }
    public string? SslCertFile { get; }
    public string? SslKeyFile { get; }
    public bool SslGenerateSelfSigned { get; }
    public string? SecretKey { get; }
    public string VapidPublicKey { get; }
    public string VapidPrivateKey { get; }
    public string VapidContactEmail { get; }
    public bool PrometheusEnabled { get; }

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

    // Panic thresholds — configurable via settings
    public double PanicCpuTemp    { get; set; } = 95.0;
    public double PanicGpuTemp    { get; set; } = 90.0;
    public double PanicHysteresis { get; set; } = 5.0;

    public AppSettings()
    {
        // Cache all env-var reads at construction time — these never change after process start.
        DataDir = ReadEnvString("DRIVECHILL_DATA_DIR")
                  ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DriveChill");

        Host = ReadEnvString("DRIVECHILL_HOST") ?? "127.0.0.1";

        var rawPort = Environment.GetEnvironmentVariable("DRIVECHILL_PORT");
        Port = int.TryParse(rawPort, out var port) && port is > 0 and <= 65535 ? port : 8085;

        Password = ReadEnvString("DRIVECHILL_PASSWORD");
        ForceAuth = ReadEnvBool("DRIVECHILL_FORCE_AUTH");
        AllowPrivateOutboundTargets = ReadEnvBool("DRIVECHILL_ALLOW_PRIVATE_OUTBOUND_TARGETS");
        AllowPrivateBrokerTargets = ReadEnvBool("DRIVECHILL_ALLOW_PRIVATE_BROKER_TARGETS");
        SslCertFile = ReadEnvString("DRIVECHILL_SSL_CERTFILE");
        SslKeyFile = ReadEnvString("DRIVECHILL_SSL_KEYFILE");
        SslGenerateSelfSigned = ReadEnvBool("DRIVECHILL_SSL_GENERATE_SELF_SIGNED");
        SecretKey = ReadEnvString("DRIVECHILL_SECRET_KEY");
        VapidPublicKey = (Environment.GetEnvironmentVariable("DRIVECHILL_VAPID_PUBLIC_KEY") ?? "").Trim();
        VapidPrivateKey = (Environment.GetEnvironmentVariable("DRIVECHILL_VAPID_PRIVATE_KEY") ?? "").Trim();
        VapidContactEmail = (Environment.GetEnvironmentVariable("DRIVECHILL_VAPID_CONTACT_EMAIL") ?? "admin@localhost").Trim();
        PrometheusEnabled = ReadEnvBool("DRIVECHILL_PROMETHEUS_ENABLED");
    }

    private static string? ReadEnvString(string name)
    {
        var v = Environment.GetEnvironmentVariable(name)?.Trim();
        return string.IsNullOrEmpty(v) ? null : v;
    }

    private static bool ReadEnvBool(string name)
        => string.Equals(Environment.GetEnvironmentVariable(name), "true", StringComparison.OrdinalIgnoreCase);
}
