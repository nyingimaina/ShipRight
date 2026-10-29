using ShipRight.Shared.ProcessRunner;

namespace ShipRight.Modules.System.WslDisk;

/// <summary>
/// Shrinks the WSL virtual disk. It first lets <see cref="WslDiskService"/> do it with Optimize-VHD; on Windows Home
/// that command does not exist (no Hyper-V), so the same job is done with diskpart, which every Windows edition has.
/// diskpart needs administrator rights, so it is started through the normal Windows permission prompt rather than
/// requiring ShipRight itself to run as administrator.
/// When the Store folder holds no disk at all (a distro that was moved, imported to another drive, or installed
/// without a Store package) the disk is looked up where WSL itself recorded it. That lookup only ever runs after the
/// original one found nothing, so a machine that works today behaves exactly as before.
/// </summary>
public class WslDiskShrinker
{
    private const string NoDisk = "No WSL virtual disk found";

    private readonly WslDiskService _inner;
    private readonly IProcessRunner _runner;
    private readonly WslVhdxLocator? _locator;

    public WslDiskShrinker(WslDiskService inner, IProcessRunner runner, WslVhdxLocator? locator = null)
    {
        _inner = inner;
        _runner = runner;
        _locator = locator;
    }

    public async Task<WslCompactResult> ShrinkAsync(CancellationToken ct, Action<string> log)
    {
        var first = await _inner.CompactAsync(ct, log);
        if (first.Succeeded) return first;

        if (first.BlockedReason == NoDisk && _locator is not null)
            return await ShrinkLocatedAsync(first, ct, log);

        if (first.BlockedReason != "Optimize-VHD unavailable") return first;

        // CompactAsync has already stopped WSL, which is what releases the disk file.
        var messages = first.Messages.Where(m => !m.StartsWith("Optimize-VHD is not available")).ToList();
        messages.Add("Optimize-VHD is not available on this edition of Windows - using diskpart instead.");

        var disks = (await _inner.GetReportAsync(ct)).VhdxFiles.Select(f => f.Path).ToList();
        if (disks.Count == 0)
            return new WslCompactResult(false, NoDisk, [], messages);

        return await ShrinkWithDiskpartAsync(disks, messages, ct, log);
    }

    /// <summary>The Store folder had no disk (and WSL has already been stopped): look where WSL recorded it.</summary>
    private async Task<WslCompactResult> ShrinkLocatedAsync(WslCompactResult first, CancellationToken ct, Action<string> log)
    {
        var located = _locator!.Find();
        var messages = first.Messages.Where(m => !m.StartsWith("No ext4.vhdx found")).ToList();

        if (located.Count == 0)
        {
            messages.Add("ShipRight could not find the WSL disk file. It looked in: " + string.Join("; ", _locator.SearchedPlaces()) + ".");
            messages.Add("Nothing was changed on your disk. WSL starts again by itself the next time it is used.");
            return new WslCompactResult(false, NoDisk, [], messages);
        }

        messages.Add($"Found {(located.Count == 1 ? "the WSL disk" : $"{located.Count} WSL disks")} where WSL recorded {(located.Count == 1 ? "it" : "them")} - using diskpart.");
        return await ShrinkWithDiskpartAsync(located, messages, ct, log);
    }

    private async Task<WslCompactResult> ShrinkWithDiskpartAsync(
        IReadOnlyList<string> disks, List<string> messages, CancellationToken ct, Action<string> log)
    {
        var results = new List<WslCompactFileResult>();
        foreach (var disk in disks)
        {
            log($"Shrinking {disk} - Windows will ask for permission to continue…");
            var before = Size(disk);
            var script = Path.Combine(Path.GetTempPath(), $"shipright-compact-{Guid.NewGuid():N}.txt");
            try
            {
                await File.WriteAllTextAsync(script,
                    $"select vdisk file=\"{disk}\"\r\nattach vdisk readonly\r\ncompact vdisk\r\ndetach vdisk\r\nexit\r\n", ct);

                var run = await _runner.RunAsync("powershell",
                    ["-NoProfile", "-NonInteractive", "-Command",
                     $"$p = Start-Process diskpart -ArgumentList '/s','\"{script}\"' -Verb RunAs -Wait -PassThru -WindowStyle Hidden; exit $p.ExitCode"],
                    null, null, null, ct, null, TimeSpan.FromMinutes(30));

                var after = Size(disk);
                results.Add(new WslCompactFileResult(disk, before, after));

                if (!run.Success)
                {
                    messages.Add("diskpart did not run. It needs administrator permission - answer Yes when Windows asks, then try again.");
                    return new WslCompactResult(false, "diskpart needs administrator permission", results, messages);
                }
                if (after >= before)
                {
                    messages.Add($"{Path.GetFileName(disk)} did not get smaller - it may already be as small as it can be.");
                    return new WslCompactResult(false, "Nothing was reclaimed", results, messages);
                }
                messages.Add($"Shrank {Path.GetFileName(disk)}: {Format(before)} → {Format(after)}");
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
