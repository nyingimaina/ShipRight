using Jattac.Libs.Tempo.Scheduling;
using Serilog;

namespace ShipRight.Modules.Scheduler;

/// <summary>Re-registers the schedules the user saved, and maps a saved schedule to a Tempo registration.</summary>
public static class SchedulerRegistration
{
    public static async Task RegisterPersistedAsync(
        TempoScheduler<BackupJob> scheduler, IBackupScheduleStore store)
    {
        var definitions = await store.GetAllAsync();
        var registered = 0;

        foreach (var definition in definitions)
        {
            try
            {
                var runtimeId = Register(scheduler, definition);
                await store.SaveAsync(definition with { RuntimeScheduleId = runtimeId });
                registered++;
                Log.Information("Restored backup schedule for project {Project}: cron {Cron}",
                    definition.ProjectName, definition.Cron);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Could not restore the backup schedule for project {ProjectId}", definition.ProjectId);
            }
        }

        Log.Information("Scheduler: {Count} saved backup schedule(s) restored", registered);
    }

    public static Guid Register(TempoScheduler<BackupJob> scheduler, BackupScheduleDefinition definition)
    {
        var schedule = new TempoSchedule.Cron(definition.Cron, TempoTimeZoneResolver.Resolve(definition.TimeZone));
        var missed = Enum.TryParse<MissedRunPolicy>(definition.MissedRunPolicy, true, out var m)
            ? m
            : MissedRunPolicy.RunOnce;
        var overlap = Enum.TryParse<OverlapPolicy>(definition.OverlapPolicy, true, out var o)
            ? o
            : OverlapPolicy.Skip;
        TimeSpan? maxDuration = TimeSpan.TryParse(definition.MaxDuration, out var d) ? d : TimeSpan.FromMinutes(30);

        return scheduler.Register(
            new BackupJob
            {
                TenantId = BackupJob.TenantIdFromProject(definition.ProjectId),
                ProjectId = definition.ProjectId,
                ProjectName = definition.ProjectName,
                DatabaseName = definition.DatabaseName,
            },
            schedule,
            missed,
            overlap,
            maxDuration);
    }
}
