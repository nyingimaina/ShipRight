using Serilog;
using ShipRight.Modules.Projects;
using ShipRight.Modules.Servers;
using ShipRight.Shared.Events;

namespace ShipRight.Modules.Database;

/// <summary>
/// Starts database backups with the authoritative server record. A project carries a snapshot of its server,
/// which can drift from the server people actually edited; the linked server store wins whenever it has a value.
/// </summary>
public sealed class DatabaseBackupService
{
    private readonly DatabaseOrchestrator _orchestrator;
    private readonly IServerStore? _servers;
    private readonly BuildEventBus? _bus;

    public DatabaseBackupService(
        DatabaseOrchestrator orchestrator,
        IServerStore? servers = null,
        BuildEventBus? bus = null)
    {
        _orchestrator = orchestrator;
        _servers = servers;
        _bus = bus;
    }

    public async Task BackupAsync(ProjectConfig project, string opId, CancellationToken ct)
    {
        var server = await ResolveServerAsync(project);
        if (string.IsNullOrWhiteSpace(server.Host))
        {
            await FailAsync(opId, "This project's server has no host. Add one in Server settings before backing up.");
            return;
        }

        if (string.IsNullOrWhiteSpace(server.SshKeyPath))
        {
            await FailAsync(opId, "This project's server has no SSH key. Add a key in Server settings before backing up.");
            return;
        }

        await _orchestrator.BackupAsync(project with { Server = server }, opId, ct);
    }

    public async Task<bool> ScheduledBackupAsync(ProjectConfig project, CancellationToken ct)
    {
        var server = await ResolveServerAsync(project);
        if (string.IsNullOrWhiteSpace(server.Host) || string.IsNullOrWhiteSpace(server.SshKeyPath))
        {
            Log.Warning("Scheduled backup skipped for project {ProjectId}: server has no host or SSH key", project.Id);
            return false;
        }

        return await _orchestrator.ScheduledBackupAsync(project with { Server = server }, ct);
    }

    private async Task<ServerConfig> ResolveServerAsync(ProjectConfig project)
    {
        if (string.IsNullOrWhiteSpace(project.ServerId) || _servers is null)
            return project.Server;

        var stored = await _servers.GetByIdAsync(project.ServerId);
        if (stored is null)
            return project.Server;

        var local = project.Server;
        return local with
        {
            Id = Prefer(stored.Id, local.Id),
            Name = Prefer(stored.Name, local.Name),
            Host = Prefer(stored.Host, local.Host),
            Username = Prefer(stored.Username, local.Username),
            SshKeyPath = Prefer(stored.SshKeyPath, local.SshKeyPath),
            ManagedSshKey = stored.ManagedSshKey || local.ManagedSshKey,
            ManagedSshKeyAuthorized = stored.ManagedSshKeyAuthorized || local.ManagedSshKeyAuthorized,
            RemoteWorkingDir = Prefer(stored.RemoteWorkingDir, local.RemoteWorkingDir),
        };
    }

    private static string Prefer(string authoritative, string fallback) =>
        string.IsNullOrWhiteSpace(authoritative) ? fallback : authoritative;

    private async Task FailAsync(string opId, string message)
    {
        Log.Warning("Backup not started: {Message}", message);
        if (_bus is null) return;

        await _bus.EmitAsync(opId, "error", new { message });
        _bus.Complete(opId);
    }
}
