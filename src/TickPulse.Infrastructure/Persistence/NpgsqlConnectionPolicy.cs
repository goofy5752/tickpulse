using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace TickPulse.Infrastructure.Persistence;

/// <summary>The <c>Database</c> settings that <see cref="NpgsqlConnectionPolicy"/> applies.</summary>
/// <param name="AllowGssEncryption"><c>Database:AllowGssEncryption</c>: keep an explicit GSS encryption mode (default false).</param>
/// <param name="MaxPoolSize"><c>Database:MaxPoolSize</c>: the shared pool's size unless the connection string sets one (default 20).</param>
public sealed record NpgsqlPolicySettings(
    bool AllowGssEncryption = false,
    int MaxPoolSize = NpgsqlConnectionPolicy.DefaultMaxPoolSize)
{
    /// <summary>The defaults: GSS encryption off, a pool of <see cref="NpgsqlConnectionPolicy.DefaultMaxPoolSize"/>.</summary>
    public static readonly NpgsqlPolicySettings Default = new();
}

/// <summary>
/// Connection settings TickPulse enforces on every PostgreSQL connection it opens, whatever the configured connection
/// string says: both data sources (and so EF Core and the migrator), the leader lock and the design-time factory.
/// </summary>
/// <remarks>
/// <para><b>GSS encryption is off.</b> Npgsql's default (<c>Prefer</c>) starts a GSSAPI (Kerberos) security context on
/// every new physical connection, before TLS and authentication. TickPulse never uses Kerberos, and in the Alpine
/// (musl) runtime image that native call segfaults when many connections open at once, which kills the process
/// (exit code 139) under load. <c>Database:AllowGssEncryption=true</c> lets a mode that the connection string sets
/// explicitly through. Without the opt-in, or when the connection string sets no mode, the mode is <c>Disable</c>.</para>
/// <para><b>The pool is bounded.</b> Npgsql's default <c>Maximum Pool Size</c> (100) alone matches PostgreSQL's default
/// <c>max_connections</c> (100, of which some are reserved for superusers). Under load the server then refuses
/// connections (<c>53300 too many clients</c>), and every refused open leaves a socket in TIME_WAIT until the API runs
/// out of local ports. The shared pool therefore gets <c>Database:MaxPoolSize</c> (default
/// <see cref="DefaultMaxPoolSize"/>) unless the connection string sets <c>Maximum Pool Size</c> itself. Together with the
/// tick writer's pool and the leader lock, the API needs <see cref="ConnectionBudget"/> connections at most.</para>
/// </remarks>
public static partial class NpgsqlConnectionPolicy
{
    /// <summary>Opt-in (default false) that keeps an explicit <c>GSS Encryption Mode</c> of the connection string.</summary>
    public const string AllowGssEncryptionKey = "Database:AllowGssEncryption";

    /// <summary>Size of the shared pool unless the connection string sets <c>Maximum Pool Size</c>.</summary>
    public const string MaxPoolSizeKey = "Database:MaxPoolSize";

    /// <summary>Default of <see cref="MaxPoolSizeKey"/>.</summary>
    public const int DefaultMaxPoolSize = 20;

    /// <summary>Upper bound Npgsql accepts for <c>Maximum Pool Size</c>.</summary>
    public const int MaxPoolSizeLimit = 1024;

    /// <summary>Connections that must stay free on the server besides the API's (psql, backups, a deploy overlap).</summary>
    public const int ConnectionHeadroom = 10;

    /// <summary>Reads the <c>Database</c> settings; an invalid <see cref="MaxPoolSizeKey"/> fails with its name.</summary>
    /// <exception cref="InvalidOperationException"><see cref="MaxPoolSizeKey"/> is not a whole number from 1 to 1024.</exception>
    public static NpgsqlPolicySettings Settings(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var raw = configuration[MaxPoolSizeKey];
        var maxPoolSize = DefaultMaxPoolSize;
        if (!string.IsNullOrWhiteSpace(raw)
            && (!int.TryParse(raw.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out maxPoolSize)
                || maxPoolSize is < 1 or > MaxPoolSizeLimit))
        {
            throw new InvalidOperationException(string.Create(
                CultureInfo.InvariantCulture,
                $"{MaxPoolSizeKey} must be a whole number from 1 to {MaxPoolSizeLimit}, such as {DefaultMaxPoolSize}."));
        }

        return new NpgsqlPolicySettings(configuration.GetValue(AllowGssEncryptionKey, defaultValue: false), maxPoolSize);
    }

