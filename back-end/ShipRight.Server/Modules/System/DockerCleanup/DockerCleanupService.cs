using System.Globalization;
using ShipRight.Modules.Projects;
using ShipRight.Shared.ProcessRunner;

namespace ShipRight.Modules.System.DockerCleanup;

public record CleanupImage(
    string Reference, string Id, string Size, long SizeBytes, string Age,
    string Owner,               // "ShipRight" (an image one of the projects builds) or "Other"
    bool SelectedByDefault);

public record CleanupPreview(
    IReadOnlyList<CleanupImage> Images,   // images no container uses
    int InUseCount,                       // tagged images a container uses - never offered
    int DanglingCount,                    // untagged leftovers
    long SelectedBytes,                   // total size of what is pre-selected
    string DiskUsage,
    string? Error);

public record CleanupRequest(IReadOnlyCollection<string>? Images, bool PruneDangling, bool PruneBuildCache);

public record CleanupItemResult(string Reference, bool Removed, string? Note);

public record CleanupResult(
    IReadOnlyList<CleanupItemResult> Items, string? DanglingOutput, string? BuildCacheOutput, string Before, string After);

/// <summary>
/// One-click Docker clean-up for people who are short of disk space. It lists the images nothing is using, split into
/// the ones ShipRight's own projects build (pre-selected) and everything else (listed, not selected), and removes
/// exactly what it is told to. It never forces a removal, never touches an image a container uses, and never
/// prunes volumes, because volumes hold data. The list is rebuilt before every run, so a stale page cannot remove
/// something that has become in use in the meantime.
/// </summary>
public class DockerCleanupService
{
    private readonly IProcessRunner _runner;
    private readonly IProjectStore _projects;

    public DockerCleanupService(IProcessRunner runner, IProjectStore projects)
    {
        _runner = runner;
        _projects = projects;
    }

    public async Task<CleanupPreview> PreviewAsync(CancellationToken ct)
    {
        var listing = await ListAsync(ct);
        if (listing.Error is not null)
            return new CleanupPreview([], 0, 0, 0, string.Empty, listing.Error);

        var selected = listing.Unused.Where(i => i.SelectedByDefault).Sum(i => i.SizeBytes);
        return new CleanupPreview(listing.Unused, listing.InUseCount, listing.DanglingCount, selected, await DiskUsageAsync(ct), null);
    }

    public async Task<CleanupResult> RunAsync(CleanupRequest request, CancellationToken ct)
    {
        var before = await DiskUsageAsync(ct);
        var results = new List<CleanupItemResult>();

        var wanted = (request.Images ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (wanted.Count > 0)
        {
            var listing = await ListAsync(ct);
            var removable = listing.Unused.Select(i => i.Reference).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var reference in wanted)
            {
                if (!removable.Contains(reference))
                {
                    results.Add(new(reference, false, "Skipped: it is in use now, or no longer on this machine."));
                    continue;
                }
                var r = await _runner.RunAsync("docker", ["rmi", reference], null, timeout: TimeSpan.FromSeconds(60), ct: ct);
                results.Add(r.Success
                    ? new(reference, true, null)
                    : new(reference, false, FirstLine(r.StdErr) ?? "Docker would not remove it."));
            }
        }

        string? dangling = null, cache = null;
        if (request.PruneDangling)
        {
            var r = await _runner.RunAsync("docker", ["image", "prune", "-f"], null, timeout: TimeSpan.FromMinutes(2), ct: ct);
            dangling = r.Success ? LastLine(r.StdOut) ?? "Done." : $"Failed: {FirstLine(r.StdErr)}";
        }
        if (request.PruneBuildCache)
        {
            var r = await _runner.RunAsync("docker", ["builder", "prune", "-af"], null, timeout: TimeSpan.FromMinutes(5), ct: ct);
            cache = r.Success ? LastLine(r.StdOut) ?? "Done." : $"Failed: {FirstLine(r.StdErr)}";
        }

        return new CleanupResult(results, dangling, cache, before, await DiskUsageAsync(ct));
    }

