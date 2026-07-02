namespace ShipRight.Modules.WatchBranch;

public record WatchBranchHistoryRecord
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string ProjectId { get; init; } = string.Empty;
    public string ProjectName { get; init; } = string.Empty;
    public string BranchName { get; init; } = string.Empty;
    public string? TriggeredBuildId { get; init; }
    public string Status { get; init; } = "triggered";   // "triggered" | "skipped" | "failed"
    public DateTime TriggeredAt { get; init; } = DateTime.UtcNow;
    public string? ErrorMessage { get; init; }
    public Guid ScheduleId { get; init; }
    public Guid CorrelationId { get; init; }
}