    /// <summary>Returns <paramref name="connectionString"/> with the enforced settings applied.</summary>
    public static string Apply(string connectionString, NpgsqlPolicySettings settings)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        Apply(builder, settings);
        return builder.ConnectionString;
    }

    /// <summary>Applies the enforced settings to <paramref name="builder"/>, in place.</summary>
    public static void Apply(NpgsqlConnectionStringBuilder builder, NpgsqlPolicySettings settings)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(settings);
        if (!(settings.AllowGssEncryption && Sets(builder, "GSS Encryption Mode", nameof(NpgsqlConnectionStringBuilder.GssEncryptionMode))))
        {
            builder.GssEncryptionMode = GssEncryptionMode.Disable;
        }

        if (!Sets(builder, "Maximum Pool Size", nameof(NpgsqlConnectionStringBuilder.MaxPoolSize)))
        {
            builder.MaxPoolSize = settings.MaxPoolSize;
        }

        // Npgsql rejects a minimum above the maximum only when it opens; fail as clearly, but here.
        if (builder.MinPoolSize > builder.MaxPoolSize)
        {
            throw new InvalidOperationException(string.Create(
                CultureInfo.InvariantCulture,
                $"Minimum Pool Size ({builder.MinPoolSize}) is larger than the pool's maximum ({builder.MaxPoolSize})."));
        }
    }

    /// <summary>
    /// The most connections the API opens at once: the shared pool, the tick writer's pool and the leader lock's one
    /// unpooled connection.
    /// </summary>
    public static int ConnectionBudget(int sharedMaxPoolSize, int writerMaxPoolSize) => sharedMaxPoolSize + writerMaxPoolSize + 1;

    /// <summary>
    /// Whether <see cref="Apply(string, NpgsqlPolicySettings)"/> replaces a GSS encryption mode other than
    /// <c>Disable</c> that <paramref name="connectionString"/> sets explicitly (worth a warning: the setting is ignored).
    /// </summary>
    public static bool OverridesExplicitMode(string connectionString, NpgsqlPolicySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        return !settings.AllowGssEncryption
            && Sets(builder, "GSS Encryption Mode", nameof(NpgsqlConnectionStringBuilder.GssEncryptionMode))
            && builder.GssEncryptionMode != GssEncryptionMode.Disable;
    }

    /// <summary>Logs, once per data source build, that the connection string's GSS encryption mode is ignored.</summary>
    internal static void WarnIfOverridden(string connectionString, NpgsqlPolicySettings settings, ILoggerFactory? loggerFactory)
    {
        if (loggerFactory is not null && OverridesExplicitMode(connectionString, settings))
        {
            LogModeIgnored(
                loggerFactory.CreateLogger(typeof(NpgsqlConnectionPolicy).FullName!),
                new NpgsqlConnectionStringBuilder(connectionString).GssEncryptionMode);
        }
    }

    /// <summary>
    /// Compares <paramref name="budget"/> with the connections the server allows for ordinary roles and logs a warning
    /// when fewer than <see cref="ConnectionHeadroom"/> would stay free. Returns the connections available, or
    /// <see langword="null"/> when the server could not be asked (the check never stops startup).
    /// </summary>
    public static async Task<int?> CheckBudgetAsync(NpgsqlDataSource dataSource, int budget, ILogger logger, CancellationToken cancellationToken)
    {
        int available;
        try
        {
            // reserved_connections exists from PostgreSQL 16; current_setting(..., true) is NULL where it does not.
            await using var command = dataSource.CreateCommand(
                """
                SELECT current_setting('max_connections')::int
                     - current_setting('superuser_reserved_connections')::int
                     - COALESCE(current_setting('reserved_connections', true)::int, 0)
                """);
            available = (int)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }
        catch (NpgsqlException ex)
        {
            LogBudgetUnknown(logger, ex);
            return null;
        }

        if (budget > available - ConnectionHeadroom)
        {
            LogBudgetExceeded(logger, budget, available, ConnectionHeadroom, MaxPoolSizeKey);
        }

        return available;
    }

    // The builder keeps only the keywords that were set, under their canonical name, whichever synonym or casing the
    // connection string used.
    private static bool Sets(NpgsqlConnectionStringBuilder builder, string canonicalKeyword, string propertyName) =>
        builder.Keys.Cast<string>().Any(key =>
            string.Equals(key, canonicalKeyword, StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, propertyName, StringComparison.OrdinalIgnoreCase));

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "GSS Encryption Mode={Mode} in the connection string is ignored; TickPulse disables GSS encryption unless "
            + AllowGssEncryptionKey + "=true")]
    private static partial void LogModeIgnored(ILogger logger, GssEncryptionMode mode);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The API may open {Budget} database connections, but PostgreSQL allows {Available} for ordinary roles and "
            + "{Headroom} should stay free; lower {Key} (or Maximum Pool Size) or raise max_connections")]
    private static partial void LogBudgetExceeded(ILogger logger, int budget, int available, int headroom, string key);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Could not read max_connections to check the connection budget")]
    private static partial void LogBudgetUnknown(ILogger logger, Exception exception);
}
