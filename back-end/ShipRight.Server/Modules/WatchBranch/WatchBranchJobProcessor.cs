using System.Collections.Concurrent;
using Jattac.Libs.Tempo;
using Jattac.Libs.Tempo.Scheduling;
using Serilog;
using ShipRight.Modules.Builds;
using ShipRight.Modules.Projects;
using ShipRight.Shared.ProcessRunner;

namespace ShipRight.Modules.WatchBranch;

public class WatchBranchJobProcessor : ITempoProcessor<TempoScheduledWork<WatchBranchJob>>
{
    private readonly IProjectStore _projectStore;
    private readonly BuildOrchestrator _buildOrchestrator;
    private readonly IProcessRunner _processRunner;

    // Last known remote SHA per "projectId:branch" key — static so it survives processor re-instantiation.
    private static readonly ConcurrentDictionary<string, string> _lastShas = new();

    public WatchBranchJobProcessor(
        IProjectStore projectStore,
        BuildOrchestrator buildOrchestrator,
        IProcessRunner processRunner)
    {
        _projectStore = projectStore;
        _buildOrchestrator = buildOrchestrator;
        _processRunner = processRunner;
    }

    public async Task<bool> ProcessAsync(TempoScheduledWork<WatchBranchJob> work, CancellationToken ct)
    {
        var job = work.Job;

        var project = await _projectStore.GetByIdAsync(job.ProjectId);
        if (project is null)
        {
            Log.Error("WatchBranch: project {ProjectId} not found", job.ProjectId);
            return false;
        }

        var result = await _processRunner.RunAsync(
            "git",
            ["-C", job.RepoPath, "ls-remote", "origin", $"refs/heads/{job.BranchName}"],
            null, ct: ct);

        if (!result.Success)
        {
            Log.Warning("WatchBranch: git ls-remote failed for {Project}/{Branch}: {Err}",
                job.ProjectName, job.BranchName, result.StdErr.Trim());
            return false;
        }

        var currentSha = ParseSha(result.StdOut);
        if (currentSha is null)
        {
            Log.Warning("WatchBranch: branch {Branch} not found on remote for {Project}",
                job.BranchName, job.ProjectName);
            return true;
        }

        var watchKey = $"{job.ProjectId}:{job.BranchName}";
        _lastShas.TryGetValue(watchKey, out var lastSha);

        _lastShas[watchKey] = currentSha;

        if (!ShouldTriggerBuild(currentSha, lastSha))
        {
            if (lastSha is null)
                Log.Information("WatchBranch: first poll for {Project}/{Branch} — SHA recorded, no build triggered",
                    job.ProjectName, job.BranchName);
            else
                Log.Debug("WatchBranch: no change on {Project}/{Branch}",
                    job.ProjectName, job.BranchName);
            return true;
        }

        var lastShaShort = lastSha is null ? "?" : lastSha[..Math.Min(8, lastSha.Length)];
        Log.Information("WatchBranch: SHA changed for {Project}/{Branch}: {Old} → {New}",
            job.ProjectName, job.BranchName,
            lastShaShort,
            currentSha[..Math.Min(8, currentSha.Length)]);

        try
        {
            await _buildOrchestrator.StartWatchedBuildAsync(job.ProjectId, job.WatchSteps, ct);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "WatchBranch: failed to trigger build for {Project}", job.ProjectName);
            return false;
        }
    }

    internal static string? ParseSha(string gitLsRemoteOutput)
    {
        if (string.IsNullOrWhiteSpace(gitLsRemoteOutput)) return null;
        var line = gitLsRemoteOutput.Trim().Split('\n')[0].Trim();
        if (line.Length < 40) return null;
        return line[..40];
    }

    internal static bool ShouldTriggerBuild(string currentSha, string? lastSha)
    {
        if (lastSha is null) return false;   // First poll — just record, don't trigger
        return currentSha != lastSha;
    }
}
