namespace ShipRight.Shared.SshRunner;

/// <summary>
/// Runs a remote command and writes stdout as raw bytes, for backups where the bytes must be preserved
/// (line-based <see cref="ISshRunner"/> normalises text and is unsuitable for a dump file).
/// </summary>
public interface IRawSshRunner
{
    Task<int> RunAsync(
        string host, string username, string keyPath, string command, Stream stdout,
        Func<string, Task>? onStderr = null, CancellationToken ct = default);
}
