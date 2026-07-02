using System.Globalization;
using Serilog;
using ShipRight.Shared.SshRunner;

namespace ShipRight.Modules.RemoteHost;

public class LinuxSshMonitoringProvider : IMonitoringProvider
{
    private readonly ISshRunner _ssh;

    // One SSH call gathers everything. Two CPU samples with sleep 0.2s between them.
    private const string MetricsCommand =
        "echo '===CPU1==='; awk '/^cpu / {print $2,$3,$4,$5}' /proc/stat; " +
        "sleep 0.2; " +
        "echo '===CPU2==='; awk '/^cpu / {print $2,$3,$4,$5}' /proc/stat; " +
        "echo '===MEM==='; awk '/MemTotal:|MemAvailable:|SwapTotal:|SwapFree:/ {print $1,$2}' /proc/meminfo; " +
        "echo '===DISK==='; df -BM --output=target,size,used 2>/dev/null | " +
            "awk 'NR>1 && !/tmpfs|udev|shm/ {sub(/M/,\"\",$2); sub(/M/,\"\",$3); print $1,$2,$3}'; " +
        "echo '===LOAD==='; cut -d' ' -f1-3 /proc/loadavg; " +
        "echo '===UPTIME==='; awk '{print int($1)}' /proc/uptime; " +
        "echo '===DOCKERPS==='; docker ps -a --format '{{.Names}}|{{.Status}}|{{.Image}}' 2>/dev/null || echo unavailable; " +
        "echo '===DOCKERSTATS==='; docker stats --no-stream --format '{{.Name}}|{{.CPUPerc}}|{{.MemUsage}}' 2>/dev/null || echo unavailable";

    public LinuxSshMonitoringProvider(ISshRunner ssh) => _ssh = ssh;

