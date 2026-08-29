using Jattac.Libs.Tempo;
using Jattac.Libs.Tempo.Scheduling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using ShipRight.Modules.Database;
using ShipRight.Modules.Projects;
using ShipRight.Shared.Store;

namespace ShipRight.Modules.Scheduler;

public static class CloudSchedulerRegistrar
{
    public static void Register(IServiceCollection services)
    {
        services.AddSingleton<BackupOverflowStore>(sp =>
            new BackupOverflowStore(DataDirectory.Resolve(), sp.GetRequiredService<TempoScheduler<BackupJob>>()));

        services.AddSingleton<TempoQueue<TempoScheduledWork<BackupJob>>>(sp =>
        {
            var processor = new BackupJobProcessor(
                sp.GetRequiredService<IProjectStore>(),
                sp.GetRequiredService<DatabaseOrchestrator>());
            var queue = new TempoQueue<TempoScheduledWork<BackupJob>>(processor,
                new TempoQueueSettings
                {
                    MessagesPerSecond = 3, BurstCapacity = 6, ChannelCapacity = 500,
                    MaxPendingItemsPerTenant = 5, ResumeThresholdFactor = 0.5,
                    PriorityLaneCount = 2, DepthReportInterval = TimeSpan.FromSeconds(30),
                    ObservabilityReportInterval = TimeSpan.FromSeconds(30),
                }, sp.GetService<ILoggerFactory>());
            var history = sp.GetRequiredService<MariaDbBackupHistoryStore>();

            queue.OnProcessed += async (correlationId, work) =>
            {
                var job = work.Job;
                await history.Append(new BackupHistoryRecord
                {
                    ProjectId = job.ProjectId, ProjectName = job.ProjectName,
                    DatabaseName = job.DatabaseName, Status = "completed",
                    CompletedAt = DateTime.UtcNow, DurationMs = 0,
                    CorrelationId = correlationId, ScheduleId = work.ScheduleId,
                });
            };

            queue.OnFailed += async (correlationId, work, error) =>
            {
                var job = work.Job;
                await history.Append(new BackupHistoryRecord
                {
                    ProjectId = job.ProjectId, ProjectName = job.ProjectName,
                    DatabaseName = job.DatabaseName, Status = "failed",
                    CompletedAt = DateTime.UtcNow, ErrorMessage = error,
                    CorrelationId = correlationId, ScheduleId = work.ScheduleId,
                });
                var overflow = sp.GetRequiredService<BackupOverflowStore>();
                overflow.Save(job, error);
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
            var timeZone = TempoTimeZoneResolver.ResolveConfigured();
            var scheduler = new TempoScheduler<BackupJob>(
                sp.GetRequiredService<TempoQueue<TempoScheduledWork<BackupJob>>>(),
                new TempoSchedulerSettings
                {
                    TickInterval = TimeSpan.FromSeconds(5),
                    MaxCatchUpSlots = 50,
                    DefaultTimeZone = timeZone,
                });
            var projects = sp.GetRequiredService<IProjectStore>().GetAllAsync().GetAwaiter().GetResult();
            var count = 0;
            foreach (var p in projects)
            {
                if (p.Database is null || string.IsNullOrWhiteSpace(p.Database.DatabaseName)) continue;
                try
                {
                    scheduler.Register(new BackupJob
                    {
                        TenantId = BackupJob.TenantIdFromProject(p.Id), ProjectId = p.Id,
                        ProjectName = p.Name, DatabaseName = p.Database.DatabaseName,
                    }, new TempoSchedule.Cron("0 2 * * *",
                        TempoTimeZoneResolver.Resolve(p.TimeZone)),
                    MissedRunPolicy.RunOnce, OverlapPolicy.Skip);
                    count++;
                }
                catch (Exception ex) { Log.Error(ex, "Failed to register backup schedule for {Project}", p.Name); }
            }
            Log.Information("Scheduler: {Count} backup schedules registered", count);
            return scheduler;
        });

        services.AddHostedService<SchedulerHostedService>();
    }
}
