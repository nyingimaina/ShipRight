using System.Diagnostics;
using Serilog;
using ShipRight.Modules.Projects;
using ShipRight.Shared.SshRunner;

namespace ShipRight.Modules.RemoteHost;

public sealed record BootstrapResult(bool Success, string Message, string? FailedStep = null);

/// <summary>
/// Prepares a fresh Linux server (Amazon Linux / Ubuntu) to run a docker-compose stack:
/// installs Docker, the compose plugin, git and the AWS CLI, clones the compose repo,
/// writes .env and logs in to ECR (via the instance role). Every step is idempotent and
/// each runs as its own SSH command so a failure names the exact step.
/// Command builders are pure static methods so they are unit-testable without SSH.
/// Logging: step start/ok/fail with timings and the output tail on failure. Commands are
/// never logged (the .env step carries the secret, base64-encoded).
/// </summary>
public sealed class RemoteServerBootstrapper
{
    private readonly ISshRunner _ssh;

    public RemoteServerBootstrapper(ISshRunner ssh) => _ssh = ssh;

    private const string NeedSudo =
        "sudo -n true 2>/dev/null || { echo 'Passwordless sudo is required for this step.'; exit 1; }";

    public static string BuildInstallDockerCommand() =>
        "if command -v docker >/dev/null 2>&1 && command -v git >/dev/null 2>&1; then echo 'Docker and git already installed.'; else " +
        NeedSudo + "; " +
        "if command -v dnf >/dev/null 2>&1; then sudo dnf install -y docker git; " +
        "elif command -v apt-get >/dev/null 2>&1; then sudo apt-get update -y && sudo apt-get install -y docker.io docker-compose-v2 git curl unzip; " +
        "else echo 'Unsupported OS: need dnf or apt-get.'; exit 1; fi; fi && " +
        "sudo -n systemctl enable --now docker && " +
        "sudo -n usermod -aG docker \"$USER\"";

    public static string BuildEnsureComposeCommand() =>
        "if docker compose version >/dev/null 2>&1 || sudo -n docker compose version >/dev/null 2>&1; then echo 'Docker Compose plugin present.'; else " +
        NeedSudo + "; " +
        "sudo mkdir -p /usr/local/lib/docker/cli-plugins && " +
        "sudo curl -fsSL \"https://github.com/docker/compose/releases/latest/download/docker-compose-linux-$(uname -m)\" " +
        "-o /usr/local/lib/docker/cli-plugins/docker-compose && " +
        "sudo chmod +x /usr/local/lib/docker/cli-plugins/docker-compose && " +
        "sudo -n docker compose version; fi";

    public static string BuildInstallAwsCliCommand() =>
        "if command -v aws >/dev/null 2>&1; then echo 'AWS CLI already installed.'; else " +
        NeedSudo + "; " +
        "if ! command -v unzip >/dev/null 2>&1; then " +
        "if command -v dnf >/dev/null 2>&1; then sudo dnf install -y unzip; else sudo apt-get install -y unzip; fi; fi; " +
        "curl -fsSL -o /tmp/awscliv2.zip \"https://awscli.amazonaws.com/awscli-exe-linux-$(uname -m).zip\" && " +
        "unzip -q -o /tmp/awscliv2.zip -d /tmp && sudo /tmp/aws/install --update; fi";

    public static string BuildCloneOrPullCommand(ServerConfig server)
    {
        var dir = server.RemoteWorkingDir.TrimEnd('/');
        return $"mkdir -p '{dir}' && cd '{dir}' && " +
               $"if [ -d .git ]; then git pull --ff-only; else git clone '{server.ComposeRepoUrl}' .; fi";
    }

    /// <summary>Null when no ECR registry is configured (login is skipped).</summary>
    public static string? BuildEcrLoginCommand(ServerConfig server)
    {
        if (string.IsNullOrWhiteSpace(server.EcrRegistry)) return null;
        var registry = server.EcrRegistry.Trim();
        var region = string.IsNullOrWhiteSpace(server.EcrRegion) ? RegionFromRegistry(registry) : server.EcrRegion.Trim();
        return $"aws ecr get-login-password --region {region} | " +
               $"docker login --username AWS --password-stdin {registry}";
    }

    private static string RegionFromRegistry(string registry)
    {
        // <account>.dkr.ecr.<region>.amazonaws.com
        var parts = registry.Split('.');
        return parts.Length >= 6 ? parts[3] : "us-east-1";
    }

