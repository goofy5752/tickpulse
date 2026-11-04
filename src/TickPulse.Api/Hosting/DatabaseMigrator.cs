using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TickPulse.Infrastructure;
using TickPulse.Infrastructure.Persistence;

namespace TickPulse.Api.Hosting;

/// <summary>
/// Applies pending EF Core migrations when <c>Database:RunMigrationsOnStartup</c> is true (the default). It is the
/// first hosted service, so the schema is current before the ingestion and backfill workers start (the tick writer's
/// cached staging table relies on that) and before the server accepts requests.
/// </summary>
/// <remarks>
/// Transient Npgsql failures (the database still starting, a dropped connection) are retried up to
/// <see cref="MigrationRetryOptions.MaxAttempts"/> times with exponential backoff, all within
/// <see cref="MigrationRetryOptions.Timeout"/>. Anything beyond that (a database that stays down, a failing
/// migration) stops startup, and the container restart policy starts the process again.
/// </remarks>
public sealed partial class DatabaseMigrator(
    IServiceScopeFactory scopes,
    IConfiguration configuration,
    TimeProvider clock,
    ILogger<DatabaseMigrator> logger) : IHostedService
{
    public const string EnabledKey = "Database:RunMigrationsOnStartup";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!configuration.GetValue(EnabledKey, defaultValue: true))
        {
            LogSkipped(logger);
            return;
        }

        await RunWithRetryAsync(MigrateOnceAsync, MigrationRetryOptions.Default, clock, logger, cancellationToken);
        await CheckConnectionBudgetAsync(cancellationToken);
    }

    // The database is reachable now. A pool that does not fit the server's max_connections only shows under load, as
    // refused connections; say so at startup instead.
    private async Task CheckConnectionBudgetAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var shared = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var writer = scope.ServiceProvider.GetRequiredKeyedService<NpgsqlDataSource>(DependencyInjection.TickWriterDataSourceKey);
        var budget = NpgsqlConnectionPolicy.ConnectionBudget(MaxPoolSize(shared), MaxPoolSize(writer));
        await NpgsqlConnectionPolicy.CheckBudgetAsync(shared, budget, logger, cancellationToken);
    }

    private static int MaxPoolSize(NpgsqlDataSource dataSource) =>
        new NpgsqlConnectionStringBuilder(dataSource.ConnectionString).MaxPoolSize;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Runs <paramref name="migrate"/>, retrying transient Npgsql failures with exponential backoff within an overall
    /// timeout. Non-transient failures and the last transient one are rethrown.
    /// </summary>
    /// <exception cref="TimeoutException">The attempts together took longer than <see cref="MigrationRetryOptions.Timeout"/>.</exception>
    internal static async Task RunWithRetryAsync(
        Func<CancellationToken, Task> migrate,
        MigrationRetryOptions options,
        TimeProvider clock,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(options.Timeout, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await migrate(linked.Token);
                    return;
                }
                catch (Exception ex) when (attempt < options.MaxAttempts && IsTransient(ex))
                {
                    var delay = RetryDelay(options.BaseDelay, attempt);
                    LogRetrying(logger, attempt, options.MaxAttempts, delay.TotalSeconds, ex);
                    await Task.Delay(delay, clock, linked.Token);
                }
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(string.Create(
                CultureInfo.InvariantCulture,
                $"Database migrations did not finish within {options.Timeout.TotalSeconds:0} s."));
        }
    }

    /// <summary>The wait after the <paramref name="failedAttempts"/>-th failure: base, 2× base, 4× base, …</summary>
    internal static TimeSpan RetryDelay(TimeSpan baseDelay, int failedAttempts) =>
        baseDelay * Math.Pow(2, failedAttempts - 1);

    private static bool IsTransient(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is NpgsqlException { IsTransient: true })
            {
                return true;
            }
        }

        return false;
    }

    private async Task MigrateOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<TickPulseDbContext>().Database;
        var pending = (await database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        await database.MigrateAsync(cancellationToken);
        LogMigrated(logger, pending.Count);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Database schema is current; {Count} migration(s) applied")]
    private static partial void LogMigrated(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Startup migrations are disabled (Database:RunMigrationsOnStartup=false)")]
    private static partial void LogSkipped(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Database migration attempt {Attempt}/{MaxAttempts} failed transiently; retrying in {DelaySeconds:F1} s")]
    private static partial void LogRetrying(ILogger logger, int attempt, int maxAttempts, double delaySeconds, Exception exception);
}

/// <summary>Retry bounds of <see cref="DatabaseMigrator"/>.</summary>
/// <param name="MaxAttempts">Attempts in total, including the first.</param>
/// <param name="BaseDelay">Wait after the first failure; it doubles after each further one.</param>
/// <param name="Timeout">Upper bound on all attempts and waits together.</param>
internal sealed record MigrationRetryOptions(int MaxAttempts, TimeSpan BaseDelay, TimeSpan Timeout)
{
    /// <summary>5 attempts, 1 s → 2 s → 4 s → 8 s between them, at most 2 minutes in total.</summary>
    public static readonly MigrationRetryOptions Default = new(5, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(2));
}
