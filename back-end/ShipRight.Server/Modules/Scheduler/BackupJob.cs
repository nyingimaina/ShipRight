using System.Security.Cryptography;
using System.Text;
using Jattac.Libs.Tempo.Scheduling;

namespace ShipRight.Modules.Scheduler;

public class BackupJob : ITempoJob
{
    public Guid TenantId { get; init; }
    public string ProjectId { get; init; } = string.Empty;
    public string ProjectName { get; init; } = string.Empty;
    public string DatabaseName { get; init; } = string.Empty;
    public int Priority { get; init; }
    public string? DeduplicationKey => ProjectId;
    public Guid JobId { get; init; } = Guid.NewGuid();
    public string Name => $"Backup-{ProjectName}";

    public static Guid TenantIdFromProject(string projectId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(projectId));
        return new Guid(bytes[..16]);
    }
}
