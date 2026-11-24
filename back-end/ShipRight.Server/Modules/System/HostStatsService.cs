using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace ShipRight.Modules.System;

/// <summary>
/// Cheap host utilisation for the global header. No shell commands and no SSH: the disk comes from
/// DriveInfo, memory from the OS API, and process CPU from our own processor-time delta.
/// </summary>
public sealed class HostStatsService : IHostStatsProvider
{
    private readonly Func<(long Used, long Total)> _disk;
    private readonly Func<(long Used, long Total)> _memory;
    private readonly Func<int> _processCount;
    private readonly Func<TimeSpan> _processCpu;
    private readonly Func<DateTime> _clock;
    private readonly object _gate = new();
    private (TimeSpan Cpu, DateTime At)? _previous;

    public HostStatsService(
        Func<(long Used, long Total)>? disk = null,
        Func<(long Used, long Total)>? memory = null,
        Func<int>? processCount = null,
        Func<TimeSpan>? processCpu = null,
        Func<DateTime>? clock = null)
    {
        _disk = disk ?? ReadDisk;
        _memory = memory ?? ReadMemory;
        _processCount = processCount ?? ReadProcessCount;
        _processCpu = processCpu ?? (() => Process.GetCurrentProcess().TotalProcessorTime);
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    public Task<SystemStatsSnapshot> GetAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var (diskUsed, diskTotal) = _disk();
        var diskPercent = Percent(diskUsed, diskTotal);
        var mount = Path.GetPathRoot(Environment.SystemDirectory) ?? "system";

        var (ramUsed, ramTotal, available, error) = ReadMemorySafely();
        var cpuPercent = ReadCpuPercent();
        var snapshot = new SystemStatsSnapshot(
            mount, diskPercent, diskUsed, diskTotal,
            Percent(ramUsed, ramTotal), ramUsed, ramTotal,
            cpuPercent, _processCount(), available, DateTimeOffset.UtcNow, error);
        return Task.FromResult(snapshot);
    }

    private (long Used, long Total, bool Available, string? Error) ReadMemorySafely()
    {
        try
        {
            var (used, total) = _memory();
            return (used, total, true, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (0, 0, false, $"Memory usage is unavailable on this host ({ex.GetType().Name}).");
        }
    }

    private double ReadCpuPercent()
    {
        var now = _clock();
        var cpu = _processCpu();
        lock (_gate)
        {
            var previous = _previous;
            _previous = (cpu, now);
            if (previous is null) return 0;
            var elapsed = (now - previous.Value.At).TotalSeconds;
            if (elapsed <= 0) return 0;
            return Clamp((cpu - previous.Value.Cpu).TotalSeconds / elapsed / Environment.ProcessorCount * 100);
        }
    }

    private static double Percent(long used, long total) =>
        total <= 0 ? 0 : Clamp((double)used / total * 100);

    private static double Clamp(double value) => Math.Min(100, Math.Max(0, value));

    private static (long Used, long Total) ReadDisk()
    {
        var root = Path.GetPathRoot(Environment.SystemDirectory) ?? string.Empty;
        var drive = new DriveInfo(root);
        if (!drive.IsReady)
            drive = DriveInfo.GetDrives().First(d => d.IsReady && d.DriveType == DriveType.Fixed);
        return (drive.TotalSize - drive.AvailableFreeSpace, drive.TotalSize);
    }

    private static (long Used, long Total) ReadMemory()
    {
        if (OperatingSystem.IsLinux())
        {
            var total = 0L;
            var available = 0L;
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                var parts = line.Split(':', 2);
                if (parts.Length != 2) continue;
                var value = long.TryParse(parts[1].Trim().Split(' ')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var kb)
                    ? kb * 1024 : 0;
                if (parts[0] == "MemTotal") total = value;
                if (parts[0] == "MemAvailable") available = value;
            }
            if (total == 0) throw new PlatformNotSupportedException("/proc/meminfo has no memory totals");
            return (total - available, total);
        }

        if (OperatingSystem.IsWindows())
        {
            var status = new MemoryStatus();
            if (!GlobalMemoryStatusEx(status)) throw new PlatformNotSupportedException("GlobalMemoryStatusEx failed");
            return ((long)(status.ullTotalPhys - status.ullAvailPhys), (long)status.ullTotalPhys);
        }

        throw new PlatformNotSupportedException("Host memory statistics are not supported here");
    }

    private static int ReadProcessCount()
    {
        try { return Process.GetProcesses().Length; }
        catch (Exception ex) when (ex is not OperationCanceledException) { return 0; }
    }

    private const int MemoryStatusLength = 64;

    [StructLayout(LayoutKind.Sequential)]
    private sealed class MemoryStatus
    {
        public uint dwLength = MemoryStatusLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(MemoryStatus buffer);
}
