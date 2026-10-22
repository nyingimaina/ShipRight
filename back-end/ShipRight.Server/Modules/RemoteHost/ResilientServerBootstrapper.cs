using System.Diagnostics;
using Serilog;
using ShipRight.Modules.Projects;
using ShipRight.Shared.SshRunner;

namespace ShipRight.Modules.RemoteHost;

/// <summary>
/// Same steps as <see cref="RemoteServerBootstrapper"/> but recoverable: the repo step is an idempotent
/// sync (works on a non-empty dir), every step logs checkpoints on the server, every output line goes
/// to the app log, and a failure collects server diagnostics and puts the real error in the message.
/// </summary>
public sealed class ResilientServerBootstrapper
{
    private readonly ISshRunner _ssh;

    public ResilientServerBootstrapper(ISshRunner ssh) => _ssh = ssh;

    public async Task<BootstrapResult> RunAsync(
        ServerConfig server, Func<string, Task> onLine, CancellationToken ct = default)
    {
        var log = Log.ForContext<ResilientServerBootstrapper>()
            .ForContext("ServerId", server.Id).ForContext("Host", server.Host);

        if (string.IsNullOrWhiteSpace(server.ComposeRepoUrl))
            return new(false, "Compose repo URL is required before preparing a server.");
        if (string.IsNullOrWhiteSpace(server.RemoteWorkingDir))
            return new(false, "Remote working directory is required before preparing a server.");

        var steps = new List<(string Name, string Command)>
        {
            ("Install Docker + git", RemoteServerBootstrapper.BuildInstallDockerCommand()),
            ("Ensure Docker Compose plugin", RemoteServerBootstrapper.BuildEnsureComposeCommand()),
            ("Install AWS CLI", RemoteServerBootstrapper.BuildInstallAwsCliCommand()),
            ("Compose repo sync", RemoteCheckpoint.BuildSyncRepoCommand(server)),
        };
        if (!string.IsNullOrWhiteSpace(server.EnvFile))
            steps.Add(("Write .env", RemoteEnvFileWriter.BuildWriteEnvFileCommand(server.RemoteWorkingDir, server.EnvFile)));
        var ecr = RemoteServerBootstrapper.BuildEcrLoginCommand(server);
        if (ecr is not null) steps.Add(("ECR login", ecr));

        log.Information("Bootstrap started: user={User} dir={Dir} steps={Steps}", server.Username, server.RemoteWorkingDir, steps.Count);
        var total = Stopwatch.StartNew();

        for (var i = 0; i < steps.Count; i++)
        {
            var (name, command) = steps[i];
            await onLine($"[bootstrap {i + 1}/{steps.Count}] {name}…");
            var tail = new Queue<string>();
            Task Capture(string l)
            {
                lock (tail) { tail.Enqueue(l); while (tail.Count > 12) tail.Dequeue(); }
                log.Information("[{Step}] {Line}", name, l);
                return onLine(l);
            }

            var sw = Stopwatch.StartNew();
            int exit;
            string error;
            try
            {
                exit = await _ssh.RunAsync(server.Host, server.Username, server.SshKeyPath,
                    RemoteCheckpoint.Wrap("bootstrap", name, command), Capture, Capture, ct);
                string[] last; lock (tail) last = tail.ToArray();
                error = last.LastOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? "no output";
                if (exit != 0)
                    log.Error("Bootstrap step '{Step}' failed exit={ExitCode} after {ElapsedMs}ms. Output tail: {Tail}",
                        name, exit, sw.ElapsedMilliseconds, string.Join(" | ", last));
            }
            catch (Exception ex)
            {
                log.Error(ex, "Bootstrap step '{Step}' threw after {ElapsedMs}ms", name, sw.ElapsedMilliseconds);
                await onLine($"[bootstrap] ✗ {name} error: {ex.Message}");
                return new(false, $"Step '{name}' error: {ex.Message}", name);
            }

            if (exit != 0)
            {
                await onLine($"[bootstrap] ✗ {name} failed (exit {exit}).");
                await RemoteCheckpoint.RunDiagnosticsAsync(_ssh, server, onLine, log, ct);
                return new(false, $"Step '{name}' failed (exit {exit}): {error}", name);
            }

            log.Information("Bootstrap step '{Step}' ok in {ElapsedMs}ms", name, sw.ElapsedMilliseconds);
            await onLine($"[bootstrap] ✓ {name}");
        }

        log.Information("Bootstrap complete in {ElapsedMs}ms", total.ElapsedMilliseconds);
        return new(true, "Server is ready.");
    }
}
