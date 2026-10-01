using System.Diagnostics;
using Serilog;
using ShipRight.Modules.Projects;
using ShipRight.Shared.SshRunner;

namespace ShipRight.Modules.RemoteHost;

public sealed record RemoteDeployResult(bool Success, string Message);

/// <summary>
/// Deploys the compose stack on a prepared server over SSH:
/// write .env → refresh ECR login → git pull + compose pull/up → health check.
/// Succeeds only when the health probe passes. Each stage is logged with timing and, on
/// failure, the output tail. Commands are never logged (the .env stage carries secrets).
/// </summary>
public sealed class RemoteServerDeployer
{
    private readonly ISshRunner _ssh;
    private readonly RemoteHealthChecker _health;

    public RemoteServerDeployer(ISshRunner ssh, RemoteHealthChecker health)
    {
        _ssh = ssh;
        _health = health;
    }

    public static string BuildDeployCommand(ServerConfig server) =>
        $"cd '{server.RemoteWorkingDir.TrimEnd('/')}' && git pull --ff-only && " +
        "docker compose pull && docker compose up -d --remove-orphans";

    public async Task<RemoteDeployResult> DeployAsync(
        ServerConfig server, Func<string, Task> onLine, CancellationToken ct = default)
    {
        var log = Log.ForContext<RemoteServerDeployer>()
            .ForContext("ServerId", server.Id).ForContext("Host", server.Host);
        var total = Stopwatch.StartNew();
        log.Information("Deploy started: user={User} workingDir={Dir} env={HasEnv} ecr={HasEcr}",
            server.Username, server.RemoteWorkingDir,
            !string.IsNullOrWhiteSpace(server.EnvFile), !string.IsNullOrWhiteSpace(server.EcrRegistry));

        async Task<int> Run(string stage, string command)
        {
            var tail = new Queue<string>();
            Task Capture(string l)
            {
                lock (tail) { tail.Enqueue(l); while (tail.Count > 12) tail.Dequeue(); }
                return onLine(l);
            }

            var sw = Stopwatch.StartNew();
            try
            {
                var exit = await _ssh.RunAsync(server.Host, server.Username, server.SshKeyPath, command, Capture, Capture, ct);
                if (exit == 0)
                {
                    log.Information("Deploy stage '{Stage}' ok in {ElapsedMs}ms", stage, sw.ElapsedMilliseconds);
                }
                else
                {
                    string[] last; lock (tail) last = tail.ToArray();
                    log.Error("Deploy stage '{Stage}' failed exit={ExitCode} after {ElapsedMs}ms. Output tail: {Tail}",
                        stage, exit, sw.ElapsedMilliseconds, string.Join(" | ", last));
                }
                return exit;
            }
            catch (Exception ex)
            {
                log.Error(ex, "Deploy stage '{Stage}' threw after {ElapsedMs}ms (SSH connect/auth/transport error?)",
                    stage, sw.ElapsedMilliseconds);
                throw;
            }
        }

        if (!string.IsNullOrWhiteSpace(server.EnvFile))
        {
            await onLine("[deploy] writing .env…");
            var exit = await Run("env-file", RemoteEnvFileWriter.BuildWriteEnvFileCommand(server.RemoteWorkingDir, server.EnvFile));
            if (exit != 0)
                return new(false, $"Writing .env failed (exit {exit}).");
        }

        var ecr = RemoteServerBootstrapper.BuildEcrLoginCommand(server);
        if (ecr is not null)
        {
            await onLine("[deploy] refreshing ECR login…");
            var exit = await Run("ecr-login", ecr);
            if (exit != 0)
                return new(false, $"ECR login failed (exit {exit}). Check the instance role and region.");
        }

        await onLine("[deploy] pulling and restarting containers…");
        var deployExit = await Run("compose-up", BuildDeployCommand(server));
        if (deployExit != 0)
            return new(false, $"Deploy command failed (exit {deployExit}).");

        if (server.SkipHealthCheck)
        {
            await onLine("[deploy] health check skipped (turned off for this server).");
            log.Information("Deploy succeeded in {ElapsedMs}ms (health check skipped by configuration)", total.ElapsedMilliseconds);
            return new(true, "Deployed (health check skipped).");
        }

        var health = await _health.CheckAsync(server, onLine, ct);
        if (health.Healthy) log.Information("Deploy succeeded in {ElapsedMs}ms", total.ElapsedMilliseconds);
        else log.Error("Deploy finished unhealthy after {ElapsedMs}ms: {Message}", total.ElapsedMilliseconds, health.Message);

        return health.Healthy
            ? new(true, "Deployed and healthy.")
            : new(false, health.Message);
    }
}
