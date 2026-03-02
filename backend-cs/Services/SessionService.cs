using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace DriveChill.Services;

/// <summary>
/// Manages user authentication sessions using PBKDF2 password hashing (built-in .NET).
/// Implements rate limiting and brute-force protection.
/// </summary>
public sealed class SessionService
{
    private readonly DbService _db;
    private readonly AppSettings _settings;

    // Rate limiting: IP → list of attempt timestamps
    private readonly ConcurrentDictionary<string, List<DateTimeOffset>> _rateLimits = new();
    private const int MaxAttemptsPerMinute = 10;

    // Brute-force lockout: username → (failCount, lockoutUntil)
    private readonly ConcurrentDictionary<string, (int Count, DateTimeOffset? LockUntil)> _lockouts = new();
    private const int MaxFailedAttempts = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    // Session TTL — 8 hours default
    private static readonly TimeSpan SessionTtl = TimeSpan.FromHours(8);

    public SessionService(DbService db, AppSettings settings)
    {
        _db = db;
        _settings = settings;
    }

    /// <summary>Check if any user account has been set up.</summary>
    public Task<bool> UserExistsAsync(CancellationToken ct = default) => _db.UserExistsAsync(ct);

    /// <summary>Create the initial admin user (first-time setup only).</summary>
    public async Task<bool> SetupAsync(string username, string password, CancellationToken ct = default)
    {
        if (await _db.UserExistsAsync(ct)) return false;
        var hash = HashPassword(password);
        await _db.CreateUserAsync(username, hash, ct);
        return true;
    }

    /// <summary>Check rate limit for an IP. Returns true if allowed.</summary>
    public bool CheckRateLimit(string ip)
    {
        var now = DateTimeOffset.UtcNow;
        var cutoff = now.AddMinutes(-1);
        var attempts = _rateLimits.GetOrAdd(ip, _ => []);
        lock (attempts)
        {
            attempts.RemoveAll(t => t < cutoff);
            // Evict the entry when there are no recent attempts to prevent
            // the dictionary growing unboundedly with stale IP entries.
            if (attempts.Count == 0)
                _rateLimits.TryRemove(ip, out _);
            if (attempts.Count >= MaxAttemptsPerMinute) return false;
            attempts.Add(now);
        }
        return true;
    }

    /// <summary>
    /// Attempt login. Returns (sessionToken, csrfToken) on success, null on failure.
    /// </summary>
    public async Task<(string SessionToken, string CsrfToken)?> LoginAsync(
        string username, string password, string? ip, string? userAgent,
        CancellationToken ct = default)
    {
        // Check lockout
        if (_lockouts.TryGetValue(username, out var lockout)
            && lockout.LockUntil.HasValue
            && lockout.LockUntil.Value > DateTimeOffset.UtcNow)
            return null;

        var user = await _db.GetUserAsync(username, ct);
        if (user == null || !VerifyPassword(password, user.Value.PasswordHash))
        {
            // Record failed attempt
            _lockouts.AddOrUpdate(username,
                _ => (1, null),
                (_, existing) =>
                {
                    var count = existing.Count + 1;
                    return count >= MaxFailedAttempts
                        ? (count, DateTimeOffset.UtcNow.Add(LockoutDuration))
                        : (count, existing.LockUntil);
                });
            return null;
        }

        // Clear lockout on success
        _lockouts.TryRemove(username, out _);

        // Create session
        var sessionToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var csrfToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        await _db.CreateSessionAsync(sessionToken, csrfToken, username, ip, userAgent, SessionTtl, ct);
        return (sessionToken, csrfToken);
    }

    /// <summary>Validate a session token. Returns username if valid.</summary>
    public async Task<(string Username, string CsrfToken)?> ValidateSessionAsync(
        string? token, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(token)) return null;
        return await _db.ValidateSessionAsync(token, ct);
    }

    /// <summary>Destroy a session (logout).</summary>
    public async Task LogoutAsync(string? token, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(token))
            await _db.DeleteSessionAsync(token, ct);
    }

    // -----------------------------------------------------------------------
    // PBKDF2 password hashing (built-in .NET, no external NuGet)
    // -----------------------------------------------------------------------

    private static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);
        return $"{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
    }

    private static bool VerifyPassword(string password, string stored)
    {
        var parts = stored.Split(':');
        if (parts.Length != 2) return false;
        var salt = Convert.FromBase64String(parts[0]);
        var expectedHash = Convert.FromBase64String(parts[1]);
        var actualHash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(expectedHash, actualHash);
    }
}
