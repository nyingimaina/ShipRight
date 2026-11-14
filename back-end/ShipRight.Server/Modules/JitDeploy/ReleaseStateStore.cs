using System.Text;
using ShipRight.Shared.SshRunner;

namespace ShipRight.Modules.JitDeploy;

/// <summary>
/// Reads and writes the deploy state file on the server over SSH. A missing file is a clean slate; a file that is
/// there but unreadable is an error (never silently treated as "no state", which would lose the rollback target).
/// Writes go to a temp file and are renamed into place, so a crash never leaves a half-written file.
/// </summary>
public sealed class ReleaseStateStore(ISshRunner ssh)
{
    public const string NoStateSentinel = "# no-state";

    public static string BuildReadCommand(JitTarget t)
    {
        JitNames.Path(t.ProjectDir);
        var f = ShellQuote.Single(t.StateFile);
        return $"if [ -f {f} ]; then cat {f}; else echo '{NoStateSentinel}'; fi";
    }

    public static string BuildWriteCommand(JitTarget t, ReleaseState state)
    {
        JitNames.Path(t.ProjectDir);
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(ReleaseStateJson.Serialize(state) + "\n"));
        var dir = ShellQuote.Single(t.StateDir);
        var tmp = ShellQuote.Single(t.StateFile + ".tmp");
        var file = ShellQuote.Single(t.StateFile);
        return $"mkdir -p {dir} && umask 077 && echo '{b64}' | base64 -d > {tmp} && mv -f {tmp} {file}";
    }

    public async Task<ReleaseState> ReadAsync(JitTarget t, CancellationToken ct = default)
    {
        var lines = new List<string>();
        var exit = await ssh.RunAsync(t.Host, t.Username, t.SshKeyPath, BuildReadCommand(t),
            line => { lock (lines) lines.Add(line); return Task.CompletedTask; },
            _ => Task.CompletedTask,          // stderr (host key notices etc.) must not be mistaken for state
            ct);
        if (exit != 0) throw new InvalidOperationException($"Could not read the deploy state on {t.Host} (exit {exit}).");

        string text; lock (lines) text = string.Join("\n", lines);
        return text.Trim() == NoStateSentinel ? ReleaseState.Empty : ReleaseStateJson.Deserialize(text);
    }

    public async Task WriteAsync(JitTarget t, ReleaseState state, CancellationToken ct = default)
    {
        var exit = await ssh.RunAsync(t.Host, t.Username, t.SshKeyPath, BuildWriteCommand(t, state), null, null, ct);
        if (exit != 0) throw new InvalidOperationException($"Could not save the deploy state on {t.Host} (exit {exit}).");
    }
}
