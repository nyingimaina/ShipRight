using Jattac.Libs.Tempo;
using Jattac.Libs.Tempo.Scheduling;
using Microsoft.Extensions.Logging;
using Serilog;
using ShipRight.Modules.Projects;
using ShipRight.Shared.Store;
using ILogger = Serilog.ILogger;

namespace ShipRight.Modules.Scheduler;

public static class SchedulerModule
{
    public static void AddSchedulerModule(this IServiceCollection services)
    {
        services.AddSingleton<BackupHistoryStore>(sp =>
        {
            var dataDir = DataDirectory.Resolve();
            return new BackupHistoryStore(dataDir);
        });

        services.AddSingleton<TempoQueue<TempoScheduledWork<BackupJob>>>(sp =>
        {
            var projectStore = sp.GetRequiredService<IProjectStore>();
            var orchestrator = sp.GetRequiredService<Database.DatabaseOrchestrator>();

            var processor = new BackupJobProcessor(projectStore, orchestrator);
            var queueSettings = new TempoQueueSettings
            {
                MessagesPerSecond = 3,
                BurstCapacity = 6,
                ChannelCapacity = 500,
                MaxPendingItemsPerTenant = 5,
                ResumeThresholdFactor = 0.5,
                PriorityLaneCount = 2,
                DepthReportInterval = TimeSpan.FromSeconds(30),
                ObservabilityReportInterval = TimeSpan.FromSeconds(30),
            };

            var loggerFactory = sp.GetService<ILoggerFactory>();
            var queue = new TempoQueue<TempoScheduledWork<BackupJob>>(
                processor, queueSettings, loggerFactory);

            var historyStore = sp.GetRequiredService<BackupHistoryStore>();

            queue.OnProcessed += (correlationId, work) =>
            {
                var job = work.Job;
                historyStore.Append(new BackupHistoryRecord
                {
                    ProjectId = job.ProjectId,
                    ProjectName = job.ProjectName,
                    DatabaseName = job.DatabaseName,
                    Status = "completed",
                    CompletedAt = DateTime.UtcNow,
                    DurationMs = 0,
                    CorrelationId = correlationId,
                    ScheduleId = work.ScheduleId,
                });
                return Task.CompletedTask;
            };

            queue.OnFailed += (correlationId, work, error) =>
            {
                var job = work.Job;
                historyStore.Append(new BackupHistoryRecord
                {
                    ProjectId = job.ProjectId,
                    ProjectName = job.ProjectName,
                    DatabaseName = job.DatabaseName,
                    Status = "failed",
                    CompletedAt = DateTime.UtcNow,
                    ErrorMessage = error,
                    CorrelationId = correlationId,
                    ScheduleId = work.ScheduleId,
                });

                var overflowStore = sp.GetRequiredService<BackupOverflowStore>();
                overflowStore.Save(job, error);
                return Task.CompletedTask;
            };

            queue.OnDepthReport += snapshot =>
            {
                Log.Information("Scheduler depth: tenant={Tenant} lane={Priority} pending={Pending}",
                    snapshot.TenantId, snapshot.Priority, snapshot.PendingCount);
                return Task.CompletedTask;
            };

            return queue;
        });

        services.AddSingleton<TempoScheduler<BackupJob>>(sp =>
        {
            var queue = sp.GetRequiredService<TempoQueue<TempoScheduledWork<BackupJob>>>();
            var projectStore = sp.GetRequiredService<IProjectStore>();
            var historyStore = sp.GetRequiredService<BackupHistoryStore>();

            var schedulerSettings = new TempoSchedulerSettings
            {
                TickInterval = TimeSpan.FromSeconds(5),
                MaxCatchUpSlots = 50,
            };

            var scheduler = new TempoScheduler<BackupJob>(queue, schedulerSettings);

            RegisterProjectJobs(scheduler, projectStore, historyStore).GetAwaiter().GetResult();

            return scheduler;
        });

        services.AddSingleton<BackupOverflowStore>(sp =>
        {
            var dataDir = DataDirectory.Resolve();
            var scheduler = sp.GetRequiredService<TempoScheduler<BackupJob>>();
            return new BackupOverflowStore(dataDir, scheduler);
        });

        services.AddHostedService<SchedulerHostedService>();
    }

    private static async Task RegisterProjectJobs(
        TempoScheduler<BackupJob> scheduler,
        IProjectStore projectStore,
        BackupHistoryStore historyStore)
    {
        var projects = await projectStore.GetAllAsync();
        var registeredCount = 0;

        foreach (var project in projects)
        {
            if (project.Database is null || string.IsNullOrWhiteSpace(project.Database.DatabaseName))
                continue;

            var scheduleExpr = DetectSchedule(project);

            try
            {
                    scheduler.Register(
                        new BackupJob
                        {
                            TenantId = BackupJob.TenantIdFromProject(project.Id),
                            ProjectId = project.Id,
                            ProjectName = project.Name,
                            DatabaseName = project.Database.DatabaseName,
                        },
                        scheduleExpr,
                        MissedRunPolicy.RunOnce,
                        OverlapPolicy.Skip);

                registeredCount++;
                Log.Information("Registered backup schedule for project {Project}: {Schedule}",
                    project.Name, DescribeSchedule(scheduleExpr));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to register backup schedule for project {Project}", project.Name);
            }
        }

        Log.Information("Scheduler: {Count} backup schedules registered", registeredCount);
    }

    private static TempoSchedule DetectSchedule(ProjectConfig project)
    {
        return new TempoSchedule.Cron("0 2 * * *");
    }

    private static string DescribeSchedule(TempoSchedule schedule)
    {
        return schedule switch
        {
            TempoSchedule.Cron c => $"cron \"{c.Expression}\"",
            TempoSchedule.Every e => $"every {e.Interval}",
            TempoSchedule.OnceAt o => $"once at {o.FireAt}",
            _ => "unknown",
        };
    }
}

public class SchedulerHostedService : IHostedService
{
    private readonly TempoScheduler<BackupJob> _scheduler;

    public SchedulerHostedService(TempoScheduler<BackupJob> scheduler)
    {
        _scheduler = scheduler;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        await _scheduler.StartAsync(ct);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        await _scheduler.StopAsync(ct);
    }
}
