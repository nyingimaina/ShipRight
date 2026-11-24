namespace ShipRight.Modules.System;

/// <summary>A low-cost snapshot for the page header. Percentages only: no host, path or process names.</summary>
public sealed record SystemStatsSnapshot(
    string DiskMount,
    double DiskUsedPercent,
    long DiskUsedBytes,
    long DiskTotalBytes,
    double RamUsedPercent,
    long RamUsedBytes,
    long RamTotalBytes,
    double ProcessCpuPercent,
    int ProcessCount,
    bool Available,
    DateTimeOffset CapturedAtUtc,
    string? Error = null);

public interface IHostStatsProvider
{
    Task<SystemStatsSnapshot> GetAsync(CancellationToken ct = default);
}
