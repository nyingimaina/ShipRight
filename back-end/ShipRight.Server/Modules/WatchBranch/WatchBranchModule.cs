using Jattac.Libs.Tempo;
using Jattac.Libs.Tempo.Scheduling;
using Microsoft.Extensions.Logging;
using Serilog;
using ShipRight.Modules.Builds;
using ShipRight.Modules.Projects;
using ShipRight.Shared.ProcessRunner;
using ShipRight.Shared.Store;

namespace ShipRight.Modules.WatchBranch;

public static class WatchBranchModule
{
    public static void AddWatchBranchModule(this IServiceCollection services)
    {
        services.AddSingleton<WatchBranchHistoryStore>(sp =>
            new WatchBranchHistoryStore(DataDirectory.Resolve()));

        services.AddSingleton<TempoQueue<TempoScheduledWork<WatchBranchJob>>>(sp =>
        {
            var projectStore      = sp.GetRequiredService<IProjectStore>();
            var buildOrchestrator = sp.GetRequiredService<BuildOrchestrator>();
            var processRunner     = sp.GetRequiredService<IProcessRunner>();
            var historyStore      = sp.GetRequiredService<WatchBranchHistoryStore>();
            var loggerFactory     = sp.GetService<ILoggerFactory>();

            var processor = new WatchBranchJobProcessor(projectStore, buildOrchestrator, processRunner);

            var settings = new TempoQueueSettings
            {
                MessagesPerSecond          = 1,
                BurstCapacity              = 3,
                ChannelCapacity            = 200,
                MaxPendingItemsPerTenant   = 3,
                ResumeThresholdFactor      = 0.5,
                PriorityLaneCount          = 1,
                DepthReportInterval        = TimeSpan.FromSeconds(60),
                ObservabilityReportInterval = TimeSpan.FromSeconds(60),
            };

            var queue = new TempoQueue<TempoScheduledWork<WatchBranchJob>>(
                processor, settings, loggerFactory);

            queue.OnProcessed += (correlationId, work) =>
            {
                historyStore.Append(new WatchBranchHistoryRecord
                {
                    ProjectId     = work.Job.ProjectId,
                    ProjectName   = work.Job.ProjectName,
                    BranchName    = work.Job.BranchName,
                    Status        = "triggered",
                    TriggeredAt   = DateTime.UtcNow,
                    ScheduleId    = work.ScheduleId,
                    CorrelationId = correlationId,
                });
                return Task.CompletedTask;
            };

            queue.OnFailed += (correlationId, work, error) =>
            {
                historyStore.Append(new WatchBranchHistoryRecord
                {
                    ProjectId     = work.Job.ProjectId,
                    ProjectName   = work.Job.ProjectName,
                    BranchName    = work.Job.BranchName,
                    Status        = "failed",
                    TriggeredAt   = DateTime.UtcNow,
                    ErrorMessage  = error,
                    ScheduleId    = work.ScheduleId,
                    CorrelationId = correlationId,
                });
                return Task.CompletedTask;
            };

            queue.OnDepthReport += snapshot =>
            {
                Log.Information("WatchBranch queue depth: pending={Pending}", snapshot.PendingCount);
                return Task.CompletedTask;
            };

            return queue;
        });

        services.AddSingleton<TempoScheduler<WatchBranchJob>>(sp =>
        {
            var queue        = sp.GetRequiredService<TempoQueue<TempoScheduledWork<WatchBranchJob>>>();
            var projectStore = sp.GetRequiredService<IProjectStore>();

            var schedulerSettings = new TempoSchedulerSettings
            {
                TickInterval    = TimeSpan.FromSeconds(10),
                MaxCatchUpSlots = 10,
            };

            var scheduler = new TempoScheduler<WatchBranchJob>(queue, schedulerSettings);

            RegisterProjectJobs(scheduler, projectStore).GetAwaiter().GetResult();

            return scheduler;
        });

        services.AddHostedService<WatchBranchHostedService>();
    }

    private static async Task RegisterProjectJobs(
        TempoScheduler<WatchBranchJob> scheduler,
        IProjectStore projectStore)
    {
        var projects = await projectStore.GetAllAsync();
        var registered = 0;

        foreach (var project in projects)
        {
            if (string.IsNullOrWhiteSpace(project.WatchBranch)) continue;
            if (project.GitRepos.Count == 0) continue;

            var repoPath = project.GitRepos[0].RepoPath;
            var interval = TimeSpan.FromSeconds(Math.Max(60, project.WatchPollSeconds));

            try
            {
                scheduler.Register(
                    new WatchBranchJob
                    {
                        TenantId    = WatchBranchJob.TenantIdFromProject(project.Id),
                        ProjectId   = project.Id,
                        ProjectName = project.Name,
                        RepoPath    = repoPath,
                        BranchName  = project.WatchBranch,
                        WatchSteps  = project.WatchSteps,
                    },
                    new TempoSchedule.Every(interval),
                    MissedRunPolicy.Skip,
                    OverlapPolicy.Skip);

                registered++;
                Log.Information("WatchBranch: registered watch for {Project}/{Branch} every {Interval}s",
                    project.Name, project.WatchBranch, interval.TotalSeconds);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "WatchBranch: failed to register schedule for project {Project}", project.Name);
            }
        }

        Log.Information("WatchBranch: {Count} watch schedules registered at startup", registered);
    }
}

public class WatchBranchHostedService : IHostedService
{
    private readonly TempoScheduler<WatchBranchJob> _scheduler;

    public WatchBranchHostedService(TempoScheduler<WatchBranchJob> scheduler)
    {
        _scheduler = scheduler;
    }

    public Task StartAsync(CancellationToken ct) => _scheduler.StartAsync(ct);
    public Task StopAsync(CancellationToken ct)  => _scheduler.StopAsync(ct);
}
