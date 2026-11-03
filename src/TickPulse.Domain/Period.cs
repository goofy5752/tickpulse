namespace TickPulse.Domain;

/// <summary>An SMA bucket period, identified by its code (for example <c>1m</c>).</summary>
public readonly record struct Period
{
    /// <summary>Common bucket origin: Monday 2000-01-03T00:00:00Z, so week buckets start on Mondays.</summary>
    public static readonly DateTime Origin = new(2000, 1, 3, 0, 0, 0, DateTimeKind.Utc);

    private Period(string code, TimeSpan length)
    {
        Code = code;
        Length = length;
    }

    public static Period OneMinute { get; } = new("1m", TimeSpan.FromMinutes(1));

    public static Period FiveMinutes { get; } = new("5m", TimeSpan.FromMinutes(5));

    public static Period ThirtyMinutes { get; } = new("30m", TimeSpan.FromMinutes(30));

    public static Period OneDay { get; } = new("1d", TimeSpan.FromDays(1));

    public static Period OneWeek { get; } = new("1w", TimeSpan.FromDays(7));

    public static IReadOnlyList<Period> All { get; } = [OneMinute, FiveMinutes, ThirtyMinutes, OneDay, OneWeek];

    public string Code { get; }

    public TimeSpan Length { get; }

    /// <summary>Parses an exact, case-sensitive period code.</summary>
    public static bool TryParse(string? code, out Period period)
    {
        foreach (var candidate in All)
        {
            if (string.Equals(candidate.Code, code, StringComparison.Ordinal))
            {
                period = candidate;
                return true;
            }
        }

        period = default;
        return false;
    }

    /// <summary>
    /// Start of the bucket containing <paramref name="utc"/>. Throws <see cref="ArgumentOutOfRangeException"/>
    /// when the result is not representable (at the extremes of the <see cref="DateTime"/> range).
    /// </summary>
    public DateTime AlignDown(DateTime utc)
    {
        EnsureInitialised();
        Guard.RequireUtc(utc, nameof(utc));

        var offset = utc.Ticks - Origin.Ticks;
        var buckets = Math.DivRem(offset, Length.Ticks, out var remainder);
        if (remainder < 0)
        {
            buckets--;
        }

        return new DateTime(Origin.Ticks + (buckets * Length.Ticks), DateTimeKind.Utc);
    }

    /// <summary>
    /// The timestamp itself when on a boundary, otherwise the start of the next bucket. Throws
    /// <see cref="ArgumentOutOfRangeException"/> at the extremes of the <see cref="DateTime"/> range.
    /// </summary>
    public DateTime AlignUp(DateTime utc)
    {
        var down = AlignDown(utc);
        return down == utc ? utc : down + Length;
    }

    internal void EnsureInitialised()
    {
        if (Length <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Uninitialised Period.");
        }
    }
}
