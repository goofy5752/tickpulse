namespace TickPulse.Domain;

internal static class Guard
{
    public static void RequireUtc(DateTime value, string paramName)
    {
        if (value.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Timestamp must have Kind=Utc.", paramName);
        }
    }
}
