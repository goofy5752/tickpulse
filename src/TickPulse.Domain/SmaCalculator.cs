namespace TickPulse.Domain;

public static class SmaCalculator
{
    /// <summary>Mean of the bucket closes rounded to 8 dp (banker's rounding), or null when empty.</summary>
    public static decimal? Average(IReadOnlyList<PricePoint> points)
    {
        if (points.Count == 0)
        {
            return null;
        }

        var sum = 0m;
        foreach (var point in points)
        {
            sum += point.Close;
        }

        return Math.Round(sum / points.Count, 8, MidpointRounding.ToEven);
    }
}
