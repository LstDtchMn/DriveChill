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

    public ApiKeyService(SettingsStore store)
    {
        _store = store;
    }

    public IReadOnlyList<ApiKeyRecord> List()
    {
        lock (_lock)
        {
            return [.. _store.GetAll().ApiKeys.OrderByDescending(k => k.CreatedAt)];
        }
    }

    public (ApiKeyRecord Metadata, string PlaintextKey) Create(string name, IEnumerable<string>? scopes = null)
    {
        var key = $"dc_live_{Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_')}";
        var now = DateTimeOffset.UtcNow.ToString("o");
        var normalizedScopes = NormalizeScopes(scopes);
        var record = new ApiKeyRecord
        {
            Id = Guid.NewGuid().ToString("N")[..16],
            Name = name.Trim(),
            KeyPrefix = key[..Math.Min(8, key.Length)],
            KeyHash = Sha256(key),
            Scopes = normalizedScopes,
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
            if (key.Scopes == null || key.Scopes.Count == 0)
                key.Scopes = ["read:sensors"];
            key.LastUsedAt = DateTimeOffset.UtcNow.ToString("o");
            _store.SetAll(data);
            return key;
        }
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
