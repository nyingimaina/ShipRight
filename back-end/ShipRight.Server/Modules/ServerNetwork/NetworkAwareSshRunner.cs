using ShipRight.Shared.SshRunner;

namespace ShipRight.Modules.ServerNetwork;

/// <summary>
/// Keeps every existing ISshRunner caller unchanged while allowing a route resolver to switch the transport.
/// Direct routes pass straight through to the original runner.
/// </summary>
public sealed class NetworkAwareSshRunner(
    ISshRunner inner,
    IServerNetworkResolver resolver,
    IServerRoutedSshRunner routed) : ISshRunner
{
    public async Task<int> RunAsync(
        string host, string username, string keyPath, string command,
        Func<string, Task>? onOutput = null, Func<string, Task>? onStderr = null,
        CancellationToken ct = default)
    {
        var route = await resolver.ResolveForHostAsync(host, ct);
        return route.Proxy is null
            ? await inner.RunAsync(host, username, keyPath, command, onOutput, onStderr, ct)
            : await routed.RunAsync(route, username, keyPath, command, onOutput, onStderr, ct);
    }
}
