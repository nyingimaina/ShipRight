using Renci.SshNet;
using Serilog;
using ShipRight.Shared.SshRunner;

namespace ShipRight.Modules.ServerNetwork;

public interface IServerRoutedRawSshRunner
{
    Task<int> RunAsync(
        ServerRoute route, string username, string keyPath, string command, Stream stdout,
        Func<string, Task>? onStderr = null, CancellationToken ct = default);
}

/// <summary>Raw byte SSH through the route's SOCKS5 proxy (Tailscale), for backups.</summary>
public sealed class Socks5RawSshRunner(KnownHostsStore knownHosts) : IServerRoutedRawSshRunner
{
    public async Task<int> RunAsync(
        ServerRoute route, string username, string keyPath, string command, Stream stdout,
        Func<string, Task>? onStderr = null, CancellationToken ct = default)
    {
        if (route.Proxy is not { Kind: ProxyKind.Socks5 } proxy)
            throw new NetworkUnavailableException("This route has no SOCKS5 proxy.");

        using var keyFile = await SshKeyLoader.LoadAsync(keyPath);
        var auth = new PrivateKeyAuthenticationMethod(username, keyFile);
        var connection = new Renci.SshNet.ConnectionInfo(
            route.Host, route.Port, username,
            ProxyTypes.Socks5, proxy.Host, proxy.Port, string.Empty, string.Empty, auth);
        connection.Timeout = TimeSpan.FromSeconds(30);

        using var client = new SshClient(connection);
        client.HostKeyReceived += (_, e) =>
        {
            var fingerprint = BitConverter.ToString(e.FingerPrint).Replace("-", ":").ToLowerInvariant();
            if (!knownHosts.IsKnown(route.Host, fingerprint)) knownHosts.Add(route.Host, fingerprint);
            e.CanTrust = true;
        };

        client.Connect();
        try
        {
            using var cmd = client.CreateCommand(command);
            var asyncResult = cmd.BeginExecute();
            var output = cmd.OutputStream.CopyToAsync(stdout, 64 * 1024, ct);
            var error = RawSshRunner.StreamStderrAsync(cmd.ExtendedOutputStream, onStderr, ct);
            await Task.WhenAll(output, error);
            cmd.EndExecute(asyncResult);
            return cmd.ExitStatus ?? -1;
        }
        finally
        {
            client.Disconnect();
        }
    }
}

/// <summary>Chooses the direct or proxied raw runner from the resolved route.</summary>
public sealed class NetworkAwareRawSshRunner(
    IRawSshRunner inner,
    IServerNetworkResolver resolver,
    IServerRoutedRawSshRunner routed) : IRawSshRunner
{
    public async Task<int> RunAsync(
        string host, string username, string keyPath, string command, Stream stdout,
        Func<string, Task>? onStderr = null, CancellationToken ct = default)
    {
        var route = await resolver.ResolveForHostAsync(host, ct);
        if (route.Proxy is null)
            return await inner.RunAsync(host, username, keyPath, command, stdout, onStderr, ct);

        Log.Information("Raw SSH through Tailscale for {Host}", route.Host);
        return await routed.RunAsync(route, username, keyPath, command, stdout, onStderr, ct);
    }
}
