using Serilog;
using ShipRight.Modules.Builds;
using ShipRight.Shared.ProcessRunner;

namespace ShipRight.Modules.System.WslDisk;

public record WslVhdxFile(string Path, long SizeBytes);

public record WslDistroInfo(string Name, string State);

public record WslDiskReport(
    List<WslVhdxFile> VhdxFiles,
    List<WslDistroInfo> Distros,
    string? DockerDfSummary,
    string? Error);

public record WslCompactFileResult(string Path, long BeforeBytes, long AfterBytes);

public record WslCompactResult(
    bool Succeeded,
    string? BlockedReason,
    List<WslCompactFileResult> Files,
    List<string> Messages);

/// <summary>
/// Opt-in WSL2 virtual-disk compaction utility.
/// Docker prune frees space inside the WSL ext4.vhdx, but the file only shrinks
/// on the Windows side after <c>wsl --shutdown</c> + <c>Optimize-VHD -Mode Full</c>.
/// Never runs while builds are active — WSL shutdown would kill a live build.
/// </summary>
public class WslDiskService
{
    private readonly IProcessRunner _runner;
    private readonly IBuildStore _buildStore;
    private readonly string[] _vhdxSearchRoots;

    public WslDiskService(
        IProcessRunner runner,
        IBuildStore buildStore,
        string[]? vhdxSearchRoots = null)
    {
        _runner = runner;
        _buildStore = buildStore;
        _vhdxSearchRoots = vhdxSearchRoots
            ?? (OperatingSystem.IsWindows()
                ? [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages")]
                : []);
    }

    public async Task<WslDiskReport> GetReportAsync(CancellationToken ct)
    {
        var distros = await ListDistrosAsync(ct);
        var vhdxFiles = FindVhdxFiles();
        var error = vhdxFiles.Count == 0 && distros.Count == 0
            ? "No WSL distros or virtual disks detected."
            : null;

        string? df = null;
        if (distros.Any(d => string.Equals(d.State, "Running", StringComparison.OrdinalIgnoreCase)))
        {
            var dfResult = await _runner.RunAsync(
                "wsl",
                ["docker", "system", "df"],
                null, null, null, ct, null, TimeSpan.FromSeconds(30));
            if (dfResult.ExitCode == 0)
            {
                df = string.IsNullOrWhiteSpace(dfResult.StdOut) ? dfResult.StdErr.Trim() : dfResult.StdOut.Trim();
            }
        }

        return new WslDiskReport(vhdxFiles, distros, df, error);
    }

    /// <summary>
    /// Compacts every detected WSL2 vhdx. Returns a non-success result carrying a
    /// <see cref="WslCompactResult.BlockedReason"/> when the machine state does not allow it.
    /// </summary>
    public async Task<WslCompactResult> CompactAsync(CancellationToken ct, Action<string> log)
    {
        var messages = new List<string>();

        var blocked = await FindBlockingReasonAsync(ct);
        if (blocked != null)
        {
            return new WslCompactResult(false, blocked, new List<WslCompactFileResult>(), messages);
        }

        var distros = await ListDistrosAsync(ct);
        if (distros.Count == 0)
        {
            messages.Add("No WSL distros found — nothing to compact.");
            return new WslCompactResult(false, "No WSL distros found", new List<WslCompactFileResult>(), messages);
        }

        log("Shutting down WSL to release the virtual disk (wsl --shutdown)…");
        messages.Add("wsl --shutdown");
        var shutdown = await _runner.RunAsync("wsl", ["--shutdown"], null, null, null, ct, null, TimeSpan.FromSeconds(30));
        if (shutdown.ExitCode != 0)
        {
            messages.Add($"wsl --shutdown failed: {shutdown.StdErr.Trim()}");
            return new WslCompactResult(false, "wsl --shutdown failed", new List<WslCompactFileResult>(), messages);
        }
        await Task.Delay(TimeSpan.FromSeconds(4), ct);

        var vhdxFiles = FindVhdxFiles();
        if (vhdxFiles.Count == 0)
        {
            messages.Add("No ext4.vhdx found under the WSL package folders.");
            return new WslCompactResult(false, "No WSL virtual disk found", new List<WslCompactFileResult>(), messages);
        }

        var optimizeAvailable = await _runner.RunAsync(
            "powershell",
            ["-NoProfile", "-NonInteractive", "-Command", "Get-Command Optimize-VHD -ErrorAction SilentlyContinue"],
            null, null, null, ct, null, TimeSpan.FromSeconds(30));
        if (optimizeAvailable.ExitCode != 0)
        {
            messages.Add("Optimize-VHD is not available (Hyper-V PowerShell module). Run ShipRight as Administrator, or enable Hypervisor Platform.");
            return new WslCompactResult(false, "Optimize-VHD unavailable", new List<WslCompactFileResult>(), messages);
        }

        var fileResults = new List<WslCompactFileResult>();
        foreach (var file in vhdxFiles)
        {
            var before = GetSize(file.Path);
            log($"Compacting {file.Path}…");
            var result = await _runner.RunAsync(
                "powershell",
                ["-NoProfile", "-NonInteractive", "-Command", $"Optimize-VHD -Path \"{file.Path}\" -Mode Full"],
                null, null, null, ct, null, TimeSpan.FromMinutes(5));
            var after = GetSize(file.Path);
            fileResults.Add(new WslCompactFileResult(file.Path, before, after));

            if (result.ExitCode != 0)
            {
                messages.Add($"Optimize-VHD failed for {file.Path}: {result.StdErr.Trim()}");
                messages.Add("Run ShipRight as Administrator and try again.");
                return new WslCompactResult(false, "Optimize-VHD failed", fileResults, messages);
            }

            messages.Add($"Compacted {Path.GetFileName(file.Path)}: {FormatBytes(before)} → {FormatBytes(after)}");
        }

        return new WslCompactResult(true, null, fileResults, messages);
    }

    private async Task<string?> FindBlockingReasonAsync(CancellationToken ct)
    {
        foreach (var status in new[] { "Running", "Paused", "Pending" })
        {
            var active = await _buildStore.QueryAsync(null, status, null, null, null, 1, 100);
            if (active.Count > 0)
            {
                return $"A build is currently '{status}' — WSL shutdown would interrupt it. Finish or abort it first.";
            }
        }

        return null;
    }

    private async Task<List<WslDistroInfo>> ListDistrosAsync(CancellationToken ct)
    {
        var result = await _runner.RunAsync("wsl", ["--list", "--verbose"], null, null, null, ct, null, TimeSpan.FromSeconds(15));
        if (result.ExitCode != 0)
        {
            return new List<WslDistroInfo>();
        }

        var distros = new List<WslDistroInfo>();
        foreach (var rawLine in (result.StdOut ?? string.Empty).Split('\n'))
        {
            var line = rawLine.Trim().TrimStart('*').Trim();
            if (line.Length == 0 || line.StartsWith("NAME", StringComparison.OrdinalIgnoreCase) || line.StartsWith("---"))
            {
                continue;
            }

            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                continue;
            }

            distros.Add(new WslDistroInfo(parts[0].Trim(), parts[1]));
        }

        return distros;
    }

    private List<WslVhdxFile> FindVhdxFiles()
    {
        var files = new List<WslVhdxFile>();
        foreach (var root in _vhdxSearchRoots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var pkgDir in Directory.GetDirectories(root))
            {
                var localState = Path.Combine(pkgDir, "LocalState");
                if (!Directory.Exists(localState))
                {
                    continue;
                }

                foreach (var vhdxPath in Directory.GetFiles(localState, "*.vhdx"))
                {
                    files.Add(new WslVhdxFile(vhdxPath, GetSize(vhdxPath)));
                }
            }
        }

        return files;
    }

    private static long GetSize(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch
        {
            return 0;
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 B";
        }

        var units = new[] { "B", "KB", "MB", "GB", "TB" };
        var idx = 0;
        var value = (double)bytes;
        while (value >= 1024 && idx < units.Length - 1)
        {
            value /= 1024;
            idx++;
        }

        return $"{value:0.##} {units[idx]}";
    }
}