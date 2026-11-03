namespace TickPulse.Domain;

/// <summary>A half-open range of bucket starts, <c>[Start, EndExclusive)</c>.</summary>
public readonly record struct SmaWindow(DateTime Start, DateTime EndExclusive)
{
    public bool IsEmpty => Start >= EndExclusive;
}

/// <summary>Computes the bucket window an SMA request covers.</summary>
public static class SmaWindowCalculator
{
    /// <summary>
    /// Without <paramref name="startUtc"/> the window is the <paramref name="n"/> last complete buckets;
    /// an <paramref name="n"/> reaching before <see cref="DateTime.MinValue"/> throws
    /// <see cref="ArgumentOutOfRangeException"/>. With it, the window starts at the first bucket boundary
    /// at or after the offset and is clipped to the last complete bucket. Alignment throws at the extremes
    /// of the <see cref="DateTime"/> range.
    /// </summary>
    public static SmaWindow Compute(Period p, int n, DateTime? startUtc, DateTime nowUtc)
    {
        p.EnsureInitialised();
        ArgumentOutOfRangeException.ThrowIfLessThan(n, 1);
        Guard.RequireUtc(nowUtc, nameof(nowUtc));

        var lastComplete = p.AlignDown(nowUtc);
        var lengthTicks = p.Length.Ticks;

        if (startUtc is not { } s)
        {
            if (n > lastComplete.Ticks / lengthTicks)
            {
                throw new ArgumentOutOfRangeException(nameof(n), n, "The window reaches before the earliest representable time.");
            }

            return new SmaWindow(new DateTime(lastComplete.Ticks - (n * lengthTicks), DateTimeKind.Utc), lastComplete);
        }

        Guard.RequireUtc(s, nameof(startUtc));
        var start = p.AlignUp(s);
        var remaining = (lastComplete.Ticks - start.Ticks) / lengthTicks;
        var end = n >= remaining ? lastComplete : new DateTime(start.Ticks + (n * lengthTicks), DateTimeKind.Utc);
        return new SmaWindow(start, end);
    }
}
