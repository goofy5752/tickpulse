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
    /// Without <paramref name="startUtc"/> the window is the <paramref name="n"/> last complete buckets.
    /// With it, the window starts at the first bucket boundary at or after the offset and is clipped
    /// to the last complete bucket.
    /// </summary>
    public static SmaWindow Compute(Period p, int n, DateTime? startUtc, DateTime nowUtc)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(n, 1);
        RequireUtc(nowUtc, nameof(nowUtc));

        var lastComplete = p.AlignDown(nowUtc);
        var span = p.Length * n;

        if (startUtc is not { } s)
        {
            return new SmaWindow(lastComplete - span, lastComplete);
        }

        RequireUtc(s, nameof(startUtc));
        var start = p.AlignUp(s);
        var end = start + span;
        return new SmaWindow(start, end < lastComplete ? end : lastComplete);
    }

    private static void RequireUtc(DateTime value, string name)
    {
        if (value.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Timestamp must have Kind=Utc.", name);
        }
    }
}
