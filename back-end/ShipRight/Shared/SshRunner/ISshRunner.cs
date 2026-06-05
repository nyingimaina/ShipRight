namespace ShipRight.Shared.SshRunner;

public interface ISshRunner
{
    Task<int> RunAsync(
        string host, string username, string keyPath,
        string command,
        Func<string, Task>? onOutput = null,
        CancellationToken ct = default);
}
