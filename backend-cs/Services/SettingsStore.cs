using System.Text.Json;
using DriveChill.Models;
using Microsoft.Extensions.Logging;

namespace DriveChill.Services;

/// <summary>
/// Persists runtime-mutable settings and profile/alert/curve data to
/// %APPDATA%\DriveChill\settings.json.
///
/// All public methods are thread-safe: a single lock serialises reads, mutations,
/// and file writes so concurrent callers never interleave or lose updates.
/// </summary>
public sealed class SettingsStore
{
    private readonly string _path;
    private readonly object _lock = new();       // protects in-memory _data
    private readonly object _diskLock = new();   // serializes file writes (held outside _lock)
    private readonly ILogger<SettingsStore>? _logger;

    private StoredData _data;

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented               = true,
        PropertyNameCaseInsensitive = true,
    };

    public SettingsStore(AppSettings appSettings, ILogger<SettingsStore>? logger = null)
    {
        _logger = logger;
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
        set => MutateAndSave(d => d.PollIntervalMs = value);
    }

    public int RetentionDays
    {
        get { lock (_lock) return _data.RetentionDays; }
        set => MutateAndSave(d => d.RetentionDays = value);
    }

    public string TempUnit
    {
        get { lock (_lock) return _data.TempUnit; }
        set => MutateAndSave(d => d.TempUnit = value);
    }

    public double FanRampRatePctPerSec
    {
        get { lock (_lock) return _data.FanRampRatePctPerSec; }
        set => MutateAndSave(d => d.FanRampRatePctPerSec = value);
    }

    public double Deadband
    {
        get { lock (_lock) return _data.Deadband; }
        set => MutateAndSave(d => d.Deadband = Math.Max(0.0, value));
    }

    public StoredData GetAll() { lock (_lock) return Clone(_data); }
    public void SetAll(StoredData d) => MutateAndSave(_ => _data = d);

    // -----------------------------------------------------------------------
    // Curves / Alerts / Profiles — stored inside settings.json
    // -----------------------------------------------------------------------

    public IReadOnlyList<FanCurve> LoadCurves()
    {
        lock (_lock) return [.. _data.Curves];
    }

    public void SaveCurves(IReadOnlyList<FanCurve> curves)
        => MutateAndSave(d => d.Curves = [.. curves]);

    public IReadOnlyList<AlertRule> LoadAlerts()
    {
        lock (_lock) return [.. _data.Alerts];
    }

    public void SaveAlerts(IEnumerable<AlertRule> rules)
        => MutateAndSave(d => d.Alerts = [.. rules]);

    public IReadOnlyList<Profile> LoadProfiles()
    {
        lock (_lock) return [.. _data.Profiles];
    }

    public void SaveProfiles(IEnumerable<Profile> profiles)
        => MutateAndSave(d => d.Profiles = [.. profiles]);

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
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to deserialize {Path}; falling back to defaults", _path);

            // Preserve the corrupt file so the user can recover data manually.
            try
            {
                if (File.Exists(_path))
                {
                    var stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");
                    File.Move(_path, $"{_path}.corrupt-{stamp}");
                }
            }
            catch (Exception renameEx)
            {
                _logger?.LogWarning(renameEx, "Could not rename corrupt settings file");
            }

            data = new StoredData();
        }

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

    /// <summary>
    /// Mutate in-memory state, serialize, and write to disk.
    /// <c>_diskLock</c> is held first to ensure write ordering, then <c>_lock</c>
    /// is held briefly for mutation + serialization. Readers block only during
    /// the fast in-memory serialize (sub-ms), not during disk I/O.
    /// Lock order: _diskLock → _lock (never reversed) prevents deadlocks.
    /// </summary>
    private void MutateAndSave(Action<StoredData> mutate)
    {
        lock (_diskLock)
        {
            string json;
            lock (_lock)
            {
                mutate(_data);
                json = JsonSerializer.Serialize(_data, _json);
            }
            // Disk I/O outside _lock — readers can proceed during file writes.
            if (File.Exists(_path))
                File.Copy(_path, _path + ".bak", overwrite: true);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
        }
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
