using Microsoft.Extensions.Options;

namespace TickPulse.Infrastructure.Backfill;

/// <summary>Rejects invalid <see cref="BackfillOptions"/> at startup.</summary>
internal sealed class BackfillOptionsValidator : IValidateOptions<BackfillOptions>
{
    public ValidateOptionsResult Validate(string? name, BackfillOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Validate() is { } error
            ? ValidateOptionsResult.Fail($"{BackfillOptions.SectionName}: {error}")
            : ValidateOptionsResult.Success;
    }
}

/// <summary>Settings of the kline backfill, bound from the <c>Backfill</c> configuration section.</summary>
public sealed class BackfillOptions
{
    public const string SectionName = "Backfill";

    /// <summary>Upper bound on <see cref="Depth"/>, far beyond what any SMA window needs.</summary>
    public static readonly TimeSpan MaxDepth = TimeSpan.FromDays(3 * 365);

    /// <summary>
    /// Whether historical 1-minute candles are fetched from Binance. Off by default: TickPulse then serves collected
    /// data only.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>How far back from now the candles are fetched (26 weeks by default, enough for a 20-week SMA).</summary>
    public TimeSpan Depth { get; set; } = TimeSpan.FromDays(182);

    /// <summary>Returns the first invalid setting, or <see langword="null"/> when all are valid.</summary>
    public string? Validate() =>
        Depth < TimeSpan.FromMinutes(1) || Depth > MaxDepth ? $"{nameof(Depth)} must be between 1 minute and {MaxDepth.TotalDays:0} days."
        : null;
}
