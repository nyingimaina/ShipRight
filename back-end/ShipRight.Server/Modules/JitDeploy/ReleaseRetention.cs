namespace ShipRight.Modules.JitDeploy;

/// <summary>One release on the server: its tag and every image built for it.</summary>
public sealed record ReleaseInfo(string Tag, IReadOnlyList<string> Images);

/// <summary>
/// Decides which old releases to delete. Whole releases only, oldest first. The newest N are kept (never fewer than
/// <see cref="MinimumToKeep"/>), and so is anything that could still be needed: the current release, the previous
/// one (the rollback target) and any release that has an image a container is using. Tags that are not release tags
/// are not ours and are never returned.
/// </summary>
public static class ReleaseRetention
{
    public const int MinimumToKeep = 2;

    public static IReadOnlyList<string> SelectToPrune(
        IReadOnlyCollection<ReleaseInfo> releases, int keep,
        string? currentTag, string? previousTag, IReadOnlySet<string> runningImages)
    {
        var effectiveKeep = Math.Max(keep, MinimumToKeep);

        var ours = new List<(ReleaseInfo Release, int Counter)>();
        foreach (var r in releases)
            if (ReleaseTag.TryParse(r.Tag, out var counter, out _)) ours.Add((r, counter));

        var protectedTags = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (r, _) in ours.OrderByDescending(x => x.Counter).Take(effectiveKeep)) protectedTags.Add(r.Tag);
        if (currentTag is not null) protectedTags.Add(currentTag);
        if (previousTag is not null) protectedTags.Add(previousTag);
        foreach (var (r, _) in ours)
            if (r.Images.Any(runningImages.Contains)) protectedTags.Add(r.Tag);

        return ours.Where(x => !protectedTags.Contains(x.Release.Tag))
                   .OrderBy(x => x.Counter)
                   .Select(x => x.Release.Tag)
                   .ToList();
    }
}
