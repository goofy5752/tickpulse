namespace TickPulse.Infrastructure.Binance;

/// <summary>
/// Innermost handler of the Binance REST pipeline, so it runs once per attempt, inside the standard resilience
/// handler's attempt timeout.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>It buffers the response body within the attempt. A body that stalls is cut off by the attempt timeout
/// (and retried) instead of hanging after the pipeline has already returned.</item>
/// <item>It feeds every attempt's <c>X-MBX-USED-WEIGHT-1M</c> to the <see cref="BinanceWeightGuard"/>, including
/// attempts that the pipeline retries (5xx). Those inner retries do not wait on the guard: waiting here would run
/// into the attempt timeout. They are few (at most 3, seconds apart) and their weight is still recorded, so the
/// next call through <see cref="BinanceRestClient"/> waits.</item>
/// </list>
/// </remarks>
internal sealed class BinanceAttemptHandler(BinanceWeightGuard guard) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        try
        {
            guard.ObserveUsedWeight(response);
            await response.Content.LoadIntoBufferAsync(cancellationToken).ConfigureAwait(false);
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }
}
