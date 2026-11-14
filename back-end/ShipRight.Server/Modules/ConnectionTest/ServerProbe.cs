using System.Text.RegularExpressions;
using ShipRight.Modules.JitDeploy;

namespace ShipRight.Modules.ConnectionTest;

/// <summary>
/// The single read-only command that asks a server about itself (login user, sudo, package manager, git, Docker,
/// Compose, free disk space and whether the deploy folder is usable) and the reading of its <c>key:value</c> answers.
/// It installs, writes and changes nothing, and never waits for a password (sudo is only ever asked with -n).
/// </summary>
public static class ServerProbe
{
    private static readonly Regex KeyShape = new("^[A-Za-z]+$", RegexOptions.Compiled);

    /// <summary>The command to send. <paramref name="workingDir"/> must be an absolute path or null; it is single-quoted, never executed.</summary>
    public static string Command(string? workingDir)
    {
        var dir = string.IsNullOrWhiteSpace(workingDir) ? "''" : ShellQuote.Single(JitNames.Path(workingDir));
        var script =
            $"dir={dir}; " +
            "echo \"user:$(id -un 2>/dev/null)\"; " +
            "if [ \"$(id -u 2>/dev/null)\" = 0 ]; then echo sudo:root; " +
            "elif command -v sudo >/dev/null 2>&1; then if sudo -n true >/dev/null 2>&1; then echo sudo:ok; else echo sudo:password; fi; " +
            "else echo sudo:none; fi; " +
            "if command -v apt-get >/dev/null 2>&1; then echo pkg:apt; elif command -v dnf >/dev/null 2>&1; then echo pkg:dnf; else echo pkg:none; fi; " +
            "if command -v git >/dev/null 2>&1; then echo git:ok; else echo git:missing; fi; " +
            "if command -v docker >/dev/null 2>&1; then echo docker:installed; " +
            "out=$(docker info 2>&1); rc=$?; " +
            "if [ $rc -eq 0 ]; then echo dockeruse:ok; " +
            "elif echo \"$out\" | grep -qi 'permission denied'; then echo dockeruse:needs-group; " +
            "else echo dockeruse:down; fi; " +
            "if docker compose version >/dev/null 2>&1; then echo compose:ok; else echo compose:missing; fi; " +
            "else echo docker:missing; fi; " +
            "if [ -n \"$dir\" ]; then t=\"$dir\"; while [ ! -d \"$t\" ] && [ \"$t\" != / ]; do t=$(dirname \"$t\"); done; " +
            "echo \"disk:$(df -Pm \"$t\" 2>/dev/null | awk 'NR==2{print $4}')\"; " +
            "if [ -d \"$dir\" ]; then if [ -w \"$dir\" ]; then echo dir:ok; else echo dir:readonly; fi; " +
            "elif [ -w \"$t\" ]; then echo dir:creatable; else echo dir:blocked; fi; " +
            "else echo dir:unset; echo \"disk:$(df -Pm \"$HOME\" 2>/dev/null | awk 'NR==2{print $4}')\"; fi; true";
        return "sh -c " + ShellQuote.Single(script);
    }

    public static ServerFacts Parse(IEnumerable<string> lines)
    {
        var facts = new ServerFacts();
        foreach (var raw in lines)
        {
            var i = raw.IndexOf(':');
            if (i <= 0) continue;
            var key = raw[..i].Trim();
            if (!KeyShape.IsMatch(key)) continue;
            facts.Set(key, raw[(i + 1)..].Trim());
        }
        return facts;
    }
}
