using System.Net;
using System.Net.Sockets;
using System.Text;
using ShipRight.Modules.ConnectionTest;
using ShipRight.Modules.Projects;

namespace ShipRight.Modules.ServerNetwork;

/// <summary>
/// Answers "can this route reach the server" the same way <see cref="TcpProbe"/> does, but follows the resolved
/// server route: a direct route is probed normally, a proxied route is probed through the proxy (a SOCKS5 CONNECT).
/// A tailnet that is not running is reported as such instead of looking like a missing server.
/// </summary>
public sealed class RoutedTcpProbe(ITcpProbe direct, IServerNetworkResolver resolver) : ITcpProbe
{
    public async Task<TcpProbeResult> ProbeAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        ServerRoute route;
        try
        {
            route = await resolver.ResolveForHostAsync(host, ct);
        }
        catch (NetworkUnavailableException)
        {
            return TcpProbeResult.Failed(TcpFailure.ProxyUnavailable);
        }

        if (route.Proxy is not { Kind: ProxyKind.Socks5 } proxy)
            return await direct.ProbeAsync(route.Host, port, timeout, ct);

        return await ProbeThroughSocks5Async(proxy, route.Host, port, timeout, ct);
    }

    private static async Task<TcpProbeResult> ProbeThroughSocks5Async(
        ProxyRoute proxy, string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        var connected = false;
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(proxy.Host, proxy.Port, limit.Token);
            connected = true;
            using var stream = client.GetStream();

            // Greeting: version 5, one method, "no authentication".
            await stream.WriteAsync(new byte[] { 5, 1, 0 }, limit.Token);
            var greeting = new byte[2];
            await stream.ReadExactlyAsync(greeting, limit.Token);
            if (greeting[0] != 5 || greeting[1] != 0)
                return TcpProbeResult.Failed(TcpFailure.ProxyUnavailable);

            var request = BuildConnectRequest(host, port);
            await stream.WriteAsync(request, limit.Token);

            var reply = new byte[4];
            await stream.ReadExactlyAsync(reply, limit.Token);
            if (reply[0] != 5) return TcpProbeResult.Failed(TcpFailure.Other);
            if (reply[1] == 0) return TcpProbeResult.Success;

            return TcpProbeResult.Failed(ReplyKind(reply[1]));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Once the proxy is connected, a timeout means it could not reach the target (usually ACL).
            return TcpProbeResult.Failed(connected ? TcpFailure.ProxyBlocked : TcpFailure.ProxyUnavailable);
        }
        catch (SocketException ex)
        {
            // The proxy itself refused or dropped the connection, so the tailnet is not usable.
            return ex.SocketErrorCode is SocketError.ConnectionRefused or SocketError.NetworkUnreachable
                or SocketError.HostUnreachable or SocketError.HostNotFound or SocketError.TimedOut
                ? TcpProbeResult.Failed(connected ? TcpFailure.ProxyBlocked : TcpFailure.ProxyUnavailable)
                : TcpProbeResult.Failed(TcpProbe.Kind(ex));
        }
        catch (IOException)
        {
            return TcpProbeResult.Failed(connected ? TcpFailure.ProxyBlocked : TcpFailure.ProxyUnavailable);
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
            if (name.Length is 0 or > 255) throw new NetworkUnavailableException("That host name is not usable through the proxy.");
            body.WriteByte(3);
            body.WriteByte((byte)name.Length);
            body.Write(name);
        }

        body.WriteByte((byte)(port >> 8));
        body.WriteByte((byte)(port & 0xFF));

        var payload = body.ToArray();
        var request = new byte[3 + payload.Length];
        request[0] = 5;   // version
        request[1] = 1;   // CONNECT
        request[2] = 0;   // reserved
        payload.CopyTo(request, 3);
        return request;
    }

    private static TcpFailure ReplyKind(byte reply) => reply switch
    {
        1 or 2 => TcpFailure.ProxyBlocked,
        3 or 4 => TcpFailure.Unreachable,
        5 => TcpFailure.Refused,
        6 => TcpFailure.TimedOut,
        _ => TcpFailure.Other,
    };
}
