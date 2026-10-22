using System.Text.RegularExpressions;
using Serilog;
using ShipRight.Modules.Projects;
using ShipRight.Shared.SshRunner;

namespace ShipRight.Modules.RemoteHost;

/// <summary>
/// Wraps <see cref="RemoteServerDeployer"/>: each deploy stage logs START/OK/FAIL checkpoints on the
/// server (the stage is recognised from its command), and a failed deploy collects server diagnostics.
/// </summary>
public sealed class ResilientServerDeployer
{
    private readonly ISshRunner _ssh;

    public ResilientServerDeployer(ISshRunner ssh) => _ssh = ssh;

    private sealed class CheckpointingSsh(ISshRunner inner) : ISshRunner
    {
        public Task<int> RunAsync(string host, string username, string keyPath, string command,
            Func<string, Task>? onOutput = null, Func<string, Task>? onStderr = null, CancellationToken ct = default)
        {
            var stage = command.Contains("base64 -d") ? "write-env"
                : command.Contains("docker login") ? "ecr-login"
                : command.Contains("docker compose up") ? "compose-up"
                : null;
            var wrapped = stage is null ? command : RemoteCheckpoint.Wrap("deploy", stage, command);
            return inner.RunAsync(host, username, keyPath, wrapped, onOutput, onStderr, ct);
        }
    }

    private static readonly Regex ErrorLine = new(
        @"\b(error|failed|fatal|denied|no such file|not found|cannot|unable)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The last line that looks like an error; falls back to the last line.</summary>
    internal static string? PickCause(IReadOnlyList<string> lines) =>
        lines.LastOrDefault(l => ErrorLine.IsMatch(l))?.Trim() ?? lines.LastOrDefault()?.Trim();

    /// <summary>
    /// Plain-language fix for the common "service has build: but no image:" mistake. The server only holds
    /// the compose repo (docker-compose.yml), so a build context has no Dockerfile to build from.
    /// </summary>
    internal static string? BuildHint(IReadOnlyList<string> lines) =>
        lines.Any(l => l.Contains("failed to read dockerfile", StringComparison.OrdinalIgnoreCase)
                    || l.Contains("No image to be pulled", StringComparison.OrdinalIgnoreCase))
            ? "A service in docker-compose.yml has a build: section but no image:, so Docker tries to build it on the server, " +
              "where there is no Dockerfile. In the compose repo, replace build: with image: <registry>/<name>:<tag> " +
              "(for example your ECR image) so the server pulls it instead."
            : null;

    public async Task<RemoteDeployResult> DeployAsync(
        ServerConfig server, Func<string, Task> onLine, CancellationToken ct = default)
    {
        var log = Log.ForContext<ResilientServerDeployer>().ForContext("ServerId", server.Id);
        var deployer = new RemoteServerDeployer(new CheckpointingSsh(_ssh), new RemoteHealthChecker(_ssh));
        // Keep the command's own output (progress lines start with '[') to name the real cause on failure.
        var output = new List<string>();
        Task Capture(string l)
        {
            if (!string.IsNullOrWhiteSpace(l) && !l.StartsWith('[')) lock (output) output.Add(l);
            return onLine(l);
        }
        var result = await deployer.DeployAsync(server, Capture, ct);
        if (!result.Success)
        {
            string[] seen; lock (output) seen = output.ToArray();
            var cause = PickCause(seen);
            var hint = BuildHint(seen);
            if (cause is not null) result = result with { Message = $"{result.Message} {cause}" };
            if (hint is not null)
            {
                result = result with { Message = $"{result.Message} {hint}" };
                await onLine($"[hint] {hint}");
            }
            await RemoteCheckpoint.RunDiagnosticsAsync(_ssh, server, onLine, log, ct);
        }
        return result;
    }
}
