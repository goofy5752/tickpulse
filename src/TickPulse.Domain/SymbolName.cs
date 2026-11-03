using System.Text.RegularExpressions;

namespace TickPulse.Domain;

/// <summary>A normalised market symbol such as <c>BTCUSDT</c>.</summary>
public readonly partial record struct SymbolName
{
    private SymbolName(string value) => Value = value;

    public string Value { get; }

    /// <summary>Trims <paramref name="raw"/>, validates it as ASCII against <c>^[A-Za-z0-9]{5,20}$</c>, then upper-cases it.</summary>
    public static bool TryCreate(string? raw, out SymbolName name)
    {
        var trimmed = raw?.Trim();
        if (trimmed is not null && Pattern().IsMatch(trimmed))
        {
            name = new SymbolName(trimmed.ToUpperInvariant());
            return true;
        }

        name = default;
        return false;
    }

    public override string ToString() => Value;

    [GeneratedRegex("^[A-Za-z0-9]{5,20}$")]
    private static partial Regex Pattern();
}
