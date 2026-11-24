using System.Net;
using System.Net.Sockets;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.ConnectionTest;
using ShipRight.Modules.Projects;
using ShipRight.Modules.ServerNetwork;

namespace ShipRight.Tests.Modules.ServerNetwork;

[TestClass]
public class RoutedHostKeyScannerTests
{
    private sealed class FixedResolver(ServerRoute route) : IServerNetworkResolver
    {
        public Task<ServerRoute> ResolveAsync(ServerConfig server, CancellationToken ct = default) => Task.FromResult(route);
        public Task<ServerRoute> ResolveForHostAsync(string host, CancellationToken ct = default) => Task.FromResult(route with { Host = host });
    }

    private sealed class RecordingScanner : IHostKeyScanner
    {
        public int Calls { get; private set; }
        public Task<HostKeyScan> ScanAsync(string host, int port, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HostKeyScan("ssh-ed25519", [1, 2, 3]));
        }
    }

    /// <summary>Answers the SOCKS5 handshake and CONNECT, then closes, so the SSH exchange cannot finish.</summary>
    private sealed class ClosingSocks5 : IDisposable
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

                var version = new byte[1];
                await stream.ReadExactlyAsync(version);
                var count = new byte[1];
                await stream.ReadExactlyAsync(count);
                var methods = new byte[count[0]];
                await stream.ReadExactlyAsync(methods);
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
                else if (head[3] == 4)
                {
                    var addr = new byte[16];
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
                var port = new byte[2];
                await stream.ReadExactlyAsync(port);
                Requests.Add($"{target}:{(port[0] << 8) | port[1]}");

                await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, 0, 0 });
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
    public async Task DirectRoute_UsesThePlainScanner()
    {
        var direct = new RecordingScanner();
        var scanner = new RoutedHostKeyScanner(direct, new FixedResolver(new ServerRoute("box.example.com", 22)));

        var scan = await scanner.ScanAsync("box.example.com", 22, CancellationToken.None);

        Assert.AreEqual(1, direct.Calls);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, scan.Blob);
    }

    [TestMethod]
    public async Task ProxiedRoute_ReadsTheKeyThroughTheSocksProxy()
    {
        using var socks = new ClosingSocks5();
        socks.Start();
        var direct = new RecordingScanner();
        var scanner = new RoutedHostKeyScanner(direct, new FixedResolver(
            new ServerRoute("box.tailnet.ts.net", 22, new ProxyRoute("127.0.0.1", socks.Port, ProxyKind.Socks5))));

        Exception? failure = null;
        try { await scanner.ScanAsync("box.tailnet.ts.net", 22, CancellationToken.None); }
        catch (Exception ex) { failure = ex; }
        await socks.WaitAsync();

        Assert.IsNotNull(failure, "the SSH exchange cannot finish once the proxy closes");
        Assert.AreEqual(0, direct.Calls, "a tailnet server must not be scanned directly");
        Assert.AreEqual("box.tailnet.ts.net:22", socks.Requests.FirstOrDefault(),
            "the proxy should be asked for the tailnet name, not a locally resolved address");
    }
}
