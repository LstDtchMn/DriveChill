using System.Security.Cryptography;
using System.Text;
using DriveChill.Models;

namespace DriveChill.Services;

public sealed class ApiKeyService
{
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

    public (ApiKeyRecord Metadata, string PlaintextKey) Create(string name)
    {
        var key = $"dc_live_{Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_')}";
        var now = DateTimeOffset.UtcNow.ToString("o");
        var record = new ApiKeyRecord
        {
            Id = Guid.NewGuid().ToString("N")[..16],
            Name = name.Trim(),
            KeyPrefix = key[..Math.Min(8, key.Length)],
            KeyHash = Sha256(key),
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
            key.LastUsedAt = DateTimeOffset.UtcNow.ToString("o");
            _store.SetAll(data);
            return key;
        }
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
