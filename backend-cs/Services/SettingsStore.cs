using System.Text.Json;
using DriveChill.Models;

namespace DriveChill.Services;

/// <summary>
/// Persists runtime-mutable settings and profile/alert/curve data to
/// %APPDATA%\DriveChill\settings.json.
///
/// All public methods are thread-safe (lock-protected reads and atomic file writes).
/// </summary>
public sealed class SettingsStore
{
    private readonly string _path;
    private readonly object _lock = new();

    private StoredData _data;

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented               = true,
        PropertyNameCaseInsensitive = true,
    };

    public SettingsStore(AppSettings appSettings)
    {
        _path = Path.Combine(appSettings.DataDir, "settings.json");
        Directory.CreateDirectory(appSettings.DataDir);
        _data = Load();
    }

    // -----------------------------------------------------------------------
    // Runtime settings
    // -----------------------------------------------------------------------

    public int PollIntervalMs
    {
        get { lock (_lock) return _data.PollIntervalMs; }
        set { lock (_lock) { _data.PollIntervalMs = value; Save(); } }
    }

    public int RetentionDays
    {
        get { lock (_lock) return _data.RetentionDays; }
        set { lock (_lock) { _data.RetentionDays = value; Save(); } }
    }

    public string TempUnit
    {
        get { lock (_lock) return _data.TempUnit; }
        set { lock (_lock) { _data.TempUnit = value; Save(); } }
    }

    public double FanRampRatePctPerSec
    {
        get { lock (_lock) return _data.FanRampRatePctPerSec; }
        set { lock (_lock) { _data.FanRampRatePctPerSec = value; Save(); } }
    }

    public double Deadband
    {
        get { lock (_lock) return _data.Deadband; }
        set { lock (_lock) { _data.Deadband = Math.Max(0.0, value); Save(); } }
    }

    public StoredData GetAll() { lock (_lock) return Clone(_data); }
    public void SetAll(StoredData d) { lock (_lock) { _data = d; Save(); } }

    // -----------------------------------------------------------------------
    // Curves / Alerts / Profiles — stored inside settings.json
    // -----------------------------------------------------------------------

    public IReadOnlyList<FanCurve> LoadCurves()
    {
        lock (_lock) return [.. _data.Curves];
    }

    public void SaveCurves(IReadOnlyList<FanCurve> curves)
    {
        lock (_lock) { _data.Curves = [.. curves]; Save(); }
    }

    public IReadOnlyList<AlertRule> LoadAlerts()
    {
        lock (_lock) return [.. _data.Alerts];
    }

    public void SaveAlerts(IEnumerable<AlertRule> rules)
    {
        lock (_lock) { _data.Alerts = [.. rules]; Save(); }
    }

    public IReadOnlyList<Profile> LoadProfiles()
    {
        lock (_lock) return [.. _data.Profiles];
    }

    public void SaveProfiles(IEnumerable<Profile> profiles)
    {
        lock (_lock) { _data.Profiles = [.. profiles]; Save(); }
    }

    // -----------------------------------------------------------------------
    // Persistence
    // -----------------------------------------------------------------------

    private StoredData Load()
    {
        StoredData data;
        try
        {
            data = File.Exists(_path)
                ? JsonSerializer.Deserialize<StoredData>(File.ReadAllText(_path), _json) ?? new StoredData()
                : new StoredData();
        }
        catch { data = new StoredData(); }

        // Retention migration: old default was 1 day; upgrade to 30 on first run after update.
        if (data.RetentionMigrationVersion < 1)
        {
            if (data.RetentionDays == 1)
                data.RetentionDays = 30;
            data.RetentionMigrationVersion = 1;

            // Persist immediately so a restart does not re-apply the migration.
            try
            {
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(data, _json));
                File.Move(tmp, _path, overwrite: true);
            }
            catch { /* best-effort; will retry on next write */ }
        }

        return data;
    }

    private void Save()
    {
        // Write to temp file then move — atomic on Windows (same drive/volume)
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_data, _json));
        File.Move(tmp, _path, overwrite: true);
    }

    private static StoredData Clone(StoredData d) =>
        JsonSerializer.Deserialize<StoredData>(
            JsonSerializer.Serialize(d, _json), _json) ?? new StoredData();
}

/// <summary>Root object persisted to settings.json.</summary>
public sealed class StoredData
{
    public int    PollIntervalMs            { get; set; } = 1000;
    public int    RetentionDays             { get; set; } = 30;
    public int    RetentionMigrationVersion { get; set; } = 0;
    public string TempUnit                  { get; set; } = "C";
    public double FanRampRatePctPerSec      { get; set; } = 5.0;
    public double Deadband                  { get; set; } = 3.0;

    public List<FanCurve>  Curves   { get; set; } = [];
    public List<AlertRule> Alerts   { get; set; } = [];
    public List<Profile>   Profiles { get; set; } = [];
    public List<ApiKeyRecord> ApiKeys { get; set; } = [];
    public WebhookConfig Webhook { get; set; } = new();
    public List<WebhookDelivery> WebhookDeliveries { get; set; } = [];
}