    /// <summary>ECR login only — reused by deploys to refresh the 12h token.</summary>
    public async Task<int> EcrLoginAsync(ServerConfig server, Func<string, Task> onLine, CancellationToken ct = default)
    {
        var cmd = BuildEcrLoginCommand(server);
        if (cmd is null) return 0;
        return await _ssh.RunAsync(server.Host, server.Username, server.SshKeyPath, cmd, onLine, onLine, ct);
    }

    public async Task<BootstrapResult> RunAsync(
        ServerConfig server, Func<string, Task> onLine, CancellationToken ct = default)
    {
        var log = Log.ForContext<RemoteServerBootstrapper>()
            .ForContext("ServerId", server.Id).ForContext("Host", server.Host);

        if (string.IsNullOrWhiteSpace(server.ComposeRepoUrl))
        {
            log.Warning("Bootstrap rejected: compose repo URL missing");
            return new(false, "Compose repo URL is required before preparing a server.");
        }
        if (string.IsNullOrWhiteSpace(server.RemoteWorkingDir))
        {
            log.Warning("Bootstrap rejected: remote working dir missing");
            return new(false, "Remote working directory is required before preparing a server.");
        }

        var steps = new List<(string Name, string Command)>
        {
            ("Install Docker + git", BuildInstallDockerCommand()),
            ("Ensure Docker Compose plugin", BuildEnsureComposeCommand()),
            ("Install AWS CLI", BuildInstallAwsCliCommand()),
            ("Compose repo clone/pull", BuildCloneOrPullCommand(server)),
        };

        if (!string.IsNullOrWhiteSpace(server.EnvFile))
            steps.Add(("Write .env", RemoteEnvFileWriter.BuildWriteEnvFileCommand(server.RemoteWorkingDir, server.EnvFile)));

        var ecr = BuildEcrLoginCommand(server);
        if (ecr is not null)
            steps.Add(("ECR login", ecr));

        log.Information("Bootstrap started: user={User} workingDir={Dir} repo={Repo} steps={Steps} env={HasEnv} ecr={HasEcr}",
            server.Username, server.RemoteWorkingDir, server.ComposeRepoUrl, steps.Count,
            !string.IsNullOrWhiteSpace(server.EnvFile), ecr is not null);
        var total = Stopwatch.StartNew();

        for (var i = 0; i < steps.Count; i++)
        {
            var (name, command) = steps[i];
            await onLine($"[bootstrap {i + 1}/{steps.Count}] {name}…");
            log.Information("Bootstrap step {Index}/{Count} '{Step}' starting", i + 1, steps.Count, name);

            // Keep the last lines of output so a failure is diagnosable from the log file alone.
            var tail = new Queue<string>();
            Task Capture(string l)
            {
                lock (tail) { tail.Enqueue(l); while (tail.Count > 12) tail.Dequeue(); }
                return onLine(l);
            }

            var sw = Stopwatch.StartNew();
            int exit;
            try
            {
                exit = await _ssh.RunAsync(server.Host, server.Username, server.SshKeyPath, command, Capture, Capture, ct);
            }
            catch (Exception ex)
            {
                log.Error(ex, "Bootstrap step '{Step}' threw after {ElapsedMs}ms (SSH connect/auth/transport error?)",
                    name, sw.ElapsedMilliseconds);
                await onLine($"[bootstrap] ✗ {name} error: {ex.Message}");
                return new(false, $"Step '{name}' error: {ex.Message}", name);
            }

            if (exit != 0)
            {
                string[] last; lock (tail) last = tail.ToArray();
                log.Error("Bootstrap step '{Step}' failed exit={ExitCode} after {ElapsedMs}ms. Output tail: {Tail}",
                    name, exit, sw.ElapsedMilliseconds, string.Join(" | ", last));
                await onLine($"[bootstrap] ✗ {name} failed (exit {exit}).");
                return new(false, $"Step '{name}' failed (exit {exit}).", name);
            }

            log.Information("Bootstrap step '{Step}' ok in {ElapsedMs}ms", name, sw.ElapsedMilliseconds);
            await onLine($"[bootstrap] ✓ {name}");
        }

        log.Information("Bootstrap complete in {ElapsedMs}ms", total.ElapsedMilliseconds);
        return new(true, "Server is ready.");
    }
}
