using Jattac.Libs.Tempo;
using Jattac.Libs.Tempo.Scheduling;
using Serilog;
using ShipRight.Modules.Projects;

namespace ShipRight.Modules.WatchBranch;

public static class WatchBranchRouter
{
    public static void MapWatchBranchRoutes(this WebApplication app)
    {
        app.MapGet("/api/watch-branch/inflight", (HttpContext context) =>
        {
            var queue = context.RequestServices
                .GetRequiredService<TempoQueue<TempoScheduledWork<WatchBranchJob>>>();
            var inflight = queue.Inflight;
            if (inflight is null) return Results.Ok(new { inflight = (object?)null });

            return Results.Ok(new
            {
                inflight = new
                {
                    projectId   = inflight.Work.Job.ProjectId,
                    projectName = inflight.Work.Job.ProjectName,
                    branchName  = inflight.Work.Job.BranchName,
                    startedAt   = inflight.StartedAt,
                    elapsedMs   = (long)inflight.ElapsedProcessingTime.TotalMilliseconds,
                }
            });
        });

        app.MapGet("/api/watch-branch/snapshot", (HttpContext context) =>
        {
            var queue = context.RequestServices
                .GetRequiredService<TempoQueue<TempoScheduledWork<WatchBranchJob>>>();
            var snap = queue.Observability.GetSnapshot();
            return Results.Ok(new
            {
                totalPending       = snap.TotalPending,
                totalProcessed     = snap.TotalProcessedSinceStart,
                totalFailures      = snap.TotalFailuresSinceStart,
                healthStatus       = snap.HealthStatus.ToString(),
                isDispatcherRunning = snap.IsDispatcherRunning,
                lastSuccessAt      = snap.LastSuccessAt,
                lastFailureAt      = snap.LastFailureAt,
                lastFailureError   = snap.LastFailureError,
                capturedAt         = snap.SnapshotTakenAtUtc,
            });
        });

        app.MapGet("/api/watch-branch/schedules", (HttpContext context) =>
        {
            var scheduler = context.RequestServices
                .GetRequiredService<TempoScheduler<WatchBranchJob>>();
            var snapshot = scheduler.GetSnapshot();
            var jobs = snapshot.Jobs.Select(j => new
            {
                scheduleId  = j.ScheduleId,
                jobName     = j.JobName,
                projectId   = ExtractProjectId(j.JobName),
                branchName  = ExtractBranchName(j.JobName),
                interval    = j.Schedule is TempoSchedule.Every e ? (int)e.Interval.TotalSeconds : 300,
                lastFiredAt = j.LastFiredAt,
                nextFireAt  = j.NextFireAt,
                totalFired  = j.TotalFired,
            }).ToList();

            return Results.Ok(new { jobs, count = jobs.Count, capturedAt = snapshot.CapturedAt });
        });

        app.MapGet("/api/watch-branch/history", (
            WatchBranchHistoryStore store,
            string? projectId = null, string? status = null, int limit = 100) =>
        {
            var records = store.Query(projectId, status, limit);
            return Results.Ok(new { records, count = records.Count });
        });

        // Syncs a project's watch schedule live — no restart needed.
        // Called by the frontend after saving project config that includes watch branch settings.
        app.MapPost("/api/watch-branch/sync/{projectId}", async (
            string projectId,
            IProjectStore projectStore,
            TempoScheduler<WatchBranchJob> scheduler) =>
        {
            var project = await projectStore.GetByIdAsync(projectId);
            if (project is null)
                return Results.NotFound(new { isError = true, message = $"Project '{projectId}' not found." });

            // Remove any existing schedule for this project (idempotent — safe to call if not registered)
            WatchBranchModule.UnregisterProject(projectId, scheduler);

            if (string.IsNullOrWhiteSpace(project.WatchBranch) || project.GitRepos.Count == 0)
            {
                Log.Information("WatchBranch: cleared schedule for project {ProjectId} (watch disabled)", projectId);
                return Results.Ok(new { registered = false, message = "Watch schedule removed." });
            }

            var interval = TimeSpan.FromSeconds(Math.Max(60, project.WatchPollSeconds));
            var scheduleId = scheduler.Register(
                new WatchBranchJob
                {
                    TenantId    = WatchBranchJob.TenantIdFromProject(projectId),
                    ProjectId   = projectId,
                    ProjectName = project.Name,
                    RepoPath    = project.GitRepos[0].RepoPath,
                    BranchName  = project.WatchBranch,
                    WatchSteps  = project.WatchSteps,
                },
                new TempoSchedule.Every(interval),
                MissedRunPolicy.Skip,
                OverlapPolicy.Skip);

            WatchBranchModule.TrackSchedule(projectId, scheduleId);

            Log.Information("WatchBranch: synced live — watching {Project}/{Branch} every {Interval}s",
                project.Name, project.WatchBranch, interval.TotalSeconds);

            return Results.Ok(new
            {
                registered = true,
                scheduleId,
                branchName  = project.WatchBranch,
                intervalSeconds = (int)interval.TotalSeconds,
                message = $"Now watching '{project.WatchBranch}' — first poll in ~{(int)interval.TotalSeconds}s.",
            });
        });
    }

    private static string ExtractProjectId(string jobName)
    {
        // Job name format: "WatchBranch-{ProjectName}/{BranchName}"
        var after = jobName.StartsWith("WatchBranch-") ? jobName[12..] : jobName;
        var slash = after.LastIndexOf('/');
        return slash >= 0 ? after[..slash] : after;
    }

    private static string ExtractBranchName(string jobName)
    {
        var slash = jobName.LastIndexOf('/');
        return slash >= 0 ? jobName[(slash + 1)..] : string.Empty;
    }
}
