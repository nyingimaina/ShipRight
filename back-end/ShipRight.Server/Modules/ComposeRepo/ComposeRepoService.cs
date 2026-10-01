using ShipRight.Modules.Projects;
using ShipRight.Shared.ProcessRunner;

namespace ShipRight.Modules.ComposeRepo;

/// <summary>
/// Health verdict for the WSL compose repo, produced by <c>git fsck</c>.
/// </summary>
public sealed record ComposeRepoHealth(bool IsHealthy, string Summary)
{
    public static ComposeRepoHealth Healthy(string summary) => new(true, summary);
    public static ComposeRepoHealth Unhealthy(string summary) => new(false, summary);
}

/// <summary>
/// Everything the project-detail screen needs to show about the WSL compose repo.
/// </summary>
public sealed record ComposeRepoStatus(
    string WslPath,
    string? WindowsPath,
    bool Exists,
    bool IsGitRepo,
    string? CloneUrl,
    ComposeRepoHealth Health);

/// <summary>
/// Owns the lifecycle of a project's WSL compose repository: where it lives, whether its
/// git object store is intact, and how to re-clone or remove it.
/// <para>
/// The compose repo is NOT the app source repo (<c>ProjectConfig.GitRepos[0]</c>). Clone-URL
/// resolution therefore prefers the compose repo's own <c>origin</c> (persisted on the project
/// as <see cref="WslConfig.ComposeRepoUrl"/>) and only falls back to the app repo for legacy
/// single-repo projects that predate the field.
/// </para>
/// </summary>
public sealed class ComposeRepoService
{
    /// <summary>Time budget for one fsck; a bigger repo is reported inconclusive rather than blocking a build.</summary>
    private static readonly TimeSpan FsckTimeout = TimeSpan.FromMinutes(2);

    /// <summary>Substrings git prints for an unusable object store.</summary>
    private static readonly string[] CorruptionMarkers =
    [
        "is corrupt",
        "is empty",
        "bad object",
        "missing commit",
        "missing tree",
        "unable to read",
        "error: object file",
    ];

    private readonly IProcessRunner _runner;
    private readonly IProjectStore _projectStore;

    public ComposeRepoService(IProcessRunner runner, IProjectStore projectStore)
    {
        _runner = runner;
        _projectStore = projectStore;
    }

    /// <summary>EnvCompose deploys have no compose-repo git round-trip, so none of this applies.</summary>
    public static bool AppliesTo(ProjectConfig project) =>
        project.Server.DeployMode != DeployMode.EnvCompose;

    public async Task<ComposeRepoStatus> GetStatusAsync(ProjectConfig project)
    {
        var wslPath = project.Wsl.WorkingDir ?? string.Empty;
        if (string.IsNullOrWhiteSpace(wslPath))
            return new ComposeRepoStatus(wslPath, null, false, false, null,
                ComposeRepoHealth.Unhealthy("No WSL working directory is configured."));

        var windowsPath = await ResolveWindowsPathAsync(wslPath);
        var isGitRepo = await IsGitRepoAsync(wslPath);
        var exists = isGitRepo || (windowsPath is not null && Directory.Exists(windowsPath));

        var health = isGitRepo
            ? await CheckHealthAsync(wslPath)
            // Absent or not-yet-cloned is a "needs cloning" state, not corruption.
            : ComposeRepoHealth.Healthy(exists
                ? "The WSL directory exists but is not a git repository yet."
                : "The WSL directory does not exist yet.");

        var cloneUrl = await ResolveCloneUrlAsync(project);
        return new ComposeRepoStatus(wslPath, windowsPath, exists, isGitRepo, cloneUrl, health);
    }

    /// <summary>
    /// Runs <c>git fsck</c> on the compose repo. A non-zero exit or a corruption message means
    /// the repo cannot be pulled/committed reliably; an fsck that times out is inconclusive and
    /// is reported healthy so a large repo never blocks a deploy on a slow disk.
    /// </summary>
    public async Task<ComposeRepoHealth> CheckHealthAsync(string wslPath)
    {
        ProcessResult result;
        try
        {
            result = await _runner.RunAsync("git", ["-C", wslPath, "fsck", "--no-progress"], null,
                timeout: FsckTimeout);
        }
        catch (Exception ex)
        {
            // A timeout or a missing/busy git must not fail a deploy on its own.
            return ComposeRepoHealth.Healthy($"git fsck could not complete ({ex.GetType().Name}); skipped.");
        }

        var output = $"{result.StdOut}\n{result.StdErr}";
        var corrupt = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .FirstOrDefault(l => CorruptionMarkers.Any(m =>
                l.Contains(m, StringComparison.OrdinalIgnoreCase)));

        if (corrupt is not null)
            return ComposeRepoHealth.Unhealthy(corrupt);
        if (!result.Success)
            return ComposeRepoHealth.Unhealthy(
                $"git fsck exited with code {result.ExitCode}: {FirstMeaningfulLine(output)}");
        return ComposeRepoHealth.Healthy("git fsck found no problems.");
    }

