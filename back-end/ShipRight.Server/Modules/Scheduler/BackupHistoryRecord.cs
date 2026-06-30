namespace ShipRight.Modules.Scheduler;

public record BackupHistoryRecord
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string ProjectId { get; init; } = string.Empty;
    public string ProjectName { get; init; } = string.Empty;
    public string DatabaseName { get; init; } = string.Empty;
    public string Status { get; init; } = "completed";
    public DateTime StartedAt { get; init; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; init; }
    public long DurationMs { get; init; }
    public string? ErrorMessage { get; init; }
    public string? BackupFileName { get; init; }
    public long BackupSizeBytes { get; init; }
    public Guid ScheduleId { get; init; }
    public Guid CorrelationId { get; init; }
}

public record BackupReportSummary
{
    public int TotalRuns { get; init; }
    public int SuccessfulRuns { get; init; }
    public int FailedRuns { get; init; }
    public double SuccessRate { get; init; }
    public double AvgDurationMs { get; init; }
    public long TotalSizeBytes { get; init; }
    public DateTime From { get; init; }
    public DateTime To { get; init; }
}

public record BackupDailyReport
{
    public DateTime Date { get; init; }
    public int TotalRuns { get; init; }
    public int SuccessfulRuns { get; init; }
    public int FailedRuns { get; init; }
    public double AvgDurationMs { get; init; }
    public long TotalSizeBytes { get; init; }
}

public record BackupProjectReport
{
    public string ProjectId { get; init; } = string.Empty;
    public string ProjectName { get; init; } = string.Empty;
    public int TotalRuns { get; init; }
    public int SuccessfulRuns { get; init; }
    public int FailedRuns { get; init; }
    public double SuccessRate { get; init; }
    public DateTime LastBackupAt { get; init; }
}
