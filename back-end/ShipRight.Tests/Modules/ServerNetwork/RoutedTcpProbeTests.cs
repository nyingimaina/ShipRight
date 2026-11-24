using System.Net;
using System.Net.Sockets;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.ConnectionTest;
using ShipRight.Modules.Projects;
using ShipRight.Modules.ServerNetwork;

namespace ShipRight.Tests.Modules.ServerNetwork;

[TestClass]
public class RoutedTcpProbeTests
{
    private sealed class FixedResolver(ServerRoute route, Exception? error = null) : IServerNetworkResolver
    {
        public Task<ServerRoute> ResolveAsync(ServerConfig server, CancellationToken ct = default) =>
            error is null ? Task.FromResult(route) : Task.FromException<ServerRoute>(error);

        public Task<ServerRoute> ResolveForHostAsync(string host, CancellationToken ct = default) =>
            error is null ? Task.FromResult(route with { Host = host }) : Task.FromException<ServerRoute>(error);
    }

    private sealed class RecordingProbe(TcpProbeResult result) : ITcpProbe
    {
        public string? Host { get; private set; }
        public int Port { get; private set; }
        public int Calls { get; private set; }

        public Task<TcpProbeResult> ProbeAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
        {
            Calls++;
            Host = host;
            Port = port;
            return Task.FromResult(result);
        }
    }

    /// <summary>A one-shot SOCKS5 server that answers the handshake and one CONNECT with a fixed reply code.</summary>
    private sealed class StubSocks5(byte connectReply) : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        public int Port { get; private set; }
        public List<string> Requests { get; } = [];
        private Task _serving = Task.CompletedTask;

        public void Start()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _serving = ServeAsync();
        }

        private async Task ServeAsync()
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync();
                using var stream = client.GetStream();

                var greeting = new byte[3];
                await stream.ReadExactlyAsync(greeting);
                await stream.WriteAsync(new byte[] { 5, 0 });

                var head = new byte[4];
                await stream.ReadExactlyAsync(head);
                string target;
                if (head[3] == 1)
                {
                    var addr = new byte[4];
                    await stream.ReadExactlyAsync(addr);
                    target = new IPAddress(addr).ToString();
                }
                else
                {
                    var length = new byte[1];
                    await stream.ReadExactlyAsync(length);
                    var name = new byte[length[0]];
                    await stream.ReadExactlyAsync(name);
                    target = System.Text.Encoding.ASCII.GetString(name);
                }
                var portBytes = new byte[2];
                await stream.ReadExactlyAsync(portBytes);
                Requests.Add($"{target}:{(portBytes[0] << 8) | portBytes[1]}");

                await stream.WriteAsync(new byte[] { 5, connectReply, 0, 1, 0, 0, 0, 0, 0, 0 });
            }
            finally
            {
                _listener.Stop();
            }
        }

        public Task WaitAsync() => _serving.WaitAsync(TimeSpan.FromSeconds(5));

        public void Dispose()
        {
            try { _listener.Stop(); } catch { /* already stopped */ }
        }
    }

    [TestMethod]
    public async Task DirectRoute_UsesThePlainProbe()
    {
        var direct = new RecordingProbe(TcpProbeResult.Success);
        var probe = new RoutedTcpProbe(direct, new FixedResolver(new ServerRoute("box.example.com", 22)));

        var result = await probe.ProbeAsync("box.example.com", 22, TimeSpan.FromSeconds(2), CancellationToken.None);

        Assert.IsTrue(result.Reached);
        Assert.AreEqual(1, direct.Calls);
        Assert.AreEqual("box.example.com", direct.Host);
    }

    [TestMethod]
    public async Task ProxiedRoute_ConnectsThroughTheSocks5Proxy()
    {
        using var stub = new StubSocks5(0);
        stub.Start();
        var direct = new RecordingProbe(TcpProbeResult.Success);
        var probe = new RoutedTcpProbe(direct, new FixedResolver(
            new ServerRoute("box.tailnet.ts.net", 22, new ProxyRoute("127.0.0.1", stub.Port, ProxyKind.Socks5))));

        var result = await probe.ProbeAsync("box.tailnet.ts.net", 22, TimeSpan.FromSeconds(5), CancellationToken.None);
        await stub.WaitAsync();

        Assert.IsTrue(result.Reached);
        Assert.AreEqual(0, direct.Calls, "a proxied route must not fall back to a direct connection");
        CollectionAssert.AreEqual(new[] { "box.tailnet.ts.net:22" }, stub.Requests);
    }

    [TestMethod]
    public async Task ProxiedRoute_ReportsTheProxysRefusal()
    {
        using var stub = new StubSocks5(5);
        stub.Start();
        var probe = new RoutedTcpProbe(new RecordingProbe(TcpProbeResult.Success), new FixedResolver(
            new ServerRoute("box.tailnet.ts.net", 22, new ProxyRoute("127.0.0.1", stub.Port, ProxyKind.Socks5))));

        var result = await probe.ProbeAsync("box.tailnet.ts.net", 22, TimeSpan.FromSeconds(5), CancellationToken.None);
        await stub.WaitAsync();

        Assert.IsFalse(result.Reached);
        Assert.AreEqual(TcpFailure.Refused, result.Failure);
    }

    [TestMethod]
    public async Task OfflineTailnet_IsReportedAsAnUnavailableProxy()
    {
        var direct = new RecordingProbe(TcpProbeResult.Success);
        var probe = new RoutedTcpProbe(direct, new FixedResolver(
            new ServerRoute("box.tailnet.ts.net", 22),
            new NetworkUnavailableException("Tailscale network 'Acme' is not online.")));

        var result = await probe.ProbeAsync("box.tailnet.ts.net", 22, TimeSpan.FromSeconds(2), CancellationToken.None);

        Assert.IsFalse(result.Reached);
        Assert.AreEqual(TcpFailure.ProxyUnavailable, result.Failure);
        Assert.AreEqual(0, direct.Calls, "an offline tailnet must fail closed");
    }

    [TestMethod]
    public void ReachAdvice_ExplainsAnUnavailableTailnet()
    {
        var advice = ReachAdvisor.Evaluate("box.tailnet.ts.net", TcpProbeResult.Failed(TcpFailure.ProxyUnavailable));

        Assert.AreEqual(CheckStatus.Fail, advice.Status);
        StringAssert.Contains(advice.Message, "Tailscale network");
        StringAssert.Contains(advice.Fix, "Tailscale in ShipRight");
    }
}
