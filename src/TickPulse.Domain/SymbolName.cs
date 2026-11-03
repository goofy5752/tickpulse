using System.Text.RegularExpressions;

namespace TickPulse.Domain;

/// <summary>A normalised market symbol such as <c>BTCUSDT</c>.</summary>
public readonly partial record struct SymbolName
{
    private SymbolName(string value) => Value = value;

    public string Value { get; }

    /// <summary>Trims and upper-cases <paramref name="raw"/>, then validates it against <c>^[A-Z0-9]{5,20}$</c>.</summary>
    public static bool TryCreate(string? raw, out SymbolName name)
    {
        var normalised = raw?.Trim().ToUpperInvariant();
        if (normalised is not null && Pattern().IsMatch(normalised))
        {
            name = new SymbolName(normalised);
            return true;
        }

        name = default;
        return false;
    }

    public override string ToString() => Value;

    [GeneratedRegex("^[A-Z0-9]{5,20}$")]
    private static partial Regex Pattern();
}
