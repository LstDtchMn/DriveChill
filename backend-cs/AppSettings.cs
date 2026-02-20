namespace DriveChill;

public sealed class AppSettings
{
    public string AppName    { get; set; } = "DriveChill";
    public string AppVersion { get; set; } = "1.0.0";

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
}
