using TickPulse.Domain;

namespace TickPulse.Application.Abstractions;

/// <summary>Exchange reference data.</summary>
public interface IExchangeMetadata
{
    /// <summary>Whether the exchange lists <paramref name="name"/> and it is currently trading.</summary>
    /// <exception cref="ExchangeUnavailableException">The exchange cannot answer right now; retrying later may help.</exception>
    Task<bool> IsTradingSymbolAsync(SymbolName name, CancellationToken ct);
}
