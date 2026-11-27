using ShipRight.Modules.Projects;
using ShipRight.Modules.Setup;
using ShipRight.Shared.ProcessRunner;

namespace ShipRight.Modules.JitDeploy;

/// <summary>What one local checkout looked like when the deploy preflight examined it.</summary>
public sealed record JitRepoProbe(
    string Path,
    string Origin,
    string? CurrentBranch,
    bool OnDeployBranch,
    bool RemoteRefreshed,
    IReadOnlyList<string> TrackedChanges,
    IReadOnlyList<string> UntrackedFiles,
    string? LocalRef,
    string? RemoteRef,
    int Ahead,
    int Behind,
    IReadOnlyList<string> Warnings,
    string? Problem);

/// <summary>The full preflight report for every local checkout that serves the deploy's git address.</summary>
public sealed record JitGuardReport(
    bool Applicable,
    string Branch,
    string? InspectedRepoPath,
    IReadOnlyList<JitRepoProbe> Repos,
    IReadOnlyList<string> Warnings,
    string? Problem);

public sealed record JitLocalBranchCheck(
    bool Applicable,
    string? RepoPath,
    string? Branch,
    bool Clean,
    int Ahead,
    string? Problem)
{
    /// <summary>The detailed report, for the diagnostics download. Null when the guard was not run.</summary>
    public JitGuardReport? Report { get; init; }

    public IReadOnlyList<string> Warnings => Report?.Warnings ?? [];
}

/// <summary>
/// Build-on-server fetches origin/branch on the target machine, so local work that was never committed or pushed is
/// invisible to the deploy. This preflight finds every local checkout that serves the configured remote and refuses a
/// deploy that would silently ship stale code. It refreshes only the remote-tracking ref for the deploy branch (the
/// checkout, the index and every other branch are untouched); committing and pushing stay with the person.
///
/// The deploy builds a <em>ref</em>, not whatever is open on disk, so the comparisons are made against
/// <c>refs/heads/&lt;branch&gt;</c>: a checkout on another branch or a detached HEAD is fine as long as that branch is
/// fully pushed. Untracked files are reported as a warning (a commit can never include them); tracked changes on the
/// branch being deployed block the deploy.
/// </summary>
public sealed class JitLocalBranchGuard(IProcessRunner runner)
{
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(60);

    public async Task<JitLocalBranchCheck> CheckAsync(ProjectConfig project, JitDeployConfig cfg, CancellationToken ct = default)
    {
        var wanted = GitAddressForms.Analyse(cfg.RepoUrl).Key;
        if (wanted.Length == 0)
            return new(false, null, cfg.Branch, true, 0, null);

        var branch = cfg.Branch;
        var probes = new List<JitRepoProbe>();
        var outerWarnings = new List<string>();

        foreach (var repo in project.GitRepos)
        {
            if (string.IsNullOrWhiteSpace(repo.RepoPath)) continue;
            var path = repo.RepoPath.Trim();

            var origin = await RunGitAsync(path, ["remote", "get-url", "origin"], ct);
            if (origin is null || !origin.Success)
            {
                outerWarnings.Add($"Could not read the git address of '{path}' (it may not be a git repository, or it may be unavailable right now).");
                continue;
            }

            var originUrl = origin.StdOut.Trim();
            if (!string.Equals(GitAddressForms.Analyse(originUrl).Key, wanted, StringComparison.OrdinalIgnoreCase))
                continue;

            probes.Add(await ProbeAsync(path, originUrl, branch, ct));
        }

        if (probes.Count == 0)
            return new(false, null, branch, true, 0, null)
            {
                Report = new(false, branch, null, [], outerWarnings, null),
            };

        var blocking = probes.FirstOrDefault(p => p.Problem is not null);
        var warnings = outerWarnings.Concat(probes.SelectMany(p => p.Warnings)).ToList();
        var ahead = probes.Count == 0 ? 0 : probes.Max(p => p.Ahead);
        var clean = probes.All(p => p.TrackedChanges.Count == 0 || !p.OnDeployBranch);

        return new(
            Applicable: true,
            RepoPath: blocking?.Path ?? probes[0].Path,
            Branch: branch,
            Clean: clean,
            Ahead: Math.Max(ahead, 0),
            Problem: blocking?.Problem)
        {
            Report = new(true, branch, blocking?.Path ?? probes[0].Path, probes, warnings, blocking?.Problem),
        };
    }

