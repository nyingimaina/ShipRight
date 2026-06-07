namespace ShipRight.Shared.SshRunner;

public interface ISshRunner
{
    /// <summary>
    /// onOutput receives stdout (and stderr when onStderr is null).
    /// Pass onStderr explicitly to route the two streams separately.
    /// </summary>
    Task<int> RunAsync(
        string host, string username, string keyPath,
        string command,
        Func<string, Task>? onOutput = null,
        Func<string, Task>? onStderr = null,
        CancellationToken ct = default);
}
