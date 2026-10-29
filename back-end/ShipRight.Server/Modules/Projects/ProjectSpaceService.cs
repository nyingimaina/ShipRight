using System.Text.RegularExpressions;
using ShipRight.Modules.Builds;
using ShipRight.Modules.Resources;
using ShipRight.Modules.Setup;
using ShipRight.Modules.System.DockerCleanup;
using ShipRight.Shared.ProcessRunner;

namespace ShipRight.Modules.Projects;

public record SpaceImage(string Reference, string Id, string Size, long SizeBytes, string Age, bool InUse, string Origin, string? BuildVersion);

public record SpaceFolder(string BuildId, string Path, long SizeBytes, bool Active, string Origin);

public record SpaceService(string Name, int KeepCount, bool NeverClean);

public record ProjectSpaceReport(
    string ProjectId,
    IReadOnlyList<SpaceImage> Images,
    IReadOnlyList<SpaceFolder> Snapshots,
    long ImagesBytes, long SnapshotsBytes, long CacheBytes,
    long TotalBytes,
    long ReclaimableBytes,                 // unused images + build copies nothing is using
    IReadOnlyList<SpaceService> Services,
    bool DeletesAfterPush,                 // every service removes its image once it is pushed
    string? Error);

public record SpaceCleanItem(string Name, string Kind, bool Removed, string? Note);

public record SpaceCleanResult(IReadOnlyList<SpaceCleanItem> Items, long FreedBytes, ProjectSpaceReport? Report);

