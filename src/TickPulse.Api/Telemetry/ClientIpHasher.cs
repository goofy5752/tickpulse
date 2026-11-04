using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace TickPulse.Api.Telemetry;

/// <summary>
/// Pseudonymises client IPs for logs: the first 12 hex characters of SHA-256 over the salt, the UTC date and the IP.
/// The date makes the hash rotate daily, so one visitor cannot be followed across days. Raw IPs are never logged.
/// </summary>
public sealed class ClientIpHasher
{
    /// <summary>Configuration key of the salt.</summary>
    public const string SaltKey = "Otel:IpSalt";

    /// <summary>Returned when the request has no remote address.</summary>
    public const string Unknown = "unknown";

    private readonly string _salt;
    private readonly TimeProvider _clock;

    /// <param name="salt">Secret salt; a random one for this instance when empty (hashes then differ between processes).</param>
    /// <param name="clock">Clock that decides the day.</param>
    public ClientIpHasher(string? salt, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _salt = string.IsNullOrEmpty(salt) ? Convert.ToHexString(RandomNumberGenerator.GetBytes(32)) : salt;
        _clock = clock;
    }

    public string Hash(IPAddress? address)
    {
        if (address is null)
        {
            return Unknown;
        }

        var normalized = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        var day = _clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes($"{_salt}|{day}|{normalized}"), digest);
        return Convert.ToHexStringLower(digest[..6]);
    }
}
