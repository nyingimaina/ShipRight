namespace ShipRight.Modules.Database;

public sealed record BackupFileView(
    string FileName,
    string DatabaseName,
    string Format,
    string Compression,
    int CompressionLevel,
    string Encryption,
    string KeyId,
    long OriginalBytes,
    long StoredBytes,
    double Ratio,
    string Sha256,
    DateTimeOffset CreatedAt,
    string Source,
    string? ScheduleId,
    long DurationMs,
    int Rank,
    DateTimeOffset? ProjectedDeleteAt);

public sealed record BackupListingView(
    IReadOnlyList<BackupFileView> Files,
    int KeepCount,
    int KeepDays,
    double? CadenceSeconds);

/// <summary>One shape for the project and standalone-server backup lists, so the UI shares one component.</summary>
public static class BackupViews
{
    public static BackupListingView Build(IReadOnlyList<BackupArtifactMeta> artifacts, int keepCount, int keepDays)
    {
        var policy = RetentionPolicy.From(keepCount, keepDays);
        var interval = BackupRetention.ObservedInterval(artifacts);

        var files = artifacts
            .Select((artifact, index) => new BackupFileView(
                artifact.FileName,
                artifact.DatabaseName,
                artifact.Format,
                artifact.Compression,
                artifact.CompressionLevel,
                artifact.Encryption,
                artifact.KeyId,
                artifact.OriginalBytes,
                artifact.StoredBytes,
                Math.Round(artifact.Ratio, 2),
                artifact.Sha256,
                artifact.CreatedAtUtc,
                artifact.Source,
                artifact.ScheduleId,
                artifact.DurationMs,
                index + 1,
                BackupRetention.ProjectedDeletion(artifact, index + 1, policy, interval)))
            .ToList();

        return new BackupListingView(files, policy.KeepCount, policy.KeepDays, interval?.TotalSeconds);
    }
}