    public async Task<SystemMetrics> GetMetricsAsync(
        RemoteHostConfig config, string keyPath, CancellationToken ct = default)
    {
        var lines = new List<string>();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));

            await _ssh.RunAsync(
                config.Host, config.Username, keyPath,
                MetricsCommand,
                onOutput: line => { lines.Add(line); return Task.CompletedTask; },
                ct: cts.Token);
        }
        catch (OperationCanceledException)
        {
            return SystemMetrics.Unreachable("Metrics request timed out.");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Monitoring: could not connect to {Host}", config.Host);
            return SystemMetrics.Unreachable(ex.Message);
        }

        return ParseOutput(lines);
    }

    private static SystemMetrics ParseOutput(List<string> lines)
    {
        var sections = SplitSections(lines);

        var cpu1 = GetSection(sections, "CPU1").FirstOrDefault() ?? string.Empty;
        var cpu2 = GetSection(sections, "CPU2").FirstOrDefault() ?? string.Empty;
        var (memUsed, memTotal, swapUsed, swapTotal) = ParseMemInfo(GetSection(sections, "MEM"));
        var disks    = ParseDisk(GetSection(sections, "DISK"));
        var (l1, l5, l15) = ParseLoad(GetSection(sections, "LOAD").FirstOrDefault() ?? string.Empty);
        var uptimeSeconds = long.TryParse(GetSection(sections, "UPTIME").FirstOrDefault(), out var u) ? u : 0L;
        var dockerPs    = ParseDockerPs(GetSection(sections, "DOCKERPS"));
        var dockerStats = ParseDockerStats(GetSection(sections, "DOCKERSTATS"));
        var containers  = MergeContainers(dockerPs, dockerStats);

        return new SystemMetrics(
            Reachable:    true,
            CpuPercent:   ParseCpu(cpu1, cpu2),
            MemUsedMb:    memUsed,
            MemTotalMb:   memTotal,
            SwapUsedMb:   swapUsed,
            SwapTotalMb:  swapTotal,
            Load1m:       l1,
            Load5m:       l5,
            Load15m:      l15,
            UptimeSeconds: uptimeSeconds,
            Disks:        disks,
            Containers:   containers);
    }

    private static Dictionary<string, List<string>> SplitSections(List<string> lines)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        string? current = null;

        foreach (var line in lines)
        {
            if (line.StartsWith("===") && line.EndsWith("==="))
            {
                current = line.Trim('=').Trim();
                result[current] = new List<string>();
                continue;
            }
            if (current is not null && !string.IsNullOrWhiteSpace(line))
                result[current].Add(line.TrimEnd());
        }

        return result;
    }

    private static string[] GetSection(Dictionary<string, List<string>> sections, string key) =>
        sections.TryGetValue(key, out var list) ? [.. list] : [];

    // ── Parsers (internal static for testability) ─────────────────────────

    internal static double ParseCpu(string sample1, string sample2)
    {
        static long[] Parse(string s) => s.Trim()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => long.TryParse(p, out var v) ? v : 0L)
            .ToArray();

        var v1 = Parse(sample1);
        var v2 = Parse(sample2);
        if (v1.Length < 4 || v2.Length < 4) return 0;

        // fields: user(0) nice(1) system(2) idle(3)
        var busy1  = v1[0] + v1[1] + v1[2];
        var total1 = v1[0] + v1[1] + v1[2] + v1[3];
        var busy2  = v2[0] + v2[1] + v2[2];
        var total2 = v2[0] + v2[1] + v2[2] + v2[3];

        var deltaBusy  = busy2  - busy1;
        var deltaTotal = total2 - total1;
        if (deltaTotal <= 0) return 0;

        return Math.Round(100.0 * deltaBusy / deltaTotal, 1);
    }

    internal static (long UsedMb, long TotalMb, long SwapUsedMb, long SwapTotalMb) ParseMemInfo(string[] lines)
    {
        long memTotal = 0, memAvailable = 0, swapTotal = 0, swapFree = 0;

        foreach (var line in lines)
        {
            var parts = line.Split(':', 2, StringSplitOptions.TrimEntries);
            if (parts.Length < 2) continue;
            if (!long.TryParse(parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries)[0], out var val)) continue;

            switch (parts[0])
            {
                case "MemTotal":    memTotal    = val; break;
                case "MemAvailable": memAvailable = val; break;
                case "SwapTotal":   swapTotal   = val; break;
                case "SwapFree":    swapFree    = val; break;
            }
        }

        return (
            UsedMb:     (memTotal - memAvailable) / 1024,
            TotalMb:    memTotal / 1024,
            SwapUsedMb: (swapTotal - swapFree) / 1024,
            SwapTotalMb: swapTotal / 1024);
    }

    internal static IReadOnlyList<DiskMetric> ParseDisk(string[] lines)
    {
        var result = new List<DiskMetric>();
        foreach (var line in lines)
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) continue;
            if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var totalMb)) continue;
            if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var usedMb)) continue;
            result.Add(new DiskMetric(
                Mount:   parts[0],
                UsedGb:  Math.Round(usedMb  / 1024.0, 1),
                TotalGb: Math.Round(totalMb / 1024.0, 1)));
        }
        return result;
    }

    internal static (double Load1m, double Load5m, double Load15m) ParseLoad(string line)
    {
        var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        static double P(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
        return parts.Length < 3 ? (0, 0, 0) : (P(parts[0]), P(parts[1]), P(parts[2]));
    }

    internal static Dictionary<string, (string Status, string Image)> ParseDockerPs(string[] lines)
    {
        var result = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line) ||
                line.Equals("unavailable", StringComparison.OrdinalIgnoreCase)) continue;
            var parts = line.Split('|', 3);
            if (parts.Length < 3) continue;
            var name = parts[0].Trim();
            if (!string.IsNullOrEmpty(name))
                result[name] = (parts[1].Trim(), parts[2].Trim());
        }
        return result;
    }

    internal static Dictionary<string, (double CpuPercent, long MemUsedMb, long MemLimitMb)> ParseDockerStats(string[] lines)
    {
        var result = new Dictionary<string, (double, long, long)>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line) ||
                line.Equals("unavailable", StringComparison.OrdinalIgnoreCase)) continue;
            var parts = line.Split('|', 3);
            if (parts.Length < 3) continue;
            var name = parts[0].Trim();
            if (string.IsNullOrEmpty(name)) continue;

            var cpuStr = parts[1].Trim().TrimEnd('%');
            if (!double.TryParse(cpuStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var cpu)) continue;

            var memSlash = parts[2].Split('/');
            if (memSlash.Length < 2) continue;
            var usedMb  = ParseDockerMemMb(memSlash[0].Trim());
            var limitMb = ParseDockerMemMb(memSlash[1].Trim());

            result[name] = (Math.Round(cpu, 1), usedMb, limitMb);
        }
        return result;
    }

    internal static IReadOnlyList<ContainerMetric> MergeContainers(
        Dictionary<string, (string Status, string Image)> dockerPs,
        Dictionary<string, (double CpuPercent, long MemUsedMb, long MemLimitMb)> dockerStats)
    {
        var result = new List<ContainerMetric>();
        foreach (var (name, ps) in dockerPs)
        {
            var friendly = FriendlyStatus(ps.Status);
            dockerStats.TryGetValue(name, out var s);
            result.Add(new ContainerMetric(name, ps.Image, friendly, s.CpuPercent, s.MemUsedMb, s.MemLimitMb));
        }
        return result;
    }

    private static string FriendlyStatus(string raw)
    {
        if (raw.Contains("(unhealthy)", StringComparison.OrdinalIgnoreCase)) return "Unhealthy";
        if (raw.Contains("(healthy)",   StringComparison.OrdinalIgnoreCase)) return "Healthy";
        if (raw.StartsWith("Up",          StringComparison.OrdinalIgnoreCase)) return "Running";
        if (raw.StartsWith("Exited",      StringComparison.OrdinalIgnoreCase) ||
            raw.StartsWith("Exit",        StringComparison.OrdinalIgnoreCase)) return "Stopped";
        if (raw.StartsWith("Restarting",  StringComparison.OrdinalIgnoreCase)) return "Restarting";
        if (raw.Equals("Created",         StringComparison.OrdinalIgnoreCase)) return "Created";
        return raw.Length > 20 ? raw[..20] + "…" : raw;
    }

    private static long ParseDockerMemMb(string s)
    {
        static bool Ends(string str, string suffix) =>
            str.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
        static double Val(string str, int drop) =>
            double.TryParse(str[..^drop], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

        if (Ends(s, "GiB")) return (long)(Val(s, 3) * 1024);
        if (Ends(s, "GB"))  return (long)(Val(s, 2) * 1000);
        if (Ends(s, "MiB")) return (long) Val(s, 3);
        if (Ends(s, "MB"))  return (long) Val(s, 2);
        if (Ends(s, "KiB")) return (long)(Val(s, 3) / 1024);
        if (Ends(s, "kB"))  return (long)(Val(s, 2) / 1000);
        return 0;
    }
}
