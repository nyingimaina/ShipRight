using System.Text.RegularExpressions;
using ShipRight.Modules.Projects;
using ShipRight.Shared.SshRunner;

namespace ShipRight.Modules.RemoteHost;

/// <summary>
/// Server-side checkpoint log + recovery helpers. Every prepare/deploy stage appends
/// START / OK / FAIL lines to ~/.shipright/checkpoints.log on the server, so a failed run can be
/// diagnosed (and the stage re-run, since every stage is idempotent) without the app's own logs.
/// Pure string builders — unit-testable without SSH.
/// </summary>
public static class RemoteCheckpoint
{
    public const string RemoteLogPath = "$HOME/.shipright/checkpoints.log";

    /// <summary>
    /// Idempotent repo sync. Unlike `git clone .`, this works when the directory already holds files
    /// (e.g. an earlier .env write or a half-finished run) but is not yet a git repo.
    /// </summary>
    public static string BuildSyncRepoCommand(ServerConfig server)
    {
        var dir = server.RemoteWorkingDir.TrimEnd('/');
        var url = server.ComposeRepoUrl;
        return $"mkdir -p '{dir}' && cd '{dir}' && " +
               "if [ -d .git ]; then " +
               $"(git remote set-url origin '{url}' 2>/dev/null || git remote add origin '{url}') && git pull --ff-only; " +
               "else " +
               $"git init -q && (git remote add origin '{url}' 2>/dev/null || git remote set-url origin '{url}') && " +
               "git fetch origin && " +
               "BR=$(git remote show origin | sed -n 's/.*HEAD branch: //p') && " +
               "[ -n \"$BR\" ] && [ \"$BR\" != '(unknown)' ] || { echo 'Remote has no default branch (empty repo?).'; exit 1; }; " +
               "git checkout -f -B \"$BR\" \"origin/$BR\"; fi";
    }

    /// <summary>Wraps a stage so it logs START/OK/FAIL on the server and keeps the original exit code.</summary>
    public static string Wrap(string action, string stage, string command)
    {
        var label = Sanitize($"{action}/{stage}");
        const string now = "\"$(date -u +%Y-%m-%dT%H:%M:%SZ)\"";
        const string file = "\"$HOME/.shipright/checkpoints.log\"";
        string Log(string state) => $"printf '%s {label} {state}\\n' {now} >> {file}";
        return "mkdir -p \"$HOME/.shipright\"; " + Log("START") + "; (\n" + command + "\n); rc=$?; " +
               "if [ $rc -eq 0 ]; then " + Log("OK") + "; " +
               $"else printf '%s {label} FAIL exit=%s\\n' {now} \"$rc\" >> {file}; fi; exit $rc";
    }

    /// <summary>Read-only snapshot of the server state, run after a failure.</summary>
    public static string BuildDiagnosticsCommand(ServerConfig server)
    {
        var dir = server.RemoteWorkingDir.TrimEnd('/');
        return "echo '--- diagnostics ---'; whoami; df -h / | tail -n 1; " +
               $"echo '# ls -la {dir}'; ls -la '{dir}' 2>&1 | head -n 30; " +
               $"echo '# git status'; git -C '{dir}' status 2>&1 | head -n 15; " +
               $"echo '# docker compose config'; cd '{dir}' && docker compose config -q 2>&1 | head -n 15; " +
               "echo '# docker compose ps'; docker compose ps -a 2>&1 | head -n 15; " +
               "echo '# docker compose logs (last 25)'; docker compose logs --tail 25 --no-color 2>&1 | head -n 60; " +
               "echo '# checkpoints.log (last 20)'; tail -n 20 \"$HOME/.shipright/checkpoints.log\" 2>&1; true";
    }

    /// <summary>Per-container image, health and log tail — the part that explains an "unhealthy" dependency.</summary>
    public static string BuildContainerDiagnosticsCommand(ServerConfig server)
    {
        var dir = server.RemoteWorkingDir.TrimEnd('/');
        return $"cd '{dir}' 2>&1; echo '# containers (name / image / status)'; " +
               "docker ps -a --format '{{.Names}} / {{.Image}} / {{.Status}}' 2>&1 | head -n 20; " +
               "for c in $(docker ps -aq 2>/dev/null | head -n 6); do " +
               "echo \"# $(docker inspect -f '{{.Name}}' $c) health: $(docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{else}}n/a{{end}} exit={{.State.ExitCode}} oom={{.State.OOMKilled}}' $c)\"; " +
               "docker inspect -f '{{if .State.Health}}{{range .State.Health.Log}}{{.Output}}{{end}}{{end}}' $c 2>&1 | tail -n 5; " +
               "docker logs --tail 40 $c 2>&1; done; " +
               "echo '# memory'; free -m 2>&1 | head -n 2; true";
    }

    /// <summary>Runs diagnostics, streaming each line to onLine. Never throws — it must not mask the real error.</summary>
    public static async Task RunDiagnosticsAsync(
        ISshRunner ssh, ServerConfig server, Func<string, Task> onLine, Serilog.ILogger log, CancellationToken ct)
    {
        try
        {
            await onLine("[diagnostics] collecting server state…");
            Task Emit(string l)
            {
                log.Information("[diagnostics] {Line}", l);
                return onLine(l);
            }
            await ssh.RunAsync(server.Host, server.Username, server.SshKeyPath,
                BuildDiagnosticsCommand(server) + "; " + BuildContainerDiagnosticsCommand(server), Emit, Emit, ct);
            await onLine($"[diagnostics] done. Full history is on the server at ~/.shipright/checkpoints.log");
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Diagnostics could not be collected");
            await onLine($"[diagnostics] could not collect: {ex.Message}");
        }
    }

    private static string Sanitize(string s) => Regex.Replace(s, @"[^A-Za-z0-9 _./:-]", "");
}
