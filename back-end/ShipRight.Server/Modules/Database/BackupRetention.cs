namespace ShipRight.Modules.Database;

public sealed record RetentionPolicy(int KeepCount, int KeepDays)
{
    /// <summary>Count and age work together: a file goes when it is beyond the count *or* older than the age cap.</summary>
    public static RetentionPolicy From(int keepCount, int keepDays) =>
        new(Math.Max(1, keepCount), Math.Max(0, keepDays));
}

public sealed record RetentionDecision(
    IReadOnlyList<BackupArtifactMeta> Keep,
    IReadOnlyList<BackupArtifactMeta> Delete);

/// <summary>
/// Decides which artifacts survive. The newest file is always kept, even when it is older than the age cap,
/// so a forgotten schedule can never leave a project with zero backups.
/// </summary>
public static class BackupRetention
{
    public static RetentionDecision Plan(
        IEnumerable<BackupArtifactMeta> artifacts, DateTimeOffset now, RetentionPolicy policy)
    {
        var ordered = artifacts
            .OrderByDescending(a => a.CreatedAtUtc)
            .ThenByDescending(a => a.FileName, StringComparer.Ordinal)
            .ToList();

        var keep = new List<BackupArtifactMeta>();
        var delete = new List<BackupArtifactMeta>();

        for (var index = 0; index < ordered.Count; index++)
        {
            var artifact = ordered[index];
            if (index == 0)
            {
                keep.Add(artifact);
                continue;
            }

            var beyondCount = index >= policy.KeepCount;
            var beyondAge = policy.KeepDays > 0 &&
                            now - artifact.CreatedAtUtc > TimeSpan.FromDays(policy.KeepDays);

            if (beyondCount || beyondAge) delete.Add(artifact);
            else keep.Add(artifact);
        }

        return new RetentionDecision(keep, delete);
    }

    /// <summary>The typical gap between backups, used only to show "roughly when this will rotate out".</summary>
    public static TimeSpan? ObservedInterval(IEnumerable<BackupArtifactMeta> artifacts)
    {
        var times = artifacts
            .Select(a => a.CreatedAtUtc)
            .OrderByDescending(t => t)
            .Take(21)
            .ToList();

        var gaps = new List<double>();
        for (var i = 1; i < times.Count; i++)
        {
            var gap = (times[i - 1] - times[i]).TotalSeconds;
            if (gap > 60) gaps.Add(gap);
        }

        if (gaps.Count == 0) return null;
        gaps.Sort();
        return TimeSpan.FromSeconds(gaps[gaps.Count / 2]);
    }

    /// <summary>When a file is expected to be pruned: whichever of the count or age rule comes first.</summary>
    public static DateTimeOffset? ProjectedDeletion(
        BackupArtifactMeta artifact, int rank, RetentionPolicy policy, TimeSpan? interval)
    {
        DateTimeOffset? byCount = null;
        if (interval is { TotalSeconds: > 0 } gap)
        {
            var runsUntilGone = Math.Max(1, policy.KeepCount - rank + 1);
            byCount = artifact.CreatedAtUtc + gap * runsUntilGone;
        }

        DateTimeOffset? byAge = policy.KeepDays > 0
            ? artifact.CreatedAtUtc + TimeSpan.FromDays(policy.KeepDays)
            : null;

        return (byCount, byAge) switch
        {
            ({ } count, { } age) => count < age ? count : age,
            ({ } count, null) => count,
            (null, { } age) => age,
            _ => null,
        };
    }
}
