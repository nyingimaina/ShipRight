using Jattac.Libs.Tempo;
using Jattac.Libs.Tempo.Scheduling;
using Serilog;
using ShipRight.Modules.Database;
using ShipRight.Modules.Projects;

namespace ShipRight.Modules.Scheduler;

public class BackupJobProcessor : ITempoProcessor<TempoScheduledWork<BackupJob>>
{
    private readonly IProjectStore _projectStore;
    private readonly DatabaseOrchestrator _orchestrator;
    private readonly DatabaseBackupService? _backups;

    public BackupJobProcessor(
        IProjectStore projectStore,
        DatabaseOrchestrator orchestrator,
        DatabaseBackupService? backups = null)
    {
        _projectStore = projectStore;
        _orchestrator = orchestrator;
        _backups = backups;
    }

    public async Task<bool> ProcessAsync(TempoScheduledWork<BackupJob> work, CancellationToken ct)
    {
        var job = work.Job;
        var project = await _projectStore.GetByIdAsync(job.ProjectId);

        if (project is null)
        {
            Log.Error("Scheduled backup: project {ProjectId} not found", job.ProjectId);
            return false;
        }

        if (project.Database is null || string.IsNullOrWhiteSpace(project.Database.DatabaseName))
        {
            Log.Error("Scheduled backup: project {ProjectId} has no database config", job.ProjectId);
            return false;
        }

        if (work.MaxDuration.HasValue)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(work.MaxDuration.Value);
            return await RunBackupAsync(project, cts.Token);
        }

        return await RunBackupAsync(project, ct);
    }

    private Task<bool> RunBackupAsync(ProjectConfig project, CancellationToken ct) =>
        _backups is not null
            ? _backups.ScheduledBackupAsync(project, ct)
            : _orchestrator.ScheduledBackupAsync(project, ct);
}