/// <summary>
/// How much disk a project is holding from its earlier builds - successful and failed - and a one-click way to give
/// it back. The images are the project's own (matched by image name), the build copies are the clean checkouts kept
/// in WSL, and the cache is the reusable git mirror that makes builds faster (reported, never removed here).
/// Only what nothing is using is ever removed: no force, nothing a container runs, nothing an active build owns.
/// </summary>
public class ProjectSpaceService
{
    private static readonly Regex SafeId = new(@"^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.Compiled);
    private static readonly HashSet<BuildStatus> Succeeded = [BuildStatus.ImageBuilt, BuildStatus.PushSucceeded, BuildStatus.Deployed];
    private static readonly HashSet<BuildStatus> Failed =
        [BuildStatus.BuildFailed, BuildStatus.PushFailed, BuildStatus.DeployFailed, BuildStatus.Aborted, BuildStatus.Interrupted];

    private readonly IProcessRunner _runner;
    private readonly IProjectStore _projects;
    private readonly IBuildStore _builds;

    public ProjectSpaceService(IProcessRunner runner, IProjectStore projects, IBuildStore builds)
    {
        _runner = runner;
        _projects = projects;
        _builds = builds;
    }

    public async Task<ProjectSpaceReport?> ReportAsync(string projectId, CancellationToken ct)
    {
        var project = await _projects.GetByIdAsync(projectId);
        if (project is null) return null;

        var services = project.Services
            .Select(s => new SpaceService(s.Name, s.LocalImageKeepCount ?? BuildMachineImagePruner.DefaultLocalImageKeepCount, s.SkipLocalCleanup == true))
            .ToList();
        var deletesAfterPush = services.Count > 0 && services.All(s => s.KeepCount == 0 && !s.NeverClean);

        ProjectSpaceReport Empty(string? error) =>
            new(projectId, [], [], 0, 0, 0, 0, 0, services, deletesAfterPush, error);

        var history = await _builds.QueryAsync(projectId, null, null, null, null, 1, 500);

        // ── images ──
        var listing = await _runner.RunAsync("docker",
            ["images", "--no-trunc", "--format", "{{.Repository}}\t{{.Tag}}\t{{.ID}}\t{{.Size}}\t{{.CreatedSince}}"],
            null, timeout: TimeSpan.FromSeconds(30), ct: ct);
        if (!listing.Success)
            return Empty("Docker could not be reached, so the space used by images cannot be measured. Is Docker running inside WSL?");

        var inUse = await _runner.RunAsync("docker", ["ps", "-a", "--format", "{{.Image}}"], null, timeout: TimeSpan.FromSeconds(30), ct: ct);
        var inUseRefs = inUse.Success ? Lines(inUse.StdOut).Select(WithTag).ToHashSet(StringComparer.OrdinalIgnoreCase) : [];
        var repos = project.Services.Select(s => s.DockerImageName).Where(n => !string.IsNullOrWhiteSpace(n)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var images = new List<SpaceImage>();
        foreach (var line in Lines(listing.StdOut))
        {
            var f = line.Split('\t');
            if (f.Length < 5 || f[0] == "<none>" || f[1] == "<none>" || !repos.Contains(f[0])) continue;
            var reference = $"{f[0]}:{f[1]}";
            var bareId = f[2].Replace("sha256:", "");
            var used = inUseRefs.Contains(reference) || inUseRefs.Contains(f[2])
                       || inUseRefs.Any(r => r.Length >= 12 && bareId.StartsWith(r, StringComparison.OrdinalIgnoreCase));
            images.Add(new SpaceImage(reference, f[2], f[3], DockerCleanupService.ParseSize(f[3]), f[4], used, OriginOfImage(history, f[0], f[1]), f[1]));
        }
        images = images.OrderByDescending(i => i.SizeBytes).ToList();

        // ── build copies and cache in WSL (only for a safe id: it ends up inside a shell command) ──
        var snapshots = new List<SpaceFolder>();
        long cache = 0;
        if (SafeId.IsMatch(projectId))
        {
            var active = (await ActiveBuildIdsAsync(projectId)).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var copies = await Sh($"du -sk \"$HOME/.shipright/build/{projectId}\"/* 2>/dev/null; true", ct);
            foreach (var row in Lines(copies))
            {
                var tab = row.IndexOf('\t');
                if (tab < 0 || !long.TryParse(row[..tab], out var kb)) continue;
                var path = row[(tab + 1)..].Trim();
                var buildId = path[(path.LastIndexOf('/') + 1)..];
                snapshots.Add(new SpaceFolder(buildId, path, kb * 1024, active.Contains(buildId), OriginOfBuild(history, buildId)));
            }

            var mirror = await Sh($"du -sk \"$HOME/.shipright/cache/{projectId}.git\" 2>/dev/null; true", ct);
            var first = Lines(mirror).FirstOrDefault();
            if (first is not null && first.IndexOf('\t') is var t and > 0 && long.TryParse(first[..t], out var cacheKb)) cache = cacheKb * 1024;
        }

        var imagesBytes = images.Sum(i => i.SizeBytes);
        var snapshotBytes = snapshots.Sum(s => s.SizeBytes);
        var reclaimable = images.Where(i => !i.InUse).Sum(i => i.SizeBytes) + snapshots.Where(s => !s.Active).Sum(s => s.SizeBytes);

        return new ProjectSpaceReport(projectId, images, snapshots, imagesBytes, snapshotBytes, cache,
            imagesBytes + snapshotBytes + cache, reclaimable, services, deletesAfterPush, null);
    }

    public async Task<SpaceCleanResult> CleanAsync(string projectId, CancellationToken ct)
    {
        var before = await ReportAsync(projectId, ct);
        if (before is null || before.Error is not null) return new SpaceCleanResult([], 0, before);

        var items = new List<SpaceCleanItem>();

        foreach (var image in before.Images.Where(i => !i.InUse))
        {
            var r = await _runner.RunAsync("docker", ["rmi", image.Reference], null, timeout: TimeSpan.FromSeconds(60), ct: ct);
            items.Add(new SpaceCleanItem(image.Reference, "Image", r.Success,
                r.Success ? null : Lines(r.StdErr).FirstOrDefault() ?? "Docker would not remove it."));
        }

        var snapshots = new BuildSnapshotService(_runner);
        foreach (var copy in before.Snapshots.Where(s => !s.Active))
        {
            // <root>/build/<project>/<build>  ->  <root>/cache/<project>.git
            var root = copy.Path[..copy.Path.IndexOf("/build/", StringComparison.Ordinal)];
            await snapshots.RemoveAsync(new BuildSnapshot(copy.Path, string.Empty, $"{root}/cache/{projectId}.git"), ct);
            items.Add(new SpaceCleanItem(copy.BuildId, "Build copy", true, null));
        }

        var after = await ReportAsync(projectId, ct);
        var freed = after is null ? 0 : Math.Max(0, before.TotalBytes - after.TotalBytes);
        return new SpaceCleanResult(items, freed, after);
    }

    // ── helpers ───────────────────────────────────────────────────────

    private async Task<IEnumerable<string>> ActiveBuildIdsAsync(string projectId)
    {
        var ids = new List<string>();
        foreach (var status in new[] { BuildStatus.Running, BuildStatus.Paused, BuildStatus.Pending })
            ids.AddRange((await _builds.QueryAsync(projectId, status.ToString(), null, null, null, 1, 100)).Select(b => b.Id));
        return ids;
    }

    private static string OriginOfImage(IEnumerable<BuildRecord> history, string repo, string tag)
    {
        var record = history
            .Where(r => r.Versions.Any(v => v.NewVersion == tag && string.Equals(v.DockerImageName, repo, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(r => r.StartedAt)
            .FirstOrDefault();
        return record is null ? "Unknown" : Describe(record.Status);
    }

    private static string OriginOfBuild(IEnumerable<BuildRecord> history, string buildId)
    {
        var record = history.FirstOrDefault(r => r.Id == buildId);
        return record is null ? "Unknown" : Describe(record.Status);
    }

    private static string Describe(BuildStatus status) =>
        Succeeded.Contains(status) ? "Successful build" : Failed.Contains(status) ? "Failed build" : "Unknown";

    private async Task<string> Sh(string script, CancellationToken ct)
    {
        var r = await _runner.RunAsync("wsl", ["sh", "-c", script], null, timeout: TimeSpan.FromSeconds(60), ct: ct);
        return r.Success ? r.StdOut : string.Empty;
    }

    private static IEnumerable<string> Lines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string WithTag(string image) =>
        image.Contains('@') || (image.Length >= 12 && image.All(Uri.IsHexDigit)) || image[(image.LastIndexOf('/') + 1)..].Contains(':') ? image : image + ":latest";
}
