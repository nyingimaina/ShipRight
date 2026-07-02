namespace ShipRight.Modules.RemoteHost;

public record DiskMetric(string Mount, double UsedGb, double TotalGb);

public record ContainerMetric(
    string Name,
    string Image,
    string Status,
    double CpuPercent,
    long MemUsedMb,
    long MemLimitMb);

public record SystemMetrics(
    bool Reachable,
    double CpuPercent,
    long MemUsedMb,
    long MemTotalMb,
    long SwapUsedMb,
    long SwapTotalMb,
    double Load1m,
    double Load5m,
    double Load15m,
    long UptimeSeconds,
    IReadOnlyList<DiskMetric> Disks,
    IReadOnlyList<ContainerMetric> Containers,
    string? Error = null)
{
    public static SystemMetrics Unreachable(string error) =>
        new(false, 0, 0, 0, 0, 0, 0, 0, 0, 0, [], [], error);
}

public interface IMonitoringProvider
{
    Task<SystemMetrics> GetMetricsAsync(RemoteHostConfig host, string keyPath, CancellationToken ct = default);
}