    private async Task<JitRepoProbe> ProbeAsync(string path, string origin, string branch, CancellationToken ct)
    {
        var warnings = new List<string>();
        string? problem = null;

        var current = await RunGitAsync(path, ["rev-parse", "--abbrev-ref", "HEAD"], ct);
        var on = current is { Success: true } ? current.StdOut.Trim() : "(unknown)";
        var onDeployBranch = string.Equals(on, branch, StringComparison.OrdinalIgnoreCase);

        var status = await RunGitAsync(path, ["status", "--porcelain=v1", "--untracked-files=normal"], ct);
        var entries = status is { Success: true } ? CleanTreeGuard.ParsePorcelain(status.StdOut) : [];
        if (status is not { Success: true })
            warnings.Add($"Could not read the working tree of {path}.");

        var tracked = entries.Where(e => e.Status != "??").Select(e => e.Path).ToList();
        var untracked = entries.Where(e => e.Status == "??").Select(e => e.Path).ToList();

        // Refresh only the remote-tracking ref for this branch. The checkout, the index and every other branch are
        // untouched, so a checkout on a feature branch does not slow down or disturb the person's work.
        var fetch = await RunGitAsync(path,
            ["fetch", "--quiet", "--no-tags", "origin", $"+refs/heads/{branch}:refs/remotes/origin/{branch}"], ct, FetchTimeout);
        var refreshed = fetch is { Success: true };
        if (!refreshed)
            warnings.Add($"Could not refresh origin/{branch} from {path}; the last fetched copy was used. Check the network and the repository's credentials.");

        var localRef = await VerifyAsync(path, $"refs/heads/{branch}", ct);
        var remoteRef = await VerifyAsync(path, $"origin/{branch}", ct);
        var missingUpstream = fetch is { Success: false } &&
            (fetch.StdErr.Contains("couldn't find remote ref", StringComparison.OrdinalIgnoreCase) ||
             fetch.StdErr.Contains("remote ref does not exist", StringComparison.OrdinalIgnoreCase));

        if (remoteRef is null || missingUpstream)
        {
            problem = $"Branch '{branch}' has not been pushed to origin yet. Push it before deploying.";
            return new(path, origin, on, onDeployBranch, refreshed, tracked, untracked, localRef, remoteRef, 0, 0, warnings, problem);
        }

        var ahead = 0;
        var behind = 0;
        if (localRef is null)
        {
            warnings.Add($"There is no local branch '{branch}' in {path}; ShipRight compared origin/{branch} with itself. " +
                         "If you have work for this branch elsewhere, push it from that checkout.");
        }
        else
        {
            var counts = await RunGitAsync(path, ["rev-list", "--left-right", "--count", $"origin/{branch}...refs/heads/{branch}"], ct);
            if (counts is not { Success: true })
            {
                problem = $"Cannot compare '{branch}' with origin in {path}. Fetch the branch and push any local commits before deploying.";
            }
            else
            {
                var parts = counts.StdOut.Trim().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                behind = parts.Length == 2 && int.TryParse(parts[0], out var b) ? b : 0;
                ahead = parts.Length == 2 && int.TryParse(parts[1], out var a) ? a : 0;
                if (ahead > 0)
                    problem = refreshed
                        ? $"You have {ahead} commit{(ahead == 1 ? "" : "s")} on '{branch}' that {(ahead == 1 ? "has" : "have")} not been pushed. Push them before deploying."
                        : $"ShipRight could not refresh origin/{branch} from {path}, and the last fetched copy says you have {ahead} unpushed commit{(ahead == 1 ? "" : "s")}. " +
                          "Check your connection, run git fetch, and deploy again. If those commits are already on origin, this check could not see them.";
            }
        }

        if (!onDeployBranch)
            warnings.Add($"The checkout at {path} is on '{on}', not '{branch}'. ShipRight checked the pushed '{branch}' branch, not what is open on disk.");

        if (tracked.Count > 0)
        {
            var sample = Sample(tracked);
            if (onDeployBranch)
            {
                // Windows checkouts often report every text file as changed when only its line endings differ. That is
                // not work the deploy would leave out, so prove it: ask git for differences ignoring CR at end of line.
                var content = await RunGitAsync(path, ["diff", "--ignore-cr-at-eol", "--quiet", "HEAD", "--"], ct);
                if (content is { Success: true })
                {
                    warnings.Add($"{tracked.Count} file{(tracked.Count == 1 ? "" : "s")} in {path} differ from HEAD only in line endings. " +
                                 "They are not real changes, so the deploy is not blocked.");
                }
                else
                {
                    var pushed = remoteRef is null ? "" :
                        $" To deploy exactly the pushed commit instead, enter {Short(remoteRef)} in \"Deploy this exact commit\".";
                    var dirty = $"Commit your local changes before deploying ({sample}). The server fetches committed code, so unsaved changes would be left out.{pushed}";
                    problem = problem is null ? dirty : problem + " " + dirty;
                }
            }
            else
                warnings.Add($"The checkout at {path} has uncommitted changes on '{on}' ({sample}); they are not part of '{branch}', so the deploy does not include them.");
        }

        if (untracked.Count > 0)
            warnings.Add($"Untracked files in {path} are not part of any commit and will not be on the server: {Sample(untracked)}. " +
                         "Save them to git if the deploy needs them.");

        return new(path, origin, on, onDeployBranch, refreshed, tracked, untracked, localRef, remoteRef, ahead, behind, warnings, problem);
    }

    private static string Sample(IReadOnlyList<string> paths)
    {
        var name = string.Join(", ", paths.Take(3));
        return paths.Count > 3 ? $"{name} and {paths.Count - 3} more" : name;
    }

    private static string Short(string sha) => sha.Length <= 7 ? sha : sha[..7];

    private async Task<string?> VerifyAsync(string path, string reference, CancellationToken ct)
    {
        var result = await RunGitAsync(path, ["rev-parse", "--verify", "--quiet", reference], ct);
        if (result is null || !result.Success) return null;
        var sha = result.StdOut.Trim();
        return sha.Length == 0 ? null : sha;
    }

    private async Task<ProcessResult?> RunGitAsync(string path, string[] args, CancellationToken ct, TimeSpan? timeout = null)
    {
        try
        {
            return await runner.RunAsync("git", ["-C", path, .. args], null, ct: ct, timeout: timeout);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // A missing repository, a timeout or a broken git must never crash the deploy preflight: the caller reports
            // what it could and could not read, and the person can still deploy an explicit commit.
            return null;
        }
    }
}
