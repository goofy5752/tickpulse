using TickPulse.Application.Abstractions;
using TickPulse.Domain;

namespace TickPulse.Infrastructure.Binance;

/// <summary>
/// <see cref="IExchangeMetadata"/> over <see cref="BinanceRestClient"/>: it reports the failures that retrying later
/// can fix (a transient <see cref="BinanceApiException"/>, a <see cref="BinanceBannedException"/>, no answer within
/// <see cref="Timeout"/>) as <see cref="ExchangeUnavailableException"/>, so the use cases never see Binance's own
/// exception types. Permanent failures (a rejected request, a malformed payload) still propagate as they are: they are
/// bugs, not outages. A cancellation by the caller propagates as <see cref="OperationCanceledException"/>.
/// </summary>
/// <remarks>
/// The timeout keeps an admin request from waiting on the shared <see cref="BinanceWeightGuard"/> while it is paused
/// after a <c>429</c> (up to a minute or more), and on the client's own retries.
/// </remarks>
public sealed class BinanceExchangeMetadata(BinanceRestClient client, TimeProvider? clock = null) : IExchangeMetadata
{
    /// <summary>How long one question may take before the exchange counts as unavailable.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<bool> IsTradingSymbolAsync(SymbolName name, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(Timeout, _clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            return await client.IsTradingSymbolAsync(name, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new ExchangeUnavailableException($"Binance did not answer within {Timeout.TotalSeconds:0} s.", ex);
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
