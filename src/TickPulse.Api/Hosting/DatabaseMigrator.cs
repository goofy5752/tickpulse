using Microsoft.EntityFrameworkCore;
using TickPulse.Infrastructure.Persistence;

namespace TickPulse.Api.Hosting;

/// <summary>
/// Applies pending EF Core migrations when <c>Database:RunMigrationsOnStartup</c> is true (the default). It is the
/// first hosted service, so the schema is current before the ingestion and backfill workers start (the tick writer's
/// cached staging table relies on that) and before the server accepts requests. A failed migration stops startup.
/// </summary>
public sealed partial class DatabaseMigrator(
    IServiceScopeFactory scopes,
    IConfiguration configuration,
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

        await using var scope = scopes.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<TickPulseDbContext>().Database;
        var pending = (await database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        await database.MigrateAsync(cancellationToken);
        LogMigrated(logger, pending.Count);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information, Message = "Database schema is current; {Count} migration(s) applied")]
    private static partial void LogMigrated(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Startup migrations are disabled (Database:RunMigrationsOnStartup=false)")]
    private static partial void LogSkipped(ILogger logger);
}
