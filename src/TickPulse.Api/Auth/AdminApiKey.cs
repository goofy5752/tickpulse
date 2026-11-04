using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace TickPulse.Api.Auth;

/// <summary>
/// The configured admin key, held only as the SHA-256 digest of its UTF-8 bytes. A presented key is hashed the same way
/// and the two 32-byte digests are compared with <see cref="CryptographicOperations.FixedTimeEquals"/>, so the time
/// taken reveals neither the key's content nor its length.
/// </summary>
public sealed class AdminApiKey
{
    /// <summary>Keys shorter than this are accepted, with a warning at startup.</summary>
    public const int RecommendedMinLength = 32;

    private readonly byte[]? _digest;

    public AdminApiKey(IOptions<AdminOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var key = options.Value.ApiKey;
        if (!string.IsNullOrWhiteSpace(key))
        {
            _digest = SHA256.HashData(Encoding.UTF8.GetBytes(key));
            Length = key.Length;
        }
    }

    /// <summary>False when no key is configured (unset, empty or blank): the admin endpoints are then disabled.</summary>
    public bool IsConfigured => _digest is not null;

    /// <summary>The configured key's length in characters, or 0.</summary>
    public int Length { get; }

    /// <summary>Whether <paramref name="presented"/> is the configured key; always false when none is configured.</summary>
    public bool Matches(string? presented)
    {
        if (_digest is null)
        {
            return false;
        }

        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(presented ?? string.Empty), digest);
        return CryptographicOperations.FixedTimeEquals(digest, _digest);
    }
}
