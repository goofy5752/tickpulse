using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
using System.Text.RegularExpressions;

namespace TickPulse.Cli.Commands;

/// <summary>The options every command shares: where the API is and how to print its answers.</summary>
internal sealed class GlobalOptions(CliEnvironment environment)
{
    public Option<string> ApiUrl { get; } = CliOptions.ApiUrl(environment);

    public Option<string> Format { get; } = CliOptions.Format();

    /// <summary>
    /// The admin key. Global (recursive) so <c>--api-key</c> anywhere on the command line is consumed as an option
    /// value, never left over as an unrecognised token that a usage error would print.
    /// </summary>
    public Option<string> ApiKey { get; } = CliOptions.ApiKey();
}

/// <summary>Arguments and options with their parsing and validation, so bad input is a usage error (64) before any request.</summary>
internal static partial class CliOptions
{
    public const string RootName = "tickpulse";

    /// <summary>The periods the API buckets by, shortest first.</summary>
    public static readonly string[] Periods = ["1m", "5m", "30m", "1d", "1w"];

    // The server limits n to AnalyticsService.MinDataPoints..MaxDataPoints.
    public const int MinPoints = 1;
    public const int MaxPoints = 1000;

    public static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan MaxInterval = TimeSpan.FromHours(1);

    private static readonly string[] LocalTimestampFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF",
        "yyyy-MM-dd'T'HH:mm:ss",
        "yyyy-MM-dd'T'HH:mm",
    ];

    // Each local format bare (UTC), with Z, or with an offset; plus a bare date.
    private static readonly string[] TimestampFormats =
    [
        .. LocalTimestampFormats,
        .. LocalTimestampFormats.Select(f => f + "'Z'"),
        .. LocalTimestampFormats.Select(f => f + "zzz"),
        "yyyy-MM-dd",
    ];

    public static Option<string> ApiUrl(CliEnvironment environment)
    {
        var option = new Option<string>("--api-url")
        {
            Description = $"Base URL of the TickPulse API (env {CliEnvironment.ApiUrlVariable}).",
            HelpName = "url",
            Recursive = true,
            DefaultValueFactory = _ =>
                environment.GetEnvironmentVariable(CliEnvironment.ApiUrlVariable) is { Length: > 0 } fromEnvironment
                    ? fromEnvironment
                    : CliEnvironment.DefaultApiUrl,
        };
        option.Validators.Add(result =>
        {
            if (ValidateApiUrl(result.GetValueOrDefault<string>(), out _) is { } error)
            {
                result.AddError(error);
            }
        });
        return option;
    }

    public static Option<string> Format()
    {
        var option = new Option<string>("--format", "-f")
        {
            Description = "table renders panels and charts; json and xml print the API's raw body, highlighted.",
            Recursive = true,
            DefaultValueFactory = _ => OutputFormats.Table,
        };
        return option.AcceptOnlyFromAmong(OutputFormats.Table, OutputFormats.Json, OutputFormats.Xml);
    }

    /// <summary>A symbol such as <c>BTCUSDT</c>, any case; parsed to upper case. Only letters and digits reach the URL.</summary>
    /// <remarks>Mirrors the server rule in <c>TickPulse.Domain.SymbolName</c> (<c>^[A-Za-z0-9]{5,20}$</c>); keep them in sync.</remarks>
    public static Argument<string> Symbol(string description = "Trading pair, e.g. BTCUSDT (case-insensitive).") => new("symbol")
    {
        Description = description,
        CustomParser = result =>
        {
            var token = result.Tokens.Count > 0 ? result.Tokens[0].Value : string.Empty;
            if (!SymbolPattern().IsMatch(token))
            {
                result.AddError($"'{token}' is not a symbol: use 5 to 20 letters or digits, e.g. BTCUSDT.");
                return null;
            }

            return token.ToUpperInvariant();
        },
    };

    /// <summary><c>-n|--points</c>: how many buckets to average, 1 to 1000.</summary>
    /// <remarks>Mirrors <c>AnalyticsService.MinDataPoints</c>/<c>MaxDataPoints</c> on the server; keep them in sync.</remarks>
    public static Option<int> Points(bool required) => new("--points", "-n")
    {
        Description = "Number of complete buckets to average, 1 to 1000.",
        HelpName = "count",
        Required = required,

        // A parser rather than a validator: a validator reading the value of a non-number token would throw.
        CustomParser = result =>
        {
            var token = result.Tokens.Count > 0 ? result.Tokens[0].Value : string.Empty;
            if (!int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var points) || points is < MinPoints or > MaxPoints)
            {
                result.AddError($"--points must be a whole number from {MinPoints} to {MaxPoints}.");
                return 0;
            }

            return points;
        },
    };

    /// <summary><c>-p|--period</c>: the bucket width.</summary>
    public static Option<string> Period(bool required)
    {
        var option = new Option<string>("--period", "-p")
        {
            Description = "Bucket width: 1m, 5m, 30m, 1d or 1w (weeks start Monday 00:00 UTC).",
            HelpName = string.Join('|', Periods),
            Required = required,
        };
        return option.AcceptOnlyFromAmong(Periods);
    }

    /// <summary><c>-s|--start</c>: an ISO-8601 timestamp, UTC unless it carries an offset.</summary>
    public static Option<DateTime?> Start() => new("--start", "-s")
    {
        Description = "Average the first buckets starting at or after this ISO-8601 time (UTC unless an offset is given), "
            + "e.g. 2026-10-01 or 2026-10-01T09:30:00+02:00. Without it, the latest complete buckets.",
        HelpName = "time",
        CustomParser = ParseStart,
    };

    /// <summary><c>-k|--api-key</c>: the admin key; never shown as a default, so it cannot leak into help output.</summary>
    public static Option<string> ApiKey() => new("--api-key", "-k")
    {
        Description = $"Admin API key for the symbols commands, sent as X-Api-Key (env {CliEnvironment.ApiKeyVariable}).",
        HelpName = "key",
        Recursive = true,
    };

    /// <summary><c>-i|--interval</c>: how often <c>watch</c> polls; <c>500ms</c>, <c>2s</c>, <c>1m</c> or <c>00:00:05</c>.</summary>
    public static Option<TimeSpan> Interval() => new("--interval", "-i")
    {
        Description = "How often to poll: e.g. 500ms, 2s, 1m or 1h (0.5s to 1h).",
        HelpName = "duration",
        DefaultValueFactory = _ => TimeSpan.FromSeconds(2),
        CustomParser = ParseInterval,
    };

    /// <summary>An absolute http(s) URL ending with <c>/</c>, so relative API paths resolve under it.</summary>
    public static Uri? TryParseApiUrl(string? value) => ValidateApiUrl(value, out var uri) is null ? uri : null;

    /// <summary>
    /// Checks a base URL: absolute http(s), no credentials, no query or fragment. Returns the error, or
    /// <see langword="null"/> with <paramref name="uri"/> set (ending with <c>/</c>). An error never contains the
    /// URL's user info, even when the URL does not parse.
    /// </summary>
    public static string? ValidateApiUrl(string? value, out Uri? uri)
    {
        uri = null;
        var shown = RedactUserInfo(value ?? string.Empty);
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            return $"--api-url '{shown}' is not an absolute http(s) URL, e.g. {CliEnvironment.DefaultApiUrl}.";
        }

        if (parsed.UserInfo.Length > 0)
        {
            return $"--api-url must not contain credentials ('{shown}'); admin commands take --api-key.";
        }

        if (parsed.Query.Length > 0 || parsed.Fragment.Length > 0)
        {
            var part = parsed.Query.Length > 0 ? "a query" : "a fragment";
            return $"--api-url must be a base URL without {part}, e.g. {CliEnvironment.DefaultApiUrl}.";
        }

        uri = parsed.AbsoluteUri.EndsWith('/') ? parsed : new Uri(parsed.AbsoluteUri + "/");
        return null;
    }

    /// <summary>
    /// <c>https://user:pw@host</c> → <c>https://***@host</c>. Everything between <c>://</c> (or the start, without a
    /// scheme) and the <em>last</em> <c>@</c> is replaced, so a password containing <c>/</c>, <c>?</c>, <c>#</c> or
    /// <c>@</c> is still hidden, and text that is not a valid URL is handled too. An <c>@</c> later in the path makes
    /// this hide more than the user info, never less.
    /// </summary>
    private static string RedactUserInfo(string value)
    {
        var text = value.Trim();
        var at = text.LastIndexOf('@');
        if (at < 0)
        {
            return text;
        }

        var scheme = text.IndexOf("://", StringComparison.Ordinal);
        var start = scheme >= 0 && scheme < at ? scheme + 3 : 0;
        return string.Concat(text.AsSpan(0, start), "***", text.AsSpan(at));
    }

    private static DateTime? ParseStart(ArgumentResult result)
    {
        var token = result.Tokens.Count > 0 ? result.Tokens[0].Value.Trim() : string.Empty;
        if (DateTimeOffset.TryParseExact(
                token.ToUpperInvariant(),
                TimestampFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            return parsed.UtcDateTime;
        }

        result.AddError($"'{token}' is not an ISO-8601 time; use e.g. 2026-10-01, 2026-10-01T00:00:00Z or 2026-10-01T09:30:00+02:00.");
        return null;
    }

    private static TimeSpan ParseInterval(ArgumentResult result)
    {
        var token = result.Tokens.Count > 0 ? result.Tokens[0].Value.Trim() : string.Empty;
        TimeSpan? interval = null;
        var match = DurationPattern().Match(token);
        if (match.Success)
        {
            var amount = decimal.Parse(match.Groups["amount"].Value, CultureInfo.InvariantCulture);
            var milliseconds = match.Groups["unit"].Value.ToUpperInvariant() switch
            {
                "MS" => amount,
                "S" => amount * 1_000m,
                "M" => amount * 60_000m,
                _ => amount * 3_600_000m,
            };
            if (milliseconds <= (decimal)MaxInterval.TotalMilliseconds)
            {
                interval = TimeSpan.FromMilliseconds((long)milliseconds);
            }
        }
        else if (TimeSpan.TryParse(token, CultureInfo.InvariantCulture, out var span))
        {
            interval = span;
        }

        if (interval is not { } value || value < MinInterval || value > MaxInterval)
        {
            result.AddError($"'{token}' is not a polling interval between 500ms and 1h; use e.g. 500ms, 2s or 1m.");
            return TimeSpan.FromSeconds(2);
        }

        return value;
    }

    [GeneratedRegex("^[A-Za-z0-9]{5,20}$", RegexOptions.CultureInvariant)]
    private static partial Regex SymbolPattern();

    [GeneratedRegex(@"^(?<amount>\d{1,7}(?:\.\d{1,3})?)\s*(?<unit>ms|s|m|h)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex DurationPattern();
}
