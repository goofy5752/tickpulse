namespace TickPulse.Domain;

/// <summary>A partial price sum and the number of samples behind it.</summary>
public readonly record struct PriceSumPart(decimal Sum, long Count);

public static class AverageCalculator
{
    /// <summary>Count-weighted mean of the parts rounded to 8 dp (banker's rounding), or null without samples.</summary>
    public static decimal? Combine(IEnumerable<PriceSumPart> parts)
    {
        var sum = 0m;
        var count = 0L;
        foreach (var part in parts)
        {
            sum += part.Sum;
            count += part.Count;
        }

        return count == 0 ? null : Math.Round(sum / count, 8, MidpointRounding.ToEven);
    }
}
