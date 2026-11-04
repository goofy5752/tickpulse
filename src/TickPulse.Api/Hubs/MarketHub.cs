using Microsoft.AspNetCore.SignalR;
using TickPulse.Domain;

namespace TickPulse.Api.Hubs;

/// <summary>
/// Public SignalR hub at <see cref="Path"/> for live prices and statistics. A client joins the group of a symbol with
/// <see cref="Subscribe"/> and receives <see cref="PriceMethod"/> messages for it, at most one per symbol every 250 ms.
/// Every client receives <see cref="StatsMethod"/> every 2 s. The messages are sent by <see cref="MarketBroadcaster"/>.
/// </summary>
public sealed class MarketHub(MarketHubConnections connections) : Hub
{
    public const string Path = "/hubs/market";

    /// <summary>Client method that receives a <see cref="Contracts.LivePriceMessage"/>.</summary>
    public const string PriceMethod = "price";

    /// <summary>Client method that receives a <see cref="Contracts.SystemStatsResponse"/>.</summary>
    public const string StatsMethod = "stats";

    /// <summary>Most symbol groups one connection may join.</summary>
    public const int MaxSubscriptionsPerConnection = 50;

    private const string GroupPrefix = "sym:";

    // Key of the connection's joined symbols in HubCallerContext.Items, which lives as long as the connection.
    private static readonly object SubscriptionsKey = new();

    /// <summary>The group of a normalised (upper-case) symbol, e.g. <c>sym:BTCUSDT</c>.</summary>
    public static string GroupName(string symbol) => GroupPrefix + symbol;

    public override async Task OnConnectedAsync()
    {
        connections.Opened();
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        connections.Closed();
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Joins the group of <paramref name="symbol"/> (case-insensitive). Returns whether the connection is now
    /// subscribed: <see langword="false"/> for an invalid symbol, or when the connection already holds
    /// <see cref="MaxSubscriptionsPerConnection"/> other subscriptions.
    /// </summary>
    /// <remarks>The symbol does not have to be monitored; a group that is never fed simply stays quiet.</remarks>
    public async Task<bool> Subscribe(string? symbol)
    {
        if (!SymbolName.TryCreate(symbol, out var name))
        {
            return false;
        }

        var joined = Subscriptions();
        lock (joined)
        {
            if (joined.Contains(name.Value))
            {
                return true;
            }

            if (joined.Count >= MaxSubscriptionsPerConnection)
            {
                return false;
            }

            joined.Add(name.Value);
        }

        try
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(name.Value), Context.ConnectionAborted);
        }
        catch
        {
            lock (joined)
            {
                joined.Remove(name.Value);
            }

            throw;
        }

        return true;
    }

    /// <summary>Leaves the group of <paramref name="symbol"/>; an invalid or unsubscribed symbol is ignored.</summary>
    public async Task Unsubscribe(string? symbol)
    {
        if (!SymbolName.TryCreate(symbol, out var name))
        {
            return;
        }

        var joined = Subscriptions();
        lock (joined)
        {
            if (!joined.Remove(name.Value))
            {
                return;
            }
        }

        await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(name.Value), Context.ConnectionAborted);
    }

    private HashSet<string> Subscriptions()
    {
        var items = Context.Items;
        lock (items)
        {
            if (items.TryGetValue(SubscriptionsKey, out var existing) && existing is HashSet<string> set)
            {
                return set;
            }

            var created = new HashSet<string>(StringComparer.Ordinal);
            items[SubscriptionsKey] = created;
            return created;
        }
    }
}
