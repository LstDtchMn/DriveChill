using System.Security.Cryptography;
using System.Text;

namespace DriveChill.Utils;

/// <summary>
/// AES-256-GCM encryption helpers for sensitive credentials stored at rest.
///
/// Format: <c>v1:&lt;base64(12-byte-nonce || ciphertext || 16-byte-tag)&gt;</c>
///
/// When <c>secretKey</c> is null or empty the helpers fall back to plaintext
/// with a log warning so the application works without extra configuration.
/// </summary>
public static class CredentialEncryption
{
    private const string Prefix  = "v1:";
    private const int    NonceLen = 12;
    private const int    TagLen   = 16;
    private static readonly byte[] Aad = "smtp"u8.ToArray();

    /// <summary>
    /// Derive a 256-bit key from the deployment secret.
    /// IMPORTANT: SecretKey MUST be high-entropy (e.g. <c>secrets.token_hex(32)</c>).
    /// A single SHA-256 pass is acceptable for a 256-bit random input; a low-entropy
    /// passphrase would be brute-forceable. See AppSettings.SecretKey docs.
    /// </summary>
    private static byte[] DeriveKey(string secretKey)
        => SHA256.HashData(Encoding.UTF8.GetBytes(secretKey));

    /// <summary>
    /// Encrypt <paramref name="plaintext"/> and return a <c>v1:…</c> ciphertext string.
    /// Returns <paramref name="plaintext"/> unchanged (with a warning) when
    /// <paramref name="secretKey"/> is null or empty.
    /// </summary>
    public static string Encrypt(string plaintext, string? secretKey)
    {
        if (string.IsNullOrEmpty(secretKey))
            return plaintext;   // plaintext fallback — caller should log a warning

        var key   = DeriveKey(secretKey);
        var nonce = new byte[NonceLen];
        RandomNumberGenerator.Fill(nonce);

        var pt  = Encoding.UTF8.GetBytes(plaintext);
        var ct  = new byte[pt.Length];
        var tag = new byte[TagLen];

        using var aes = new AesGcm(key, TagLen);
        aes.Encrypt(nonce, pt, ct, tag, Aad);

        var payload = new byte[NonceLen + ct.Length + TagLen];
        nonce.CopyTo(payload, 0);
        ct.CopyTo(payload, NonceLen);
        tag.CopyTo(payload, NonceLen + ct.Length);

        return Prefix + Convert.ToBase64String(payload);
    }

    /// <summary>
    /// Decrypt a <c>v1:…</c> ciphertext string and return the plaintext.
    /// Returns <paramref name="stored"/> unchanged if it is not encrypted.
    /// Returns an empty string when the key is missing but the value is encrypted.
    /// </summary>
    public static string Decrypt(string stored, string? secretKey)
    {
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal))
            return stored;   // plaintext fallback

        if (string.IsNullOrEmpty(secretKey))
            return string.Empty;   // can't decrypt without key

        try
        {
            var data  = Convert.FromBase64String(stored[Prefix.Length..]);
            var nonce = data[..NonceLen];
            var tag   = data[^TagLen..];
            var ct    = data[NonceLen..^TagLen];
            var pt    = new byte[ct.Length];

            var key = DeriveKey(secretKey);
            using var aes = new AesGcm(key, TagLen);
            aes.Decrypt(nonce, ct, tag, pt, Aad);
            return Encoding.UTF8.GetString(pt);
        }
        catch (CryptographicException)
        {
            // Wrong key or tampered ciphertext — caller gets empty string.
            return string.Empty;
        }
    }

    /// <summary>Returns true if <paramref name="value"/> is an encrypted credential.</summary>
    public static bool IsEncrypted(string value)
        => value.StartsWith(Prefix, StringComparison.Ordinal);
}
