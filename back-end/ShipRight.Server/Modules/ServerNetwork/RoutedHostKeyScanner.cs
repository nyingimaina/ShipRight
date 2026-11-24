using Renci.SshNet;
using Serilog;
using ShipRight.Modules.ConnectionTest;

namespace ShipRight.Modules.ServerNetwork;

/// <summary>
/// Reads a server's host key along its resolved route: plain SSH for a direct server, the tailscaled SOCKS5 proxy for a
/// tailnet server. The key is still refused after it is read, so nothing is trusted here.
/// </summary>
public sealed class RoutedHostKeyScanner(IHostKeyScanner direct, IServerNetworkResolver resolver) : IHostKeyScanner
{
    public async Task<HostKeyScan> ScanAsync(string host, int port, CancellationToken ct)
    {
        var route = await resolver.ResolveForHostAsync(host, ct);
        if (route.Proxy is not { Kind: ProxyKind.Socks5 } proxy)
            return await direct.ScanAsync(route.Host, port, ct);

        return await Task.Run(() =>
        {
            HostKeyScan? found = null;
            var user = "hostkey-check";
            var info = new Renci.SshNet.ConnectionInfo(
                route.Host, port, user,
                ProxyTypes.Socks5, proxy.Host, proxy.Port, string.Empty, string.Empty,
                new PasswordAuthenticationMethod(user, "x"));
            info.Timeout = TimeSpan.FromSeconds(15);

            using var client = new SshClient(info);
            client.HostKeyReceived += (_, e) =>
            {
                found = new HostKeyScan(e.HostKeyName, e.HostKey);
                e.CanTrust = false;
            };
            try { client.Connect(); }
            catch (Exception) when (found is not null) { /* expected: the key was refused on purpose */ }

            if (found is null) throw new IOException("The server did not present a host key.");
            Log.Information("Host key read through Tailscale for {Host}", route.Host);
            return found;
        }, ct);
    }
}
