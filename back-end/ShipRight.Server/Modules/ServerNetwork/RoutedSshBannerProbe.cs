using System.Net;
using System.Net.Sockets;
using System.Text;
using ShipRight.Modules.Projects;

namespace ShipRight.Modules.ServerNetwork;

/// <summary>
/// Reads the SSH identification line the way the real connection would be made: directly, or through the server's
/// SOCKS5 proxy when it belongs to a tailnet. Without this, a Tailscale SSH server looks like a plain timeout because
/// the management host cannot reach the tailnet address on its own.
/// </summary>
public sealed class RoutedSshBannerProbe(IServerNetworkResolver resolver)
{
    public async Task<string> ReadAsync(ServerConfig server, CancellationToken ct = default)
    {
        var route = await resolver.ResolveAsync(server, ct);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(4));

        using var client = route.Proxy is { Kind: ProxyKind.Socks5 } proxy
            ? await ConnectThroughSocks5Async(proxy, route.Host, route.Port, limit.Token)
            : await ConnectDirectAsync(route.Host, route.Port, limit.Token);

        if (client is null) return string.Empty;
        using (client)
        {
            using var stream = client.GetStream();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            var buffer = new byte[255];
            var n = 0;
            try
            {
                while (n < buffer.Length)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(n), timeout.Token);
                    if (read == 0) break;
                    n += read;
                    if (Array.IndexOf(buffer, (byte)'\n', 0, n) >= 0) break;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (IOException) { }

            return n == 0 ? string.Empty : Encoding.ASCII.GetString(buffer, 0, n).Split('\n')[0].Trim();
        }
    }

    private static async Task<TcpClient?> ConnectDirectAsync(string host, int port, CancellationToken ct)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, port, ct);
            return client;
        }
        catch
        {
            client.Dispose();
            return null;
        }
    }

    private static async Task<TcpClient?> ConnectThroughSocks5Async(
        ProxyRoute proxy, string host, int port, CancellationToken ct)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(proxy.Host, proxy.Port, ct);
            var stream = client.GetStream();

            await stream.WriteAsync(new byte[] { 5, 1, 0 }, ct);
            var greeting = new byte[2];
            await stream.ReadExactlyAsync(greeting, ct);
            if (greeting[0] != 5 || greeting[1] != 0)
            {
                client.Dispose();
                return null;
            }

            await stream.WriteAsync(BuildConnectRequest(host, port), ct);
            var reply = new byte[4];
            await stream.ReadExactlyAsync(reply, ct);
            if (reply[0] != 5 || reply[1] != 0)
            {
                client.Dispose();
                return null;
            }

            return client;
        }
        catch
        {
            client.Dispose();
            return null;
        }
    }

    private static byte[] BuildConnectRequest(string host, int port)
    {
        using var body = new MemoryStream();
        if (IPAddress.TryParse(host, out var ip))
        {
            body.WriteByte((byte)(ip.AddressFamily == AddressFamily.InterNetwork ? 1 : 4));
            body.Write(ip.GetAddressBytes());
        }
        else
        {
            var name = Encoding.ASCII.GetBytes(host);
            body.WriteByte(3);
            body.WriteByte((byte)name.Length);
            body.Write(name);
        }
        body.WriteByte((byte)(port >> 8));
        body.WriteByte((byte)(port & 0xFF));
        return [5, 1, 0, .. body.ToArray()];
    }
}
