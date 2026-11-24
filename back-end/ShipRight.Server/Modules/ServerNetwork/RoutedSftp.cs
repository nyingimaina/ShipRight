using Renci.SshNet;
using ShipRight.Modules.Projects;
using ShipRight.Shared.SshRunner;

namespace ShipRight.Modules.ServerNetwork;

/// <summary>
/// Opens SFTP connections along the resolved server route: a direct server keeps its plain connection, a tailnet
/// server is reached through the tailscaled SOCKS5 proxy. The caller owns the returned connection (client + key).
/// Without a resolver (older call sites and tests) the direct path is used, unchanged.
/// </summary>
public static class RoutedSftp
{
    public static TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public static async Task<RoutedSftpConnection> OpenAsync(
        IServerNetworkResolver? resolver, ServerConfig server, KnownHostsStore knownHosts,
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(server.SshKeyPath))
            throw new InvalidOperationException("No SSH key is configured for this server.");

        var route = resolver is null
            ? new ServerRoute(server.Host, 22)
            : await resolver.ResolveAsync(server, ct);
        return await OpenRouteAsync(route, server.Username, server.SshKeyPath, knownHosts, timeout ?? DefaultTimeout, ct);
    }

    /// <summary>For callers that only know a host (the file picker). Unknown hosts stay on the direct path.</summary>
    public static async Task<RoutedSftpConnection> OpenForHostAsync(
        IServerNetworkResolver? resolver, string host, int port, string username, string keyPath,
        KnownHostsStore knownHosts, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var route = resolver is null
            ? new ServerRoute(host, port)
            : await resolver.ResolveForHostAsync(host, ct);
        if (port > 0 && port != route.Port) route = route with { Port = port };
        return await OpenRouteAsync(route, username, keyPath, knownHosts, timeout ?? DefaultTimeout, ct);
    }

    private static async Task<RoutedSftpConnection> OpenRouteAsync(
        ServerRoute route, string username, string keyPath, KnownHostsStore knownHosts,
        TimeSpan timeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var key = await SshKeyLoader.LoadAsync(keyPath);
        try
        {
            var auth = new PrivateKeyAuthenticationMethod(username, key);
            var info = route.Proxy is { Kind: ProxyKind.Socks5 } proxy
                ? new Renci.SshNet.ConnectionInfo(route.Host, route.Port, username,
                    ProxyTypes.Socks5, proxy.Host, proxy.Port, string.Empty, string.Empty, auth)
                : new Renci.SshNet.ConnectionInfo(route.Host, route.Port, username, auth);
            info.Timeout = timeout;

            var client = new SftpClient(info);
            client.HostKeyReceived += (_, e) =>
            {
                var fingerprint = BitConverter.ToString(e.FingerPrint).Replace("-", ":").ToLowerInvariant();
                if (!knownHosts.IsKnown(route.Host, fingerprint)) knownHosts.Add(route.Host, fingerprint);
                e.CanTrust = true;
            };

            try
            {
                client.Connect();
                return new RoutedSftpConnection(client, key);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }
}

/// <summary>An open SFTP client plus the private key it was created with; disposing closes both.</summary>
public sealed class RoutedSftpConnection(SftpClient client, IDisposable key) : IDisposable
{
    public SftpClient Client { get; } = client;

    public void Dispose()
    {
        try { if (Client.IsConnected) Client.Disconnect(); } catch { /* best-effort */ }
        Client.Dispose();
        key.Dispose();
    }
}
