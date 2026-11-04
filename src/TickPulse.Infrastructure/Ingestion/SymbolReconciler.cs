using System.Net.WebSockets;
using Microsoft.Extensions.Logging;
using Npgsql;
using TickPulse.Application.Abstractions;
using TickPulse.Infrastructure.Binance;

namespace TickPulse.Infrastructure.Ingestion;

/// <summary>The result of one reconcile pass.</summary>
/// <param name="RetrySoon">
/// Some state is unknown (a command timed out, or the symbols could not be read); run the next pass soon instead of
/// waiting for the regular interval.
/// </param>
/// <param name="Unsubscribed">Symbols confirmed unsubscribed in this pass.</param>
internal sealed record ReconcileOutcome(bool RetrySoon, IReadOnlyList<string> Unsubscribed)
{
    public static ReconcileOutcome Unknown { get; } = new(true, []);
}

/// <summary>
/// Brings a connection's subscriptions in line with the active symbols in the database: one SUBSCRIBE for the
/// missing symbols and one UNSUBSCRIBE for the extra ones. It also refreshes the <see cref="SymbolIdCache"/> before
/// subscribing, so every streamed trade can be mapped to its id.
/// </summary>
/// <remarks>
/// <para>Success is never assumed. Only an ack changes <see cref="StreamLink.Subscribed"/>. A timed-out command
/// leaves its symbols <see cref="StreamLink.Unknown"/> (Binance may or may not have applied it, for example while a
/// full trade channel stalls ack handling), and the next pass sends the desired command for them again; SUBSCRIBE and
/// UNSUBSCRIBE are idempotent. A rejected command changed nothing and is sent again on the next regular pass.</para>
/// <para>Not thread-safe per link: only the supervisor's control loop calls it.</para>
/// </remarks>
internal sealed partial class SymbolReconciler(ISymbolRepository symbols, SymbolIdCache cache, ILogger<SymbolReconciler> logger)
{
    private enum CommandResult
    {
        Applied,
        Rejected,
        Unknown,
    }

    public async Task<ReconcileOutcome> ReconcileAsync(StreamLink link, CancellationToken ct)
    {
        IReadOnlyList<MonitoredSymbol> active;
        try
        {
            active = await symbols.ListActiveAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            LogListFailed(ex);
            return ReconcileOutcome.Unknown;
        }

        cache.Update(active);
        var desired = active.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] toAdd = [.. desired
            .Where(s => !link.Subscribed.Contains(s) || link.Unknown.Contains(s))
            .Order(StringComparer.Ordinal)];
        string[] toRemove = [.. link.Subscribed
            .Concat(link.Unknown)
            .Where(s => !desired.Contains(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)];

        var retrySoon = false;
        if (toAdd.Length > 0)
        {
            var result = await SendAsync(link, subscribe: true, toAdd, ct).ConfigureAwait(false);
            retrySoon |= result == CommandResult.Unknown;
            if (result == CommandResult.Applied)
            {
                LogSubscribed(toAdd.Length, new SymbolList(toAdd));
            }
        }

        IReadOnlyList<string> removed = [];
        if (toRemove.Length > 0)
        {
            var result = await SendAsync(link, subscribe: false, toRemove, ct).ConfigureAwait(false);
            retrySoon |= result == CommandResult.Unknown;
            if (result == CommandResult.Applied)
            {
                removed = toRemove;
                LogUnsubscribed(toRemove.Length, new SymbolList(toRemove));
            }
        }

        return new ReconcileOutcome(retrySoon, removed);
    }

    private static void MarkUnknown(StreamLink link, string[] names)
    {
        link.Subscribed.ExceptWith(names);
        link.Unknown.UnionWith(names);
    }

    private async Task<CommandResult> SendAsync(StreamLink link, bool subscribe, string[] names, CancellationToken ct)
    {
        var method = subscribe ? "SUBSCRIBE" : "UNSUBSCRIBE";
        try
        {
            if (subscribe)
            {
                await link.Connection.SubscribeAsync(names, ct).ConfigureAwait(false);
                link.Subscribed.UnionWith(names);
            }
            else
            {
                await link.Connection.UnsubscribeAsync(names, ct).ConfigureAwait(false);
                link.Subscribed.ExceptWith(names);
            }

            link.Unknown.ExceptWith(names);
            return CommandResult.Applied;
        }
        catch (BinanceCommandException ex)
        {
            // An error reply: Binance applied nothing, so the state is as it was.
            LogRejected(ex, method, ex.Code);
            return CommandResult.Rejected;
        }
        catch (TimeoutException)
        {
            // Sent, but nobody knows whether it was applied.
            MarkUnknown(link, names);
            LogTimedOut(method, names.Length);
            return CommandResult.Unknown;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            MarkUnknown(link, names);
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or WebSocketException)
        {
            // The connection closed underneath; the supervisor notices and reconnects.
            MarkUnknown(link, names);
            LogConnectionGone(method);
            return CommandResult.Unknown;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Subscribed {Count} symbol(s): {Symbols:l}")]
    private partial void LogSubscribed(int count, SymbolList symbols);

    [LoggerMessage(Level = LogLevel.Information, Message = "Unsubscribed {Count} symbol(s): {Symbols:l}")]
    private partial void LogUnsubscribed(int count, SymbolList symbols);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the active symbols; reconcile retries soon")]
    private partial void LogListFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Binance rejected {Method} (code {Code}); reconcile retries on the next pass")]
    private partial void LogRejected(Exception exception, string method, int code);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Method} for {Count} symbol(s) timed out; subscription state unknown, reconcile retries soon")]
    private partial void LogTimedOut(string method, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Method} not sent: the connection has closed")]
    private partial void LogConnectionGone(string method);
}

/// <summary>
/// Symbols for a log message, rendered as <c>ADAUSDT, BTCUSDT</c> (with <c>{Symbols:l}</c>, without quotes). A list
/// parameter would reach Serilog as a sequence and appear in the message as a quoted JSON array; this value is not
/// enumerable, so Serilog stores its text, and it is only joined when the event is actually written.
/// </summary>
internal readonly record struct SymbolList(IReadOnlyList<string> Names)
{
    public override string ToString() => string.Join(", ", Names);
}
