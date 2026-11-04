using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace TickPulse.Infrastructure.Persistence;

/// <summary>
/// Connection settings TickPulse enforces on every PostgreSQL connection it opens, whatever the configured connection
/// string says: both data sources (and so EF Core and the migrator), the leader lock and the design-time factory.
/// </summary>
/// <remarks>
/// <para><b>GSS encryption is off.</b> Npgsql's default (<c>Prefer</c>) starts a GSSAPI (Kerberos) security context on
/// every new physical connection, before TLS and authentication. TickPulse never uses Kerberos, and in the Alpine
/// (musl) runtime image that native call segfaults when many connections open at once, which kills the process
/// (exit code 139) under load.</para>
/// <para><c>Database:AllowGssEncryption=true</c> lets a mode that the connection string sets explicitly through. Without
/// the opt-in, or when the connection string sets no mode, the mode is <c>Disable</c>.</para>
/// </remarks>
public static partial class NpgsqlConnectionPolicy
{
    /// <summary>Opt-in (default false) that keeps an explicit <c>GSS Encryption Mode</c> of the connection string.</summary>
    public const string AllowGssEncryptionKey = "Database:AllowGssEncryption";

    /// <summary>The value of <see cref="AllowGssEncryptionKey"/>; false when unset.</summary>
    public static bool AllowGssEncryption(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration.GetValue(AllowGssEncryptionKey, defaultValue: false);
    }

    /// <summary>Returns <paramref name="connectionString"/> with the enforced settings applied.</summary>
    public static string Apply(string connectionString, bool allowGssEncryption)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        Apply(builder, allowGssEncryption);
        return builder.ConnectionString;
    }

    /// <summary>Applies the enforced settings to <paramref name="builder"/>, in place.</summary>
    public static void Apply(NpgsqlConnectionStringBuilder builder, bool allowGssEncryption)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (!(allowGssEncryption && SetsGssEncryptionMode(builder)))
        {
            builder.GssEncryptionMode = GssEncryptionMode.Disable;
        }
    }

    /// <summary>
    /// Whether <see cref="Apply(string, bool)"/> replaces a GSS encryption mode other than <c>Disable</c> that
    /// <paramref name="connectionString"/> sets explicitly (worth a warning: the setting is ignored).
    /// </summary>
    public static bool OverridesExplicitMode(string connectionString, bool allowGssEncryption)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        return !allowGssEncryption && SetsGssEncryptionMode(builder) && builder.GssEncryptionMode != GssEncryptionMode.Disable;
    }

    /// <summary>Logs, once per data source build, that the connection string's GSS encryption mode is ignored.</summary>
    internal static void WarnIfOverridden(string connectionString, bool allowGssEncryption, ILoggerFactory? loggerFactory)
    {
        if (loggerFactory is not null && OverridesExplicitMode(connectionString, allowGssEncryption))
        {
            LogModeIgnored(
                loggerFactory.CreateLogger(typeof(NpgsqlConnectionPolicy).FullName!),
                new NpgsqlConnectionStringBuilder(connectionString).GssEncryptionMode);
        }
    }

    // The builder keeps only the keywords that were set, under their canonical name ("GSS Encryption Mode"), whichever
    // synonym or casing the connection string used.
    private static bool SetsGssEncryptionMode(NpgsqlConnectionStringBuilder builder) =>
        builder.Keys.Cast<string>().Any(key =>
            string.Equals(key, "GSS Encryption Mode", StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, nameof(NpgsqlConnectionStringBuilder.GssEncryptionMode), StringComparison.OrdinalIgnoreCase));

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "GSS Encryption Mode={Mode} in the connection string is ignored; TickPulse disables GSS encryption unless "
            + AllowGssEncryptionKey + "=true")]
    private static partial void LogModeIgnored(ILogger logger, GssEncryptionMode mode);
}
