using Serilog;
using ShipRight.Modules.Setup;
using ShipRight.Shared.ProcessRunner;

namespace ShipRight.Modules.Builds;

public enum ForceAbortOutcome { NotFound, NotActive, CancelRequested, ForceCleaned }

/// <summary>
/// Builds can be left saying "Paused" or "Running" with nothing behind them - a pipeline that ended without
/// recording it, or ShipRight stopped while a build waited for an answer. Such a build can never be cancelled
/// (there is no pipeline to cancel), yet it blocks anything that must wait for builds to finish and keeps its
/// build copy on disk. This frees it, and sweeps the ones a restart leaves behind.
/// </summary>
public static class StaleBuilds
{
    /// <summary>
    /// A build that really is running is cancelled the normal way and left alone. One with no pipeline behind it is
    /// marked Aborted and its leftover files are cleaned (best effort - a cleanup problem never keeps it stuck).
    /// </summary>
    public static async Task<ForceAbortOutcome> ForceAbortAsync(
        string buildId, IBuildStore store, Func<string, bool> tryCancel, Func<BuildRecord, Task>? cleanup)
    {
        var record = await store.GetByIdAsync(buildId);
        if (record is null) return ForceAbortOutcome.NotFound;
        if (record.Status is not (BuildStatus.Running or BuildStatus.Paused)) return ForceAbortOutcome.NotActive;

        if (tryCancel(buildId)) return ForceAbortOutcome.CancelRequested;

        record.Status = BuildStatus.Aborted;
        record.CompletedAt = DateTime.UtcNow;
        record.FailedStep ??= record.CurrentStepName;
        record.ErrorSummary = "Cleared by hand: nothing was running behind this build any more.";
        record.AppendLogLine("[ShipRight] Build cleared by hand - no pipeline was running for it, so it was marked as aborted.");
        await store.SaveAsync(record);
        Log.Warning("Build {BuildId} had no running pipeline and was force-aborted", buildId);

        if (cleanup is not null)
        {
            try { await cleanup(record); }
            catch (Exception ex) { Log.Warning(ex, "Leftover files of build {BuildId} could not be cleaned", buildId); }
        }
        return ForceAbortOutcome.ForceCleaned;
    }

    /// <summary>
    /// Nothing can be running when ShipRight has only just started, so any build still Paused or Pending is a ghost.
    /// (The earlier start-up clean-up only covered Running and Deploying builds.)
    /// </summary>
    public static async Task<int> SweepAtStartupAsync(IBuildStore store)
    {
        var swept = 0;
        foreach (var status in new[] { BuildStatus.Paused, BuildStatus.Pending })
        {
            foreach (var record in await store.QueryAsync(null, status.ToString(), null, null, null, 1, 1000))
            {
                try
                {
                    record.Status = BuildStatus.Interrupted;
                    record.CompletedAt ??= DateTime.UtcNow;
                    record.AppendLogLine("[ShipRight] Build marked as interrupted: ShipRight was restarted while it was " + status.ToString().ToLowerInvariant() + ".");
                    await store.SaveAsync(record);
                    swept++;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to mark build {BuildId} as interrupted", record.Id);
                }
            }
        }
        if (swept > 0) Log.Warning("{Count} build(s) left paused or pending by a restart were marked as interrupted", swept);
        return swept;
    }

    /// <summary>Removes the clean copy of the code a build checked out in WSL, if it left one behind.</summary>
    public static Func<BuildRecord, Task> SnapshotCleanup(IProcessRunner runner) => async record =>
    {
        var snapshots = new BuildSnapshotService(runner);
        var home = await snapshots.GetWslHomeAsync();
        var root = home + "/.shipright";
        await snapshots.RemoveAsync(new BuildSnapshot(
            $"{root}/build/{record.ProjectId}/{record.Id}", string.Empty, $"{root}/cache/{record.ProjectId}.git"));
    };
}

public static class BuildForceAbortRoutes
{
    public static void MapBuildForceAbortRoutes(this WebApplication app)
    {
        app.MapPost("/api/builds/{id}/force-abort", async (string id, IBuildStore store, BuildOrchestrator orchestrator, IProcessRunner runner) =>
        {
            var outcome = await StaleBuilds.ForceAbortAsync(id, store, orchestrator.CancelBuild, StaleBuilds.SnapshotCleanup(runner));
            return outcome switch
            {
                ForceAbortOutcome.NotFound => Results.NotFound(new { isError = true, message = $"Build '{id}' not found." }),
                ForceAbortOutcome.NotActive => Results.BadRequest(new { isError = true, message = "That build is not running or paused." }),
                ForceAbortOutcome.CancelRequested => Results.Ok(new { forced = false, message = "The build was running, so it is being cancelled normally." }),
                _ => Results.Ok(new { forced = true, message = "The stuck build was cleared." }),
            };
        });
    }
}
