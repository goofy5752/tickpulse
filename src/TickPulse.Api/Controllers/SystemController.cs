using Microsoft.AspNetCore.Mvc;
using TickPulse.Api.Hosting;
using TickPulse.Contracts;

namespace TickPulse.Api.Controllers;

/// <summary>Public operational statistics of this instance.</summary>
[ApiController]
public sealed class SystemController(SystemStatsProvider stats) : ControllerBase
{
    /// <summary>
    /// Ingestion status (WebSocket state, leadership, ticks/s, lag, rotation), approximate stored ticks, active symbols,
    /// uptime and version. The market hub pushes the same document as <c>stats</c> every 2 s.
    /// </summary>
    [HttpGet("api/system/stats")]
    [ProducesResponseType<SystemStatsResponse>(StatusCodes.Status200OK)]
    public async Task<SystemStatsResponse> StatsAsync(CancellationToken ct) => await stats.GetAsync(ct);
}
