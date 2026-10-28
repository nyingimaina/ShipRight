using ShipRight.Shared.ProcessRunner;

namespace ShipRight.Modules.Setup;

public record DirtyEntry(string Status, string Path, string Description);

/// <param name="IsClean">True only when the tree has no unsaved changes AND there is a commit to build.</param>
/// <param name="Sha">The commit that would be built; null when there is none.</param>
/// <param name="Error">Why the check could not give a clean answer (not a repo, no commits).</param>
public record CleanTreeResult(bool IsClean, string? Sha, IReadOnlyList<DirtyEntry> Entries, string? Error);

/// <summary>
/// Builds must come from a saved commit, never from whatever happens to be on disk. This answers "can I build
/// this repo, and which commit would that be?" - untracked files count as unsaved so a forgotten new file
/// cannot silently be missing from a build.
/// </summary>
public sealed class CleanTreeGuard
{
    private readonly IProcessRunner _runner;

    public CleanTreeGuard(IProcessRunner runner) => _runner = runner;

    public async Task<CleanTreeResult> CheckAsync(string repoPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(repoPath)) throw new ArgumentException("A repository path is required.", nameof(repoPath));

        var status = await _runner.RunAsync("git",
            ["-C", repoPath, "status", "--porcelain=v1", "--untracked-files=normal"], null, ct: ct);
        if (!status.Success)
            return new CleanTreeResult(false, null, [], "This folder is not a git repository yet.");

        var entries = ParsePorcelain(status.StdOut);

        var head = await _runner.RunAsync("git", ["-C", repoPath, "rev-parse", "HEAD"], null, ct: ct);
        if (!head.Success)
            return new CleanTreeResult(false, null, entries, "This project has no commits yet. Save your work as a first commit.");

        return new CleanTreeResult(entries.Count == 0, head.StdOut.Trim(), entries, null);
    }

    public static IReadOnlyList<DirtyEntry> ParsePorcelain(string output)
    {
        var entries = new List<DirtyEntry>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length < 4) continue;

            var code = line[..2];
            var path = line[3..];
            var arrow = path.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrow >= 0 && (code[0] == 'R' || code[0] == 'C' || code[1] == 'R' || code[1] == 'C')) path = path[(arrow + 4)..];
            if (path.Length >= 2 && path[0] == '"' && path[^1] == '"') path = path[1..^1];

            entries.Add(new DirtyEntry(code, path, Describe(code)));
        }
        return entries;
    }

    private static string Describe(string code)
    {
        if (code == "??") return "New file, not saved to git yet";
        if (code is "UU" or "AA" or "DD" or "AU" or "UA" or "DU" or "UD") return "Merge conflict";
        if (code.Contains('R')) return "Renamed";
        if (code.Contains('D')) return "Deleted";
        if (code[0] == 'A' && code[1] == ' ') return "New file, ready to commit";
        return "Changed";
    }
}
