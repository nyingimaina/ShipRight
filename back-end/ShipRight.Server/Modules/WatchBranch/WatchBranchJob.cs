using System.Security.Cryptography;
using System.Text;
using Jattac.Libs.Tempo.Scheduling;

namespace ShipRight.Modules.WatchBranch;

public class WatchBranchJob : ITempoJob
{
    public Guid TenantId { get; init; }
    public string ProjectId { get; init; } = string.Empty;
    public string ProjectName { get; init; } = string.Empty;
    public string RepoPath { get; init; } = string.Empty;
    public string BranchName { get; init; } = string.Empty;
    public string WatchSteps { get; init; } = "Build";
    public int Priority { get; init; }
    public string? DeduplicationKey => $"{ProjectId}:{BranchName}";
    public Guid JobId { get; init; } = Guid.NewGuid();
    public string Name => $"WatchBranch-{ProjectName}/{BranchName}";

    public static Guid TenantIdFromProject(string projectId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"watch:{projectId}"));
        return new Guid(bytes[..16]);
    }
}
