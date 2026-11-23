using ShipRight.Modules.Projects;
using ShipRight.Modules.Setup;
using ShipRight.Shared.ProcessRunner;

namespace ShipRight.Modules.JitDeploy;

public sealed record JitLocalBranchCheck(
    bool Applicable,
    string? RepoPath,
    string? Branch,
    bool Clean,
    int Ahead,
    string? Problem);

/// <summary>
/// Build-on-server fetches origin/branch on the target machine, so local work that was never committed or pushed is
/// invisible to the deploy. This preflight finds the project's local checkout for the configured remote and refuses
/// a deploy that would silently ship stale code. It only reads git state; committing and pushing stay with the person.
/// </summary>
public sealed class JitLocalBranchGuard(IProcessRunner runner)
{
    public async Task<JitLocalBranchCheck> CheckAsync(ProjectConfig project, JitDeployConfig cfg, CancellationToken ct = default)
    {
        var wanted = GitAddressForms.Analyse(cfg.RepoUrl).Key;
        if (wanted.Length == 0)
            return new(false, null, cfg.Branch, true, 0, null);

        string? path = null;
        foreach (var repo in project.GitRepos)
        {
            if (string.IsNullOrWhiteSpace(repo.RepoPath)) continue;
            var origin = await runner.RunAsync("git", ["-C", repo.RepoPath, "remote", "get-url", "origin"], null, ct: ct);
            if (!origin.Success) continue;
            if (string.Equals(GitAddressForms.Analyse(origin.StdOut.Trim()).Key, wanted, StringComparison.OrdinalIgnoreCase))
            {
                path = repo.RepoPath;
                break;
            }
        }

        if (path is null)
            return new(false, null, cfg.Branch, true, 0, null);   // nothing local to check: the URL may be a CI-only mirror

        var branch = cfg.Branch;
        var current = await runner.RunAsync("git", ["-C", path, "rev-parse", "--abbrev-ref", "HEAD"], null, ct: ct);
        if (!current.Success)
            return new(true, path, branch, true, 0, $"Cannot read the current branch of {path}. Check that it is a git repository.");
        var on = current.StdOut.Trim();
        if (!string.Equals(on, branch, StringComparison.OrdinalIgnoreCase))
            return new(true, path, branch, true, 0,
                $"Your local checkout is on branch '{on}', but this project deploys '{branch}'. Switch to '{branch}', commit and push your changes, then deploy again.");

        var status = await runner.RunAsync("git", ["-C", path, "status", "--porcelain=v1", "--untracked-files=normal"], null, ct: ct);
        if (!status.Success)
            return new(true, path, branch, true, 0, $"Cannot read the state of {path}. Check that it is a git repository.");
        var entries = CleanTreeGuard.ParsePorcelain(status.StdOut);
        if (entries.Count > 0)
        {
            var sample = string.Join(", ", entries.Take(3).Select(e => e.Path));
            var more = entries.Count > 3 ? $" and {entries.Count - 3} more" : "";
            return new(true, path, branch, false, 0,
                $"Commit your local changes before deploying ({sample}{more}). The server fetches committed code, so unsaved changes would be left out.");
        }

        var upstream = await runner.RunAsync("git", ["-C", path, "rev-parse", "--verify", "--quiet", $"origin/{branch}"], null, ct: ct);
        if (!upstream.Success)
            return new(true, path, branch, true, 0,
                $"Branch '{branch}' has not been pushed to origin yet. Push it before deploying.");

        var counts = await runner.RunAsync("git", ["-C", path, "rev-list", "--left-right", "--count", $"origin/{branch}...HEAD"], null, ct: ct);
        if (!counts.Success)
            return new(true, path, branch, true, 0, $"Cannot compare '{branch}' with origin. Push your commits before deploying.");
        var parts = counts.StdOut.Trim().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        var ahead = parts.Length == 2 && int.TryParse(parts[1], out var n) ? n : -1;
        if (ahead > 0)
            return new(true, path, branch, true, ahead,
                $"You have {ahead} commit{(ahead == 1 ? "" : "s")} on '{branch}' that {(ahead == 1 ? "has" : "have")} not been pushed. Push them before deploying.");

        return new(true, path, branch, true, Math.Max(ahead, 0), null);
    }
}
