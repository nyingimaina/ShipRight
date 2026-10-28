using System.Text.RegularExpressions;
using ShipRight.Shared.ProcessRunner;

namespace ShipRight.Modules.Setup;

/// <param name="WorkDir">WSL path of the checkout to build from.</param>
/// <param name="MirrorDir">WSL path of ShipRight's private mirror the checkout belongs to.</param>
public record BuildSnapshot(string WorkDir, string Sha, string MirrorDir);

/// <summary>
/// Gives a build its own clean Linux-side checkout of one exact commit. The user's repository on the Windows
/// drive is only ever read: commits are fetched into a private bare mirror under ~/.shipright, and each build
/// is a throwaway worktree of that mirror. That keeps builds fast (ext4, not /mnt) and keeps the user's
/// .git free of Linux paths.
/// </summary>
public sealed class BuildSnapshotService
{
    private static readonly Regex SafeId = new(@"^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.Compiled);
    private static readonly Regex FullSha = new(@"^([0-9a-f]{40}|[0-9a-f]{64})$", RegexOptions.Compiled);
    private static readonly Regex OurWorkDir = new(@"^/[^\s]*/\.shipright/build/[A-Za-z0-9][A-Za-z0-9._-]*/[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.Compiled);
    private static readonly Regex OurMirror = new(@"^/[^\s]*/\.shipright/cache/[A-Za-z0-9][A-Za-z0-9._-]*\.git$", RegexOptions.Compiled);

    private readonly IProcessRunner _runner;

    public BuildSnapshotService(IProcessRunner runner) => _runner = runner;

    public async Task<BuildSnapshot> CreateAsync(
        string windowsRepoPath, string sha, string projectId, string buildId, CancellationToken ct = default)
    {
        if (!FullSha.IsMatch(sha ?? "")) throw new ArgumentException("A full commit hash is required.", nameof(sha));
        if (!SafeId.IsMatch(projectId ?? "")) throw new ArgumentException("Invalid project id.", nameof(projectId));
        if (!SafeId.IsMatch(buildId ?? "")) throw new ArgumentException("Invalid build id.", nameof(buildId));

        var root = await GetWslHomeAsync(ct) + "/.shipright";

        var mirror = $"{root}/cache/{projectId}.git";
        var workDir = $"{root}/build/{projectId}/{buildId}";
        var source = ProcessRunner.ToWslPath(windowsRepoPath);

        await Must(["mkdir", "-p", $"{root}/cache", $"{root}/build/{projectId}"], "prepare the build folders", ct);

        if (!(await Wsl(["test", "-d", mirror], ct)).Success)
            await Must(["git", "init", "--bare", mirror], "create the build cache", ct);

        // safe.directory: the repo is owned by the Windows user, which git inside WSL would otherwise distrust.
        await Must(["git", "-c", "safe.directory=*", "--git-dir", mirror, "fetch", "--force", "--no-tags", source,
            "+refs/heads/*:refs/heads/*", "+HEAD:refs/shipright/head"], "read your project's saved versions", ct);

        if (!(await Wsl(["git", "--git-dir", mirror, "cat-file", "-e", $"{sha}^{{commit}}"], ct)).Success)
            throw new InvalidOperationException(
                $"That commit ({sha[..7]}) was not found in your project. Save your work with a commit and try again.");

        await Must(["git", "--git-dir", mirror, "worktree", "add", "--detach", workDir, sha], "check out the build copy", ct);

        return new BuildSnapshot(workDir, sha, mirror);
    }

    /// <summary>The home folder inside WSL (e.g. /home/sam); fails with a plain message when WSL cannot be reached.</summary>
    public async Task<string> GetWslHomeAsync(CancellationToken ct = default)
    {
        var home = await Wsl(["printenv", "HOME"], ct);
        if (!home.Success || string.IsNullOrWhiteSpace(home.StdOut))
            throw new InvalidOperationException("ShipRight could not reach WSL (Windows Subsystem for Linux). Make sure it is installed and started.");
        return home.StdOut.Trim();
    }

    /// <summary>Best effort: a leftover folder must never fail a build or hide its real result.</summary>
    public async Task RemoveAsync(BuildSnapshot snapshot, CancellationToken ct = default)
    {
        if (!OurWorkDir.IsMatch(snapshot.WorkDir) || !OurMirror.IsMatch(snapshot.MirrorDir)) return;

        try
        {
            var remove = await Wsl(["git", "--git-dir", snapshot.MirrorDir, "worktree", "remove", "--force", snapshot.WorkDir], ct);
            if (remove.Success) return;

            await Wsl(["rm", "-rf", snapshot.WorkDir], ct);
            await Wsl(["git", "--git-dir", snapshot.MirrorDir, "worktree", "prune"], ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // cleanup failure is not worth surfacing
        }
    }

    private Task<ProcessResult> Wsl(string[] args, CancellationToken ct) =>
        _runner.RunAsync("wsl", args, null, ct: ct);

    private async Task Must(string[] args, string doing, CancellationToken ct)
    {
        var r = await Wsl(args, ct);
        if (r.Success) return;
        var detail = (string.IsNullOrWhiteSpace(r.StdErr) ? r.StdOut : r.StdErr).Trim();
        throw new InvalidOperationException($"ShipRight could not {doing}: {detail}");
    }
}
