namespace ShipRight.Modules.RemoteHost;

public record SystemMetrics(
    double CpuPercent,
    long MemUsedMb,
    long MemTotalMb,
    IReadOnlyList<DiskMetric> Disks);

public record DiskMetric(string Mount, double UsedGb, double TotalGb);

public interface IMonitoringProvider
{
    Task<SystemMetrics> GetMetricsAsync(RemoteHostConfig host, CancellationToken ct = default);
}
