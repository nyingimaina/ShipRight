namespace ShipRight.Modules.RemoteHost;

public record DatabaseBackupRequest(string ContainerName, string DatabaseName, string BackupDir);

public interface IDatabaseBackupProvider
{
    /// Triggers a backup on the remote host and returns the remote path of the resulting file.
    Task<string> BackupAsync(RemoteHostConfig host, DatabaseBackupRequest req, CancellationToken ct = default);
}