    private sealed record Listing(List<CleanupImage> Unused, int InUseCount, int DanglingCount, string? Error);

    private async Task<Listing> ListAsync(CancellationToken ct)
    {
        var images = await _runner.RunAsync("docker",
            ["images", "--no-trunc", "--format", "{{.Repository}}\t{{.Tag}}\t{{.ID}}\t{{.Size}}\t{{.CreatedSince}}"],
            null, timeout: TimeSpan.FromSeconds(30), ct: ct);
        if (!images.Success)
            return new([], 0, 0, "Docker could not be reached, so there is nothing to clean up yet. Is Docker running inside WSL?");

        var inUse = await _runner.RunAsync("docker", ["ps", "-a", "--format", "{{.Image}}"], null,
            timeout: TimeSpan.FromSeconds(30), ct: ct);
        var inUseRefs = inUse.Success ? Lines(inUse.StdOut).Select(WithTag).ToHashSet(StringComparer.OrdinalIgnoreCase) : [];

        // Containers may name an image by ID instead of by tag; those images are in use as well.
        var shipRightRepos = (await _projects.GetAllAsync())
            .SelectMany(p => p.Services).Select(s => s.DockerImageName)
            .Where(n => !string.IsNullOrWhiteSpace(n)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unused = new List<CleanupImage>();
        int used = 0, dangling = 0;
        foreach (var line in Lines(images.StdOut))
        {
            var f = line.Split('\t');
            if (f.Length < 5) continue;
            var (repo, tag, id, size, age) = (f[0], f[1], f[2], f[3], f[4]);
            if (repo == "<none>" || tag == "<none>") { dangling++; continue; }

            var reference = $"{repo}:{tag}";
            var bareId = id.Replace("sha256:", "");
            if (inUseRefs.Contains(reference) || inUseRefs.Contains(id) || inUseRefs.Any(r => r.Length >= 12 && bareId.StartsWith(r, StringComparison.OrdinalIgnoreCase)))
            {
                used++;
                continue;
            }

            var mine = shipRightRepos.Contains(repo);
            unused.Add(new CleanupImage(reference, id, size, ParseSize(size), age, mine ? "ShipRight" : "Other", mine));
        }

        unused = unused.OrderBy(i => i.Owner == "ShipRight" ? 0 : 1).ThenByDescending(i => i.SizeBytes).ToList();
        return new(unused, used, dangling, null);
    }

    private async Task<string> DiskUsageAsync(CancellationToken ct)
    {
        var df = await _runner.RunAsync("docker", ["system", "df"], null, timeout: TimeSpan.FromSeconds(30), ct: ct);
        return df.Success ? df.StdOut.Trim() : string.Empty;
    }

    /// <summary>Docker prints sizes in decimal units: 12.5kB, 682MB, 1.2GB.</summary>
    internal static long ParseSize(string text)
    {
        var t = text.Trim();
        var units = new (string Suffix, double Factor)[] { ("TB", 1e12), ("GB", 1e9), ("MB", 1e6), ("kB", 1e3), ("B", 1) };
        foreach (var (suffix, factor) in units)
        {
            if (!t.EndsWith(suffix, StringComparison.Ordinal)) continue;
            return double.TryParse(t[..^suffix.Length], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
                ? (long)Math.Round(n * factor) : 0;
        }
        return 0;
    }

    /// <summary>docker ps shows an image without its tag when the tag is latest.</summary>
    private static string WithTag(string image) =>
        image.Contains('@') || (image.Length >= 12 && image.All(Uri.IsHexDigit)) || image[(image.LastIndexOf('/') + 1)..].Contains(':') ? image : image + ":latest";

    private static IEnumerable<string> Lines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string? FirstLine(string text) => Lines(text).FirstOrDefault();

    private static string? LastLine(string text) => Lines(text).LastOrDefault();
}
