using Jattac.Libs.Tempo.Scheduling;
using ShipRight.Modules.Projects;
using ShipRight.Modules.Scheduler;

namespace ShipRight.Modules.Database;

/// <summary>
/// One call for the shared Backups page: every database project, its schedule, and the size/age of its
/// retained artifacts. The project's own Database tab remains the place where backups are managed.
/// </summary>
public static class BackupCoverageRouter
{
    public static void MapBackupCoverageRoutes(this WebApplication app)
    {
        app.MapGet("/api/backups/coverage", async (
            IProjectStore store, DatabaseOrchestrator orchestrator,
            IBackupScheduleStore schedules, TempoScheduler<BackupJob> scheduler) =>
        {
            var projects = (await store.GetAllAsync())
                .Where(p => p.Database is not null && !string.IsNullOrWhiteSpace(p.Database.DatabaseName))
                .ToList();
            var definitions = await schedules.GetAllAsync();
            var snapshot = scheduler.GetSnapshot();

            var items = projects.Select(project =>
            {
                var artifacts = orchestrator.ListBackups(project.Id);
                var last = artifacts.FirstOrDefault();
                var definition = definitions.FirstOrDefault(d => d.ProjectId == project.Id);
                var runtime = definition?.RuntimeScheduleId is Guid runtimeId
                    ? snapshot.Jobs.FirstOrDefault(j => j.ScheduleId == runtimeId)
                    : null;

                return new
                {
                    projectId = project.Id,
                    projectName = project.Name,
                    databaseName = project.Database!.DatabaseName,
                    backupCount = artifacts.Count,
                    lastBackupAt = last?.CreatedAtUtc,
                    storedBytes = artifacts.Sum(a => a.StoredBytes),
                    originalBytes = artifacts.Sum(a => a.OriginalBytes),
                    keepCount = project.Database.BackupRetainCount,
                    keepDays = project.Database.BackupRetainDays,
                    cron = definition?.Cron,
                    nextFireAt = runtime?.NextFireAt,
                    lastFiredAt = runtime?.LastFiredAt,
                };
            }).ToList();

            return Results.Ok(new { projects = items, capturedAt = DateTimeOffset.UtcNow });
        });
    }
}
