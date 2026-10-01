using Serilog;
using ShipRight.Modules.Projects;
using ShipRight.Shared.SshRunner;

namespace ShipRight.Modules.RemoteHost;

public sealed record RemoteHealthResult(bool Healthy, string Message);

/// <summary>
/// Probes the app from the server itself over SSH (curl against localhost) with retries.
/// On failure, collects `docker compose ps` and a log tail for the build log.
/// </summary>
public sealed class RemoteHealthChecker
{
    public const string DefaultUrl = "http://localhost:5200/api/health";

    private readonly ISshRunner _ssh;
    private readonly int _maxAttempts;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public RemoteHealthChecker(ISshRunner ssh, int maxAttempts = 12,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _ssh = ssh;
        _maxAttempts = maxAttempts;
        _delay = delay ?? Task.Delay;
    }

    public static string BuildProbeCommand(ServerConfig server)
    {
        var url = string.IsNullOrWhiteSpace(server.HealthCheckUrl) ? DefaultUrl : server.HealthCheckUrl.Trim();
        return $"curl -sf --max-time 5 '{url}'";
    }

    public static string BuildDiagnosticsCommand(ServerConfig server) =>
        $"cd '{server.RemoteWorkingDir}' && docker compose ps; docker compose logs --tail 50";

    public async Task<RemoteHealthResult> CheckAsync(
        ServerConfig server, Func<string, Task> onLine, CancellationToken ct = default)
    {
        var log = Log.ForContext<RemoteHealthChecker>()
            .ForContext("ServerId", server.Id).ForContext("Host", server.Host);
        var probe = BuildProbeCommand(server);
        log.Information("Health check started: {Probe} maxAttempts={Max}", probe, _maxAttempts);

        for (var attempt = 1; attempt <= _maxAttempts; attempt++)
        {
            await onLine($"[health] probe {attempt}/{_maxAttempts}…");
            var exit = await _ssh.RunAsync(server.Host, server.Username, server.SshKeyPath, probe,
                onOutput: onLine, onStderr: onLine, ct: ct);
            if (exit == 0)
            {
                log.Information("Health check passed on attempt {Attempt}/{Max}", attempt, _maxAttempts);
                return new(true, "Health check passed.");
            }

            log.Warning("Health probe {Attempt}/{Max} failed exit={ExitCode} (curl: 7=refused, 22=HTTP error, 28=timeout)",
                attempt, _maxAttempts, exit);
            if (attempt < _maxAttempts)
                await _delay(TimeSpan.FromSeconds(5), ct);
        }

        log.Error("Health check failed after {Max} attempts — collecting diagnostics", _maxAttempts);
        await onLine("[health] app did not become healthy — collecting diagnostics…");
        var diag = new List<string>();
        await _ssh.RunAsync(server.Host, server.Username, server.SshKeyPath,
            BuildDiagnosticsCommand(server),
            onOutput: l => { lock (diag) diag.Add(l); return onLine(l); },
            onStderr: l => { lock (diag) diag.Add(l); return onLine(l); }, ct: ct);
        string[] snapshot; lock (diag) snapshot = diag.TakeLast(60).ToArray();
        log.Error("Diagnostics after failed health check: {Diagnostics}", string.Join(" | ", snapshot));
        return new(false, $"Health check failed after {_maxAttempts} attempts.");
    }
}
