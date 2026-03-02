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
    public string AppVersion { get; set; } = "1.5.0";

    public double SensorPollInterval    { get; set; } = 1.0;
    /// <summary>Poll interval in milliseconds (derived from SensorPollInterval).</summary>
    public int    PollIntervalMs        => (int)(SensorPollInterval * 1000);
    public int    HistoryRetentionHours { get; set; } = 24;
    public string TempUnit              { get; set; } = "C";

    public string DataDir =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DriveChill");

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

    public string? SslCertFile =>
        string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DRIVECHILL_SSL_CERTFILE"))
            ? null
            : Environment.GetEnvironmentVariable("DRIVECHILL_SSL_CERTFILE")!.Trim();

    public string? SslKeyFile =>
        string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DRIVECHILL_SSL_KEYFILE"))
            ? null
            : Environment.GetEnvironmentVariable("DRIVECHILL_SSL_KEYFILE")!.Trim();

    public bool SslGenerateSelfSigned =>
        string.Equals(
            Environment.GetEnvironmentVariable("DRIVECHILL_SSL_GENERATE_SELF_SIGNED"),
            "true",
            StringComparison.OrdinalIgnoreCase
        );
}
