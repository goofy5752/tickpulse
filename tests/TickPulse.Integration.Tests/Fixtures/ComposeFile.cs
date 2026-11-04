using System.Globalization;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace TickPulse.Integration.Tests.Fixtures;

/// <summary>
/// A compose file of this repository, parsed as YAML: its services, their environment and their stop grace period.
/// Anchors and aliases are resolved by the parser; Compose's own merge keys (<c>&lt;&lt;</c>) and tags
/// (<c>!reset</c>, <c>!override</c>) are left as they are, so only keys a service sets itself are read.
/// </summary>
public sealed partial class ComposeFile
{
    public const string Local = "docker-compose.yml";
    public const string Production = "deploy/docker-compose.prod.yml";

    private static readonly string Root = FindRoot();

    private readonly YamlMappingNode _root;

    private ComposeFile(string path, YamlMappingNode root)
    {
        Path = path;
        _root = root;
    }

    /// <summary>The file, relative to the repository root.</summary>
    public string Path { get; }

    public static ComposeFile Load(string relativePath)
    {
        using var reader = new StreamReader(System.IO.Path.Combine(Root, relativePath));
        var stream = new YamlStream();
        stream.Load(reader);
        return new ComposeFile(relativePath, (YamlMappingNode)stream.Documents.Single().RootNode);
    }

    /// <summary>The mapping of one service; fails with the file and service name when it is missing.</summary>
    public YamlMappingNode Service(string name)
    {
        var services = (YamlMappingNode)_root.Children[new YamlScalarNode("services")];
        return services.Children.TryGetValue(new YamlScalarNode(name), out var service)
            ? (YamlMappingNode)service
            : throw new InvalidOperationException($"{Path}: no service '{name}'.");
    }

    /// <summary>A variable of the service's <c>environment</c>, given as a mapping or as a list of <c>KEY=value</c>.</summary>
    public string? Environment(string service, string key)
    {
        if (!Service(service).Children.TryGetValue(new YamlScalarNode("environment"), out var environment))
        {
            return null;
        }

        return environment switch
        {
            YamlMappingNode map => map.Children.TryGetValue(new YamlScalarNode(key), out var value) ? ((YamlScalarNode)value).Value : null,
            YamlSequenceNode list => list.Children.Cast<YamlScalarNode>()
                .Select(n => n.Value ?? string.Empty)
                .Where(entry => entry.StartsWith(key + "=", StringComparison.Ordinal))
                .Select(entry => entry[(key.Length + 1)..])
                .FirstOrDefault(),
            _ => null,
        };
    }

    /// <summary>The service's <c>stop_grace_period</c>, or <see langword="null"/> when it sets none.</summary>
    public TimeSpan? StopGracePeriod(string service) =>
        Service(service).Children.TryGetValue(new YamlScalarNode("stop_grace_period"), out var value)
            ? ParseDuration(((YamlScalarNode)value).Value!)
            : null;

    /// <summary>A Compose duration such as <c>30s</c>, <c>1m30s</c> or <c>500ms</c>.</summary>
    public static TimeSpan ParseDuration(string value)
    {
        var parts = DurationPart().Matches(value);
        if (parts.Count == 0 || string.Concat(parts.Select(p => p.Value)) != value)
        {
            throw new FormatException($"'{value}' is not a Compose duration.");
        }

        return parts.Aggregate(TimeSpan.Zero, (total, part) =>
        {
            var amount = double.Parse(part.Groups[1].Value, CultureInfo.InvariantCulture);
            return total + part.Groups[2].Value switch
            {
                "h" => TimeSpan.FromHours(amount),
                "m" => TimeSpan.FromMinutes(amount),
                "s" => TimeSpan.FromSeconds(amount),
                "ms" => TimeSpan.FromMilliseconds(amount),
                "us" => TimeSpan.FromMicroseconds(amount),
                _ => throw new FormatException($"'{value}' has an unknown unit."),
            };
        });
    }

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(System.IO.Path.Combine(dir.FullName, "TickPulse.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("TickPulse.slnx not found above the test output directory.");
    }

    [GeneratedRegex(@"(\d+(?:\.\d+)?)(ms|us|h|m|s)")]
    private static partial Regex DurationPart();
}
