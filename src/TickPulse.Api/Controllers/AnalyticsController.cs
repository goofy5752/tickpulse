using Microsoft.AspNetCore.Mvc;
using TickPulse.Application.Analytics;
using TickPulse.Contracts;

namespace TickPulse.Api.Controllers;

/// <summary>The public analytics of one symbol, as JSON or XML. Symbols are case-insensitive.</summary>
[ApiController]
public sealed class AnalyticsController(AnalyticsService analytics) : ControllerBase
{
    /// <summary>The arithmetic mean of the trades collected in the last 24 hours.</summary>
    [HttpGet("api/{symbol}/24hAvgPrice")]
    [ProducesResponseType<AveragePriceResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemResponse>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAverageAsync(string symbol, CancellationToken ct) =>
        this.ToActionResult(await analytics.GetAverageAsync(symbol, ct), r => r.ToContract());

    /// <summary>
    /// The simple moving average of the closes of <paramref name="n"/> complete <paramref name="p"/> buckets: the last
    /// ones before now, or the first ones starting at or after <paramref name="s"/>.
    /// </summary>
    /// <param name="symbol">The symbol, e.g. <c>BTCUSDT</c>.</param>
    /// <param name="n">Number of data points, 1 to 1000.</param>
    /// <param name="p">Bucket period: <c>1w</c>, <c>1d</c>, <c>30m</c>, <c>5m</c> or <c>1m</c>.</param>
    /// <param name="s">Optional ISO-8601 start, not in the future; without an offset it is UTC.</param>
    /// <param name="ct">Request cancellation.</param>
    [HttpGet("api/{symbol}/SimpleMovingAverage")]
    [ProducesResponseType<SmaResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemResponse>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSmaAsync(
        string symbol,
        [FromQuery] int n,
        [FromQuery] string p,
        [FromQuery] string? s,
        CancellationToken ct)
    {
        if (!SmaStartParameter.TryParse(s, out var start))
        {
            return this.InvalidParameter(nameof(s), "s must be an ISO-8601 timestamp such as 2026-10-01T00:00:00Z.");
        }

        return this.ToActionResult(await analytics.GetSmaAsync(symbol, n, p, start, ct), r => r.ToContract());
    }

    /// <summary>OHLCV candles of the last <paramref name="limit"/> <paramref name="p"/> periods, oldest first.</summary>
    /// <param name="symbol">The symbol, e.g. <c>BTCUSDT</c>.</param>
    /// <param name="p">Period: <c>1w</c>, <c>1d</c>, <c>30m</c>, <c>5m</c> or <c>1m</c>.</param>
    /// <param name="limit">Number of periods, 1 to 1000; 300 by default.</param>
    /// <param name="ct">Request cancellation.</param>
    [HttpGet("api/{symbol}/candles")]
    [ProducesResponseType<CandleListResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemResponse>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetCandlesAsync(
        string symbol,
        [FromQuery] string p,
        [FromQuery] int limit = AnalyticsService.DefaultCandleLimit,
        CancellationToken ct = default) =>
        this.ToActionResult(await analytics.GetCandlesAsync(symbol, p, limit, ct), r => r.ToContract());
}
