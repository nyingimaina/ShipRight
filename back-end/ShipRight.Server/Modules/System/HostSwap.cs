using System.Globalization;
using ShipRight.Modules.Projects;
using ShipRight.Modules.RemoteHost;
using ShipRight.Shared.SshRunner;

namespace ShipRight.Modules.System;

/// <summary>
/// The host machine's swap is outside the ShipRight container. Creating it therefore goes over SSH to the
/// metal host instead of running swapon in the app's own namespaces.
/// </summary>
public sealed record HostSwapTarget(string Host, string Username, string SshKeyPath, int SizeGb, bool KeyExists = false)
{
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Host) &&
        !string.IsNullOrWhiteSpace(Username) &&
        !string.IsNullOrWhiteSpace(SshKeyPath) &&
        KeyExists;
}

public interface IHostSwapTargetProvider
{
    HostSwapTarget Get();
}

/// <summary>Resolves the metal host from SHIPRIGHT__HOST_SWAP__* variables. No variables means "not configured".</summary>
public sealed class HostSwapTargetProvider : IHostSwapTargetProvider
{
    internal const int DefaultSizeGb = 2;
    internal const int MinSizeGb = 1;
    internal const int MaxSizeGb = 8;

    private readonly Func<string, string?> _getEnv;
    private readonly Func<string, bool> _fileExists;

    public HostSwapTargetProvider(Func<string, string?>? getEnv = null, Func<string, bool>? fileExists = null)
    {
        _getEnv = getEnv ?? Environment.GetEnvironmentVariable;
        _fileExists = fileExists ?? File.Exists;
    }

    public HostSwapTarget Get()
    {
        var host = _getEnv("SHIPRIGHT__HOST_SWAP__HOST")?.Trim() ?? string.Empty;
        var username = _getEnv("SHIPRIGHT__HOST_SWAP__USERNAME")?.Trim() ?? string.Empty;
        var keyPath = _getEnv("SHIPRIGHT__HOST_SWAP__SSH_KEY_PATH")?.Trim() ?? string.Empty;
        var size = int.TryParse(_getEnv("SHIPRIGHT__HOST_SWAP__SIZE_GB"), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var requested)
            ? Math.Clamp(requested, MinSizeGb, MaxSizeGb)
            : DefaultSizeGb;
        return new HostSwapTarget(host, username, keyPath, size,
            keyPath.Length > 0 && _fileExists(keyPath));
    }
}

public sealed record HostSwapSnapshot(
    long SwapUsedBytes,
    long SwapTotalBytes,
    double SwapUsedPercent,
    bool Available);

public interface IHostSwapStatsProvider
{
    Task<HostSwapSnapshot> GetAsync(CancellationToken ct = default);
}

/// <summary>
/// Reads the swap counters the container can see. On Linux this is the host's /proc/meminfo view, which is
/// exactly the metal swap the header reports; no shell command and no SSH on every poll.
/// </summary>
public sealed class HostSwapStatsProvider : IHostSwapStatsProvider
{
    private readonly Func<(long Used, long Total)> _read;

    public HostSwapStatsProvider(Func<(long Used, long Total)>? read = null) =>
        _read = read ?? ReadSwap;

    public Task<HostSwapSnapshot> GetAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var (used, total) = _read();
            return Task.FromResult(new HostSwapSnapshot(used, total, Percent(used, total), true));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Task.FromResult(new HostSwapSnapshot(0, 0, 0, false));
        }
    }

    private static double Percent(long used, long total) =>
        total <= 0 ? 0 : Math.Min(100, Math.Max(0, (double)used / total * 100));

    private static (long Used, long Total) ReadSwap()
    {
        if (!OperatingSystem.IsLinux()) return (0, 0);

        var total = 0L;
        var free = 0L;
        foreach (var line in File.ReadLines("/proc/meminfo"))
        {
            var parts = line.Split(':', 2);
            if (parts.Length != 2) continue;
            var value = long.TryParse(parts[1].Trim().Split(' ')[0], NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var kb) ? kb * 1024 : 0;
            if (parts[0] == "SwapTotal") total = value;
            if (parts[0] == "SwapFree") free = value;
        }

        return total <= 0 ? (0, 0) : (Math.Max(0, total - free), total);
    }
}

public sealed class HostSwapNotConfiguredException : InvalidOperationException
{
    public HostSwapNotConfiguredException()
        : base("Host swap is not configured. Set the host, user and SSH key for the metal host first.") { }
}

/// <summary>Creates swap on the configured metal host over SSH; never runs swapon inside the app container.</summary>
public sealed class HostSwapService
{
    private readonly IHostSwapTargetProvider _targets;
    private readonly ISshRunner _ssh;

    public HostSwapService(IHostSwapTargetProvider targets, ISshRunner ssh)
    {
        _targets = targets;
        _ssh = ssh;
    }

    public async Task<SwapResult> CreateAsync(int? sizeGb = null, CancellationToken ct = default)
    {
        var target = _targets.Get();
        if (!target.IsConfigured) throw new HostSwapNotConfiguredException();

        var gb = sizeGb ?? target.SizeGb;
        if (gb is < HostSwapTargetProvider.MinSizeGb or > HostSwapTargetProvider.MaxSizeGb)
            throw new ArgumentOutOfRangeException(nameof(sizeGb), "Swap size must be 1–8 GB.");

        var server = new ServerConfig
        {
            Host = target.Host,
            Username = target.Username,
            SshKeyPath = target.SshKeyPath,
        };
        return await new RemoteSwap(_ssh).CreateAsync(server, gb, ct);
    }
}
