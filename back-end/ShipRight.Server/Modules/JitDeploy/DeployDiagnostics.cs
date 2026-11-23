using System.Text.RegularExpressions;

namespace ShipRight.Modules.JitDeploy;

/// <summary>
/// Turns a failed remote command into something a person can act on. Docker writes most compose errors to stderr,
/// so both streams are kept here. They stay separate: a warning on stderr must not be folded into the JSON that is
/// parsed when the check succeeds.
/// </summary>
public static class DeployDiagnostics
{
    private const int CommitPrefixLength = 7;

    /// <summary>Says which release failed, what compose said, and (when known) what to fix.</summary>
    public static string ComposeFailure(string? composeFile, string tag, string sha, IEnumerable<string> output, IEnumerable<string> errors)
    {
        var all = output.Concat(errors).ToList();
        var commit = sha.Length >= CommitPrefixLength ? sha[..CommitPrefixLength] : sha;
        var file = composeFile ?? "docker-compose.yml";
        return $"The compose file is not valid for commit {commit} (release {tag}, {file}): {Tail(all)}.{Hint(all)}";
    }

    /// <summary>Plain-language next step for the errors people actually hit; empty when compose already said enough.</summary>
    public static string Hint(IEnumerable<string> lines)
    {
        var text = string.Join("\n", lines);
        var missing = Regex.Match(text, @"required variable\s+(\S+)\s+is missing a value", RegexOptions.IgnoreCase);
        if (missing.Success)
            return $" Set {missing.Groups[1].Value} in the project's build-on-server variables (or the server settings) and deploy again.";
        if (text.Contains("Additional property", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("unsupported config option", StringComparison.OrdinalIgnoreCase))
            return " This Docker Compose version does not understand that option; check the field name or upgrade Compose on the server.";
        if (text.Contains("no such file or directory", StringComparison.OrdinalIgnoreCase))
            return " Check that the compose file and every file it mounts exist at that commit.";
        return string.Empty;
    }

    private static string Tail(IEnumerable<string> lines, int n = 2) =>
        string.Join(" | ", lines.Select(l => l.Trim()).Where(l => l.Length > 0).TakeLast(n));
}