    /// <summary>
    /// Compose-repo clone URL, in priority order: the URL stored on the project, then the live
    /// repo's own origin (which is then persisted for next time), then — for projects that
    /// predate the stored field — the first app repo's origin.
    /// </summary>
    public async Task<string?> ResolveCloneUrlAsync(ProjectConfig project)
    {
        if (!string.IsNullOrWhiteSpace(project.Wsl.ComposeRepoUrl))
            return project.Wsl.ComposeRepoUrl.Trim();

        var wslPath = project.Wsl.WorkingDir;
        if (!string.IsNullOrWhiteSpace(wslPath) && await IsGitRepoAsync(wslPath))
        {
            var live = await TryGetOriginAsync(wslPath);
            if (live is not null)
            {
                await PersistCloneUrlAsync(project, live);
                return live;
            }
        }

        var appRepo = project.GitRepos.FirstOrDefault();
        if (appRepo is null || string.IsNullOrWhiteSpace(appRepo.RepoPath)) return null;
        return await TryGetOriginAsync(appRepo.RepoPath);
    }

    public async Task PersistCloneUrlAsync(ProjectConfig project, string cloneUrl)
    {
        if (string.IsNullOrWhiteSpace(cloneUrl)) return;
        var url = cloneUrl.Trim();
        if (project.Wsl.ComposeRepoUrl == url) return;
        await _projectStore.SaveAsync(project with { Wsl = project.Wsl with { ComposeRepoUrl = url } });
    }

    /// <summary>
    /// Moves the compose directory aside as <c>&lt;dir&gt;.corrupt-&lt;timestamp&gt;</c> so a
    /// corrupt tree is recoverable rather than destroyed. Returns null when there is nothing to archive.
    /// </summary>
    public async Task<string?> ArchiveAsync(ProjectConfig project, Func<string, Task>? log = null)
    {
        var windowsPath = await ResolveWindowsPathOrThrowAsync(project.Wsl.WorkingDir);
        if (!Directory.Exists(windowsPath))
        {
            if (log is not null) await log($"Nothing to archive: {windowsPath} does not exist.");
            return null;
        }

        var archive = $"{windowsPath}.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
        Directory.Move(windowsPath, archive);
        if (log is not null) await log($"Archived {windowsPath} → {archive} (recoverable — move it back to undo).");
        return archive;
    }

    public async Task CloneAsync(ProjectConfig project, string cloneUrl, Func<string, Task>? log = null)
    {
        var wslPath = project.Wsl.WorkingDir;
        if (log is not null) await log($"git clone {cloneUrl} {wslPath}");
        var clone = await _runner.RunAsync("git", ["clone", cloneUrl, wslPath], null,
            line => log?.Invoke(line) ?? Task.CompletedTask);
        if (!clone.Success)
            throw new InvalidOperationException($"git clone of the compose repo failed:\n{clone.StdErr.Trim()}");
    }

    /// <summary>
    /// Recovers a corrupt compose repo: archives the damaged tree, then clones a clean copy.
    /// Returns the archive path (null when there was nothing to archive).
    /// </summary>
    public async Task<string?> RepairAsync(ProjectConfig project, string cloneUrl, Func<string, Task>? log = null)
    {
        var archive = await ArchiveAsync(project, log);
        await CloneAsync(project, cloneUrl, log);
        return archive;
    }

    private async Task<bool> IsGitRepoAsync(string wslPath)
    {
        if (string.IsNullOrWhiteSpace(wslPath)) return false;
        var probe = await _runner.RunAsync("git", ["-C", wslPath, "rev-parse", "--is-inside-work-tree"], null);
        return probe.Success;
    }

    private async Task<string?> TryGetOriginAsync(string repoPath)
    {
        var r = await _runner.RunAsync("git", ["-C", repoPath, "remote", "get-url", "origin"], null);
        if (!r.Success) return null;
        var url = r.StdOut.Trim();
        return string.IsNullOrWhiteSpace(url) ? null : url;
    }

    /// <summary>
    /// Translates a WSL Linux path to a Windows UNC path so .NET file APIs can reach it.
    /// Returns null when the path cannot be resolved (e.g. WSL unavailable).
    /// </summary>
    public async Task<string?> ResolveWindowsPathAsync(string linuxPath)
    {
        if (string.IsNullOrWhiteSpace(linuxPath)) return null;
        if (!OperatingSystem.IsWindows()) return linuxPath;
        var r = await _runner.RunAsync("wsl", ["wslpath", "-w", linuxPath], null);
        var path = r.StdOut.Trim();
        return r.Success && !string.IsNullOrWhiteSpace(path) ? path : null;
    }

    private async Task<string> ResolveWindowsPathOrThrowAsync(string linuxPath) =>
        await ResolveWindowsPathAsync(linuxPath)
        ?? throw new InvalidOperationException(
            $"Could not resolve the WSL path '{linuxPath}' to a local path (wslpath failed). " +
            "Ensure WSL is available, then retry.");

    private static string FirstMeaningfulLine(string output)
    {
        var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0);
        return line ?? "git fsck reported a corrupt object store.";
    }
}
