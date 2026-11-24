using System.Text;
using Renci.SshNet;
using Serilog;
using ShipRight.Shared.SshRunner;
using ShipRight.Shared.Store;

namespace ShipRight.Modules.ServerNetwork;

/// <summary>An SSH connection through the proxy route. The route is the only Tailscale-specific input.</summary>
public sealed class Socks5SshRunner(KnownHostsStore knownHosts) : IServerRoutedSshRunner
{
    public async Task<int> RunAsync(
        ServerRoute route, string username, string keyPath, string command,
        Func<string, Task>? onOutput = null, Func<string, Task>? onStderr = null,
        CancellationToken ct = default)
    {
        if (route.Proxy is not { Kind: ProxyKind.Socks5 } proxy)
            throw new NetworkUnavailableException("This route has no SOCKS5 proxy.");

        Log.Information("SSH connecting through Tailscale SOCKS5: {Username}@{Host}", username, route.Host);

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
            if (!knownHosts.IsKnown(route.Host, fingerprint))
            {
                knownHosts.Add(route.Host, fingerprint);
                Log.Information("SSH host key accepted through Tailscale for {Host}: {Fingerprint}", route.Host, fingerprint);
            }
            e.CanTrust = true;
        };

        client.Connect();
        Log.Information("SSH connected through Tailscale: {Username}@{Host}", username, route.Host);

        try
        {
            using var cmd = client.CreateCommand(command);
            var asyncResult = cmd.BeginExecute();
            var stdout = StreamAsync(cmd.OutputStream, onOutput, ct);
            var stderr = StreamAsync(cmd.ExtendedOutputStream, onStderr ?? onOutput, ct);
            await Task.WhenAll(stdout, stderr);
            cmd.EndExecute(asyncResult);
            Log.Information("SSH command exited {ExitCode} through Tailscale: {Host}", cmd.ExitStatus, route.Host);
            return cmd.ExitStatus ?? -1;
        }
        finally
        {
            client.Disconnect();
        }
    }

    private static async Task StreamAsync(Stream stream, Func<string, Task>? onOutput, CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        var buffer = new char[256];
        var line = new StringBuilder();

        while (!ct.IsCancellationRequested)
        {
            int read;
            try { read = await reader.ReadAsync(buffer, 0, buffer.Length); }
            catch (ObjectDisposedException) { break; }
            catch (OperationCanceledException) { break; }
            if (read == 0) break;

            for (var i = 0; i < read; i++)
            {
                if (buffer[i] == '\n')
                {
                    if (onOutput is not null && line.Length > 0) await onOutput(line.ToString());
                    line.Clear();
                }
                else line.Append(buffer[i]);
            }
        }

        if (onOutput is not null && line.Length > 0) await onOutput(line.ToString());
    }
}
