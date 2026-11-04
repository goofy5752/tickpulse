using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TickPulse.Application.Ingestion;
using TickPulse.Infrastructure;
using TickPulse.Infrastructure.Persistence;
using TickPulse.Integration.Tests.Fixtures;
using TickPulse.Integration.Tests.Ingestion;

namespace TickPulse.Integration.Tests.Api;

/// <summary>
/// Regression: Npgsql's GSS encryption negotiation (its default, Prefer) segfaulted the API in the Alpine image under
/// load. A connection string that asks for it must not switch it back on: the host forces it off on every connection.
/// </summary>
public sealed class GssEncryptionStartupTests(TimescaleFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Require proves the forcing by behaviour: the test database has no GSS encryption, so migrations, the leader lock
    // and every query would fail if the mode got through. Prefer is the mode production connection strings default to.
    [Theory]
    [InlineData("Prefer")]
    [InlineData("Require")]
    public async Task Api_WithGssEncryptionInTheConnectionString_StartsAndServes_WithItDisabled(string mode)
    {
        await using var api = new ApiFactory(db)
            .WithWorkers()
            .WithSystemClock()
            .WithSetting("ConnectionStrings:TickPulse", $"{db.ConnectionString};GSS Encryption Mode={mode}");
        await api.InitializeAsync();
        using var client = api.CreateClient();
        var state = api.Services.GetRequiredService<IngestionState>();

        // Leadership needs the leader lock's own (unpooled) connection; Connected follows the migrations.
        await IngestionTestHost.WaitForAsync(() => state.IsLeader && state.Status == WsState.Connected, "the worker to lead and connect");

        using (var ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), Ct))
        {
            ready.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using (var symbols = await client.GetAsync(new Uri("/api/symbols", UriKind.Relative), Ct))
        {
            symbols.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await symbols.Content.ReadAsStringAsync(Ct)).ShouldContain("BTCUSDT");
        }

        using (var average = await client.GetAsync(new Uri("/api/BTCUSDT/24hAvgPrice", UriKind.Relative), Ct))
        {
            // 200 with ticks, 404 no-data without; both mean the query ran.
            average.StatusCode.ShouldBeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound);
        }

        Mode(api.Services.GetRequiredService<NpgsqlDataSource>().ConnectionString).ShouldBe(GssEncryptionMode.Disable);
        Mode(api.Services.GetRequiredKeyedService<NpgsqlDataSource>(DependencyInjection.TickWriterDataSourceKey).ConnectionString)
            .ShouldBe(GssEncryptionMode.Disable);
        await using var scope = api.Services.CreateAsyncScope();
        Mode(scope.ServiceProvider.GetRequiredService<TickPulseDbContext>().Database.GetDbConnection().ConnectionString)
            .ShouldBe(GssEncryptionMode.Disable);
    }

    private static GssEncryptionMode Mode(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString).GssEncryptionMode;
}
