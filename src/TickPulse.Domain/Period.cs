namespace TickPulse.Domain;

/// <summary>An SMA bucket period, identified by its code (for example <c>1m</c>).</summary>
public readonly record struct Period(string Code)
{
    public static Period OneMinute { get; } = new("1m");
}
