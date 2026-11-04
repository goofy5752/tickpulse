using TickPulse.Application.Abstractions;
using TickPulse.Domain;

namespace TickPulse.Infrastructure.Binance;

/// <summary>
/// <see cref="IExchangeMetadata"/> over <see cref="BinanceRestClient"/>: it reports the failures that retrying later
/// can fix (a transient <see cref="BinanceApiException"/>, a <see cref="BinanceBannedException"/>) as
/// <see cref="ExchangeUnavailableException"/>, so the use cases never see Binance's own exception types. Permanent
/// failures (a rejected request, a malformed payload) still propagate as they are: they are bugs, not outages.
/// </summary>
public sealed class BinanceExchangeMetadata(BinanceRestClient client) : IExchangeMetadata
{
    public async Task<bool> IsTradingSymbolAsync(SymbolName name, CancellationToken ct)
    {
        try
        {
            return await client.IsTradingSymbolAsync(name, ct).ConfigureAwait(false);
        }
        catch (BinanceApiException ex) when (ex.IsTransient)
        {
            throw new ExchangeUnavailableException("Binance is unavailable.", ex);
        }
        catch (BinanceBannedException ex)
        {
            throw new ExchangeUnavailableException("Binance has banned this IP for now.", ex);
        }
    }
}
