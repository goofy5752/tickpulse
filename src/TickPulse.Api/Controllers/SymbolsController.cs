using Microsoft.AspNetCore.Mvc;
using TickPulse.Application.Analytics;
using TickPulse.Contracts;

namespace TickPulse.Api.Controllers;

/// <summary>The public list of symbols.</summary>
[ApiController]
public sealed class SymbolsController(AnalyticsService analytics) : ControllerBase
{
    /// <summary>Every monitored or formerly monitored symbol, with its latest price and 24 h change.</summary>
    [HttpGet("api/symbols")]
    [ProducesResponseType<SymbolListResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ListAsync(CancellationToken ct) =>
        this.ToActionResult(await analytics.ListSymbolsAsync(ct), r => r.ToContract());
}
