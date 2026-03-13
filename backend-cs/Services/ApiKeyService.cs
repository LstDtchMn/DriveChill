using System.Security.Cryptography;
using System.Text;
using DriveChill.Models;

namespace DriveChill.Services;

public sealed class ApiKeyService
{
    private static readonly string[] _scopeDomains =
    [
        "alerts",
        "analytics",
        "auth",
        "drives",
        "fans",
        "machines",
        "notifications",
        "profiles",
        "quiet_hours",
        "sensors",
        "settings",
        "temperature_targets",
        "webhooks",
    ];
    private static readonly HashSet<string> _allowedScopes = new(
        new[] { "*", "read:*", "write:*" }
        .Concat(_scopeDomains.Select(d => $"read:{d}"))
        .Concat(_scopeDomains.Select(d => $"write:{d}")),
        StringComparer.OrdinalIgnoreCase
    );

    private readonly SettingsStore _store;
    private readonly object _lock = new();

    // Buffer LastUsedAt updates to avoid writing the full settings file on every validation.
    // Flushed periodically (every 60s) or on explicit List()/Create()/Revoke().
    private readonly Dictionary<string, string> _pendingLastUsed = new();
    private DateTimeOffset _lastFlush = DateTimeOffset.UtcNow;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(60);

    public ApiKeyService(SettingsStore store)
    {
        _store = store;
    }

    public IReadOnlyList<ApiKeyRecord> List()
    {
        lock (_lock)
        {
            var data = _store.GetAll();
            FlushPendingLastUsed(data);
            return [.. data.ApiKeys.OrderByDescending(k => k.CreatedAt)];
        }
    }

    public (ApiKeyRecord Metadata, string PlaintextKey) Create(
        string name,
        IEnumerable<string>? scopes = null,
        string requestingRole = "admin",
        string? createdByUsername = null)
    {
        var key = $"dc_live_{Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_')}";
        var now = DateTimeOffset.UtcNow.ToString("o");
        var normalizedScopes = NormalizeScopes(scopes);
        // Cap role: a viewer-role caller cannot mint an admin key.
        var effectiveRole = requestingRole == "viewer" ? "viewer" : "admin";
        var record = new ApiKeyRecord
        {
            Id = Guid.NewGuid().ToString("N")[..16],
            Name = name.Trim(),
            KeyPrefix = key[..Math.Min(8, key.Length)],
            KeyHash = Sha256(key),
            Scopes = normalizedScopes,
            Role = effectiveRole,
            CreatedBy = createdByUsername,
            CreatedAt = now,
            RevokedAt = null,
            LastUsedAt = null,
        };

        lock (_lock)
        {
            var data = _store.GetAll();
            data.ApiKeys.Add(record);
            _store.SetAll(data);
        }
        return (record, key);
    }

    public bool Revoke(string keyId)
    {
        lock (_lock)
        {
            var data = _store.GetAll();
            var key = data.ApiKeys.FirstOrDefault(k => k.Id == keyId && k.RevokedAt == null);
            if (key == null) return false;
            key.RevokedAt = DateTimeOffset.UtcNow.ToString("o");
            _store.SetAll(data);
            return true;
        }
    }

    public ApiKeyRecord? Validate(string plaintextKey)
    {
        if (string.IsNullOrWhiteSpace(plaintextKey)) return null;
        var hash = Sha256(plaintextKey.Trim());
        lock (_lock)
        {
            var data = _store.GetAll();
            var key = data.ApiKeys.FirstOrDefault(
                k => k.RevokedAt == null && FixedTimeEqualsUtf8(k.KeyHash, hash)
            );
            if (key == null) return null;

            // Return a shallow copy with effective scopes so we never mutate
            // the in-memory stored object (which is not persisted here).
            var effectiveScopes = key.Scopes is { Count: > 0 }
                ? key.Scopes
                : new List<string> { "read:sensors" };

            var result = new ApiKeyRecord
            {
                Id         = key.Id,
                Name       = key.Name,
                KeyPrefix  = key.KeyPrefix,
                KeyHash    = key.KeyHash,
                Scopes     = effectiveScopes,
                Role       = key.Role,
                CreatedBy  = key.CreatedBy,
                CreatedAt  = key.CreatedAt,
                RevokedAt  = key.RevokedAt,
            };

            // Buffer the LastUsedAt update instead of flushing to disk on every call.
            var now = DateTimeOffset.UtcNow.ToString("o");
            key.LastUsedAt = now;
            result.LastUsedAt = now;
            _pendingLastUsed[key.Id] = now;

            // Flush buffered updates periodically (every 60s).
            if (DateTimeOffset.UtcNow - _lastFlush > FlushInterval)
                FlushPendingLastUsed(data);

            return result;
        }
    }

    /// <summary>
    /// Apply buffered LastUsedAt timestamps and write to disk. Must hold <c>_lock</c>.
    /// </summary>
    private void FlushPendingLastUsed(StoredData? data = null)
    {
        if (_pendingLastUsed.Count == 0) return;
        data ??= _store.GetAll();
        foreach (var (id, ts) in _pendingLastUsed)
        {
            var k = data.ApiKeys.FirstOrDefault(x => x.Id == id);
            if (k != null) k.LastUsedAt = ts;
        }
        _pendingLastUsed.Clear();
        _lastFlush = DateTimeOffset.UtcNow;
        _store.SetAll(data);
    }

    private static List<string> NormalizeScopes(IEnumerable<string>? scopes)
    {
        var raw = scopes?.ToList() ?? ["read:sensors"];
        if (raw.Count == 0)
            throw new ArgumentException("At least one scope is required");

        var normalized = new List<string>(raw.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var scopeValue in raw)
        {
            var scope = (scopeValue ?? string.Empty).Trim().ToLowerInvariant();
            if (scope.Length == 0)
                throw new ArgumentException("Scopes cannot contain empty values");
            if (!_allowedScopes.Contains(scope))
                throw new ArgumentException($"Unsupported API key scope: {scope}");
            if (seen.Add(scope))
                normalized.Add(scope);
        }

        return normalized;
    }

    private static string Sha256(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static bool FixedTimeEqualsUtf8(string a, string b)
    {
        var aBytes = Encoding.UTF8.GetBytes(a);
        var bBytes = Encoding.UTF8.GetBytes(b);
        if (aBytes.Length != bBytes.Length) return false;
        return CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
    }
}
