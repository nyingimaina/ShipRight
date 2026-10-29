using System.Text;
using System.Text.RegularExpressions;
using ShipRight.Modules.Projects;
using ShipRight.Shared.SshRunner;

namespace ShipRight.Modules.Setup;

/// <summary>
/// Wraps the SSH runner so an EnvCompose deploy to a server with no compose file gets one first. Every deploy path
/// (deploy, pipeline step, rollback) ends in the same <c>cd &lt;dir&gt; &amp;&amp; export …</c> command, so recognising it
/// here covers all of them without touching the orchestrator. Anything else passes straight through, and a compose
/// file that already exists - under any of the names docker compose reads - is never touched.
/// </summary>
public sealed class ComposeProvisioningSshRunner : ISshRunner
{
    private static readonly Regex EnvComposeDeploy = new(@"^cd (?<dir>\S+) && export ", RegexOptions.Compiled);

    private readonly ISshRunner _inner;
    private readonly IProjectStore _projects;

    public ComposeProvisioningSshRunner(ISshRunner inner, IProjectStore projects)
    {
        _inner = inner;
        _projects = projects;
    }

    public async Task<int> RunAsync(string host, string username, string keyPath, string command,
        Func<string, Task>? onOutput = null, Func<string, Task>? onStderr = null, CancellationToken ct = default)
    {
        var match = EnvComposeDeploy.Match(command);
        if (match.Success)
        {
            var dir = match.Groups["dir"].Value;
            var project = (await _projects.GetAllAsync()).FirstOrDefault(p =>
                p.Server.DeployMode == DeployMode.EnvCompose && p.Services.Count > 0 &&
                string.Equals(p.Server.Host, host, StringComparison.OrdinalIgnoreCase) &&
                p.Server.RemoteWorkingDir.TrimEnd('/') == dir.TrimEnd('/'));
            var yml = project is null ? null : GeneratedCompose.Render(project);

            if (yml is not null)
            {
                var failed = await EnsureComposeFileAsync(host, username, keyPath, dir.TrimEnd('/'), yml, onOutput, onStderr, ct);
                if (failed is not null) return failed.Value;
            }
        }
        return await _inner.RunAsync(host, username, keyPath, command, onOutput, onStderr, ct);
    }

    /// <summary>Null when a compose file is in place afterwards; otherwise the exit code to stop with.</summary>
    private async Task<int?> EnsureComposeFileAsync(string host, string username, string keyPath, string dir, string yml,
        Func<string, Task>? onOutput, Func<string, Task>? onStderr, CancellationToken ct)
    {
        var exists = string.Join(" || ", GeneratedCompose.FileNames.Select(n => $"test -f {Quote($"{dir}/{n}")}"));
        if (await _inner.RunAsync(host, username, keyPath, exists, null, null, ct) == 0) return null;

        var target = $"{dir}/docker-compose.yml";
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(yml));
        // [ -e ] guards against a file appearing between the check and the write.
        var write = $"mkdir -p {Quote(dir)} && ([ -e {Quote(target)} ] || printf %s '{b64}' | base64 -d > {Quote(target)})";

        if (onOutput is not null)
            await onOutput($"No compose file on the server - writing {target} (ShipRight will not overwrite it later).");
        var exit = await _inner.RunAsync(host, username, keyPath, write, onOutput, onStderr, ct);
        if (exit != 0 && onOutput is not null)
            await onOutput($"[ERROR] ShipRight could not create {target} on the server (exit code {exit}). Nothing was deployed.");
        return exit == 0 ? null : exit;
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";
}
