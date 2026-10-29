using ShipRight.Shared.ProcessRunner;

namespace ShipRight.Modules.System.WslDisk;

/// <summary>
/// Shrinks the WSL virtual disk. It first lets <see cref="WslDiskService"/> do it with Optimize-VHD; on Windows Home
/// that command does not exist (no Hyper-V), so the same job is done with diskpart, which every Windows edition has.
/// diskpart needs administrator rights, so it is started through the normal Windows permission prompt rather than
/// requiring ShipRight itself to run as administrator.
/// </summary>
public class WslDiskShrinker
{
    private readonly WslDiskService _inner;
    private readonly IProcessRunner _runner;

    public WslDiskShrinker(WslDiskService inner, IProcessRunner runner)
    {
        _inner = inner;
        _runner = runner;
    }

    public async Task<WslCompactResult> ShrinkAsync(CancellationToken ct, Action<string> log)
    {
        var first = await _inner.CompactAsync(ct, log);
        if (first.Succeeded || first.BlockedReason != "Optimize-VHD unavailable") return first;

        // CompactAsync has already stopped WSL, which is what releases the disk file.
        var messages = first.Messages.Where(m => !m.StartsWith("Optimize-VHD is not available")).ToList();
        messages.Add("Optimize-VHD is not available on this edition of Windows - using diskpart instead.");

        var disks = (await _inner.GetReportAsync(ct)).VhdxFiles;
        if (disks.Count == 0)
            return new WslCompactResult(false, "No WSL virtual disk found", [], messages);

        var results = new List<WslCompactFileResult>();
        foreach (var disk in disks)
        {
            log($"Shrinking {disk.Path} - Windows will ask for permission to continue…");
            var before = Size(disk.Path);
            var script = Path.Combine(Path.GetTempPath(), $"shipright-compact-{Guid.NewGuid():N}.txt");
            try
            {
                await File.WriteAllTextAsync(script,
                    $"select vdisk file=\"{disk.Path}\"\r\nattach vdisk readonly\r\ncompact vdisk\r\ndetach vdisk\r\nexit\r\n", ct);

                var run = await _runner.RunAsync("powershell",
                    ["-NoProfile", "-NonInteractive", "-Command",
                     $"$p = Start-Process diskpart -ArgumentList '/s','\"{script}\"' -Verb RunAs -Wait -PassThru -WindowStyle Hidden; exit $p.ExitCode"],
                    null, null, null, ct, null, TimeSpan.FromMinutes(30));

                var after = Size(disk.Path);
                results.Add(new WslCompactFileResult(disk.Path, before, after));

                if (!run.Success)
                {
                    messages.Add("diskpart did not run. It needs administrator permission - answer Yes when Windows asks, then try again.");
                    return new WslCompactResult(false, "diskpart needs administrator permission", results, messages);
                }
                if (after >= before)
                {
                    messages.Add($"{Path.GetFileName(disk.Path)} did not get smaller - it may already be as small as it can be.");
                    return new WslCompactResult(false, "Nothing was reclaimed", results, messages);
                }
                messages.Add($"Shrank {Path.GetFileName(disk.Path)}: {Format(before)} → {Format(after)}");
            }
            finally
            {
                try { File.Delete(script); } catch { /* temp file; harmless if it stays */ }
            }
        }
        return new WslCompactResult(true, null, results, messages);
    }

    private static long Size(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }

    private static string Format(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.#} GB" : $"{bytes / (double)(1L << 20):0.#} MB";
}
