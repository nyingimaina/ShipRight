using System.Collections.Concurrent;

namespace ShipRight.Modules.Database;

public sealed record BackupRunInfo(string FileName, long OriginalBytes, long StoredBytes, long DurationMs);

/// <summary>
/// Carries the result of the last scheduled run from the backup code to the scheduler callbacks, which only
/// receive a success flag. One run per project is enforced by the backup lock, so a per-project slot is enough.
/// </summary>
public sealed class BackupRunTelemetry
{
    private readonly ConcurrentDictionary<string, BackupRunInfo> _runs = new(StringComparer.Ordinal);

    public void Record(string projectId, BackupRunInfo info) => _runs[projectId] = info;

    public BackupRunInfo? Take(string projectId) =>
        _runs.TryRemove(projectId, out var info) ? info : null;
}
