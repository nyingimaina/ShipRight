namespace ShipRight.Modules.RemoteHost;

public record DiskMetric(string Mount, double UsedGb, double TotalGb);

public record ContainerMetric(
    string Name,
    string Image,
    string Status,
    double CpuPercent,
    long MemUsedMb,
    long MemLimitMb);

public record SslCertMetric(string Domain, DateTime IssuedUtc, DateTime ExpiresUtc);

public record OomEventMetric(string ProcessName, long Pid, long MemoryMb, string? OccurredAt);

public record ZombieProcess(long Pid, string ProcessName, string ParentName);

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
    IReadOnlyList<SslCertMetric> Certs,
    IReadOnlyList<OomEventMetric> OomEvents,
    IReadOnlyList<ZombieProcess> Zombies,
    IReadOnlyList<string> FailedServices,
    string? Error = null)
{
    public static SystemMetrics Unreachable(string error) =>
        new(false, 0, 0, 0, 0, 0, 0, 0, 0, 0, [], [], [], [], [], [], error);
}

public interface IMonitoringProvider
{
    Task<SystemMetrics> GetMetricsAsync(RemoteHostConfig host, string keyPath, CancellationToken ct = default);
}
