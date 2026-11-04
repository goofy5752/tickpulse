using Microsoft.Extensions.Options;

namespace TickPulse.Infrastructure.Ingestion;

/// <summary>Rejects invalid <see cref="IngestionOptions"/> at startup.</summary>
internal sealed class IngestionOptionsValidator : IValidateOptions<IngestionOptions>
{
    public ValidateOptionsResult Validate(string? name, IngestionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Validate() is { } error
            ? ValidateOptionsResult.Fail($"{IngestionOptions.SectionName}: {error}")
            : ValidateOptionsResult.Success;
    }
}

/// <summary>Settings of the ingestion worker, bound from the <c>Ingestion</c> configuration section.</summary>
public sealed class IngestionOptions
{
    public const string SectionName = "Ingestion";

    /// <summary>Binance closes a connection after 24 h; the worker replaces it earlier (make-before-break).</summary>
    public static readonly TimeSpan MaxConnectionLifetime = TimeSpan.FromHours(24);

    /// <summary>Age at which the connection is replaced. Must be below 24 h.</summary>
    public TimeSpan RotationInterval { get; set; } = TimeSpan.FromHours(23);

    /// <summary>A batch is written once it holds this many ticks.</summary>
    public int BatchMaxSize { get; set; } = 5000;

    /// <summary>A batch is written at the latest this long after its first tick arrived.</summary>
    public TimeSpan BatchMaxDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Bound of the tick channel between the WebSocket and the batch writer (back-pressure when full).</summary>
    public int ChannelCapacity { get; set; } = 100_000;

    /// <summary>How often the subscriptions are compared with the active symbols in the database.</summary>
    public TimeSpan ReconcileInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How often a standby instance tries to take the leader lock.</summary>
    public TimeSpan LeaderRetryInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How often the leader probes its lock connection; a failed probe stops ingestion.</summary>
    public TimeSpan LeaderCheckInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Returns the first invalid setting, or <see langword="null"/> when all are valid.</summary>
    public string? Validate() =>
        RotationInterval <= TimeSpan.Zero || RotationInterval >= MaxConnectionLifetime ? $"{nameof(RotationInterval)} must be between 0 and 24 h."
        : BatchMaxSize < 1 ? $"{nameof(BatchMaxSize)} must be at least 1."
        : BatchMaxDelay <= TimeSpan.Zero ? $"{nameof(BatchMaxDelay)} must be positive."
        : ChannelCapacity < 1 ? $"{nameof(ChannelCapacity)} must be at least 1."
        : ReconcileInterval <= TimeSpan.Zero ? $"{nameof(ReconcileInterval)} must be positive."
        : LeaderRetryInterval <= TimeSpan.Zero ? $"{nameof(LeaderRetryInterval)} must be positive."
        : LeaderCheckInterval <= TimeSpan.Zero ? $"{nameof(LeaderCheckInterval)} must be positive."
        : null;
}
