namespace ShipRight.Modules.RemoteHost;

public interface IFileOperationsProvider
{
    /// Compresses a remote directory and returns the remote path of the resulting archive.
    Task<string> CompressDirectoryAsync(RemoteHostConfig host, string remotePath, CancellationToken ct = default);

    /// Downloads a remote file to a local path.
    Task DownloadAsync(RemoteHostConfig host, string remotePath, string localPath, CancellationToken ct = default);
}
