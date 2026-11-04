using System.Text.RegularExpressions;
using TickPulse.Integration.Tests.Fixtures;

namespace TickPulse.Integration.Tests.Api;

/// <summary>
/// The CD job copies the deploy files to the server with one <c>scp</c>, and <c>deploy/deploy.sh</c> refuses to
/// deploy when any script in its <c>SCRIPTS</c> list is missing from that upload. The two lists live in different
/// files, so a script added to one must be added to the other: the first deploy after <c>mask-env.sh</c> joined
/// <c>SCRIPTS</c> was refused because the job never sent it.
/// </summary>
public sealed partial class DeployUploadTests
{
    [Fact]
    public void Upload_CarriesEveryScriptDeployShRequires()
    {
        var script = File.ReadAllText(Path.Combine(ComposeFile.Root, "deploy", "deploy.sh"));
        var scripts = ScriptsList().Match(script);
        scripts.Success.ShouldBeTrue("deploy/deploy.sh: no readonly SCRIPTS=(...) list");
        var required = scripts.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var workflow = File.ReadAllText(Path.Combine(ComposeFile.Root, ".github", "workflows", "ci.yml"));
        var upload = ScpUpload().Match(workflow);
        upload.Success.ShouldBeTrue(".github/workflows/ci.yml: no scp -rp ... upload to .incoming");
        var sent = upload.Groups[1].Value.Split([' ', '\\', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);

        required.ShouldNotBeEmpty();
        foreach (var name in required)
        {
            sent.ShouldContain($"deploy/{name}", $"ci.yml's upload misses deploy/{name}, which deploy.sh requires");
        }
    }

    [GeneratedRegex(@"^readonly SCRIPTS=\(([^)]*)\)$", RegexOptions.Multiline)]
    private static partial Regex ScriptsList();

    [GeneratedRegex(@"scp -rp ((?:[^""\n]|\\\r?\n)*?)""tickpulse-prod:\$REMOTE_DIR/\.incoming/""")]
    private static partial Regex ScpUpload();
}
