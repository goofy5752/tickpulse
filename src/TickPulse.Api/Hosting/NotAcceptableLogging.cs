using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Options;

namespace TickPulse.Api.Hosting;

/// <summary>
/// Logs MVC's "No output formatter was found" message, which <see cref="ObjectResultExecutor"/> writes at Warning for
/// every 406, at Debug instead.
/// </summary>
/// <remarks>
/// A 406 is a client asking for a type TickPulse does not serve (<c>Accept: text/csv</c>), not a server problem. The
/// request log already records it once, at Information, as it does every 4xx; the framework's Warning on top let one
/// client write a Warning per request, past the request log's sampling. Nothing else changes: MVC still sets the 406,
/// and the status code pages turn it into the TickPulse <c>not-acceptable</c> problem as before. The executor is MVC's
/// own; only the logger it is given differs.
/// </remarks>
internal static class NotAcceptableLogging
{
    /// <summary>Replaces MVC's <see cref="ObjectResult"/> executor with one whose Warnings are written at Debug. Call after <c>AddControllers</c>.</summary>
    public static IServiceCollection AddQuietNotAcceptable(this IServiceCollection services) =>
        services.AddSingleton<IActionResultExecutor<ObjectResult>>(sp => new ObjectResultExecutor(
            sp.GetRequiredService<OutputFormatterSelector>(),
            sp.GetRequiredService<IHttpResponseStreamWriterFactory>(),
            new DebugWarningsLoggerFactory(sp.GetRequiredService<ILoggerFactory>()),
            sp.GetRequiredService<IOptions<MvcOptions>>()));

    /// <summary>Hands out loggers that write Warning entries at Debug; the category stays the executor's.</summary>
    private sealed class DebugWarningsLoggerFactory(ILoggerFactory inner) : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName) => new DebugWarningsLogger(inner.CreateLogger(categoryName));

        public void AddProvider(ILoggerProvider provider) => inner.AddProvider(provider);

        public void Dispose()
        {
            // The inner factory belongs to the host.
        }
    }

    private sealed class DebugWarningsLogger(ILogger inner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(Relevel(logLevel));

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var level = Relevel(logLevel);
            if (inner.IsEnabled(level))
            {
                inner.Log(level, eventId, state, exception, formatter);
            }
        }

        private static LogLevel Relevel(LogLevel level) => level == LogLevel.Warning ? LogLevel.Debug : level;
    }
}
