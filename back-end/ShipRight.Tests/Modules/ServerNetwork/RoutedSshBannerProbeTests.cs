using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Projects;
using ShipRight.Modules.ServerNetwork;

namespace ShipRight.Tests.Modules.ServerNetwork;

[TestClass]
public class RoutedSshBannerProbeTests
{
    private sealed class FixedResolver(ServerRoute route) : IServerNetworkResolver
    {
        public Task<ServerRoute> ResolveAsync(ServerConfig server, CancellationToken ct = default) => Task.FromResult(route);
        public Task<ServerRoute> ResolveForHostAsync(string host, CancellationToken ct = default) => Task.FromResult(route with { Host = host });
    }

    private static async Task<(TcpListener Listener, int Port)> BannerServerAsync(string line)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync();
                await using var stream = client.GetStream();
                await stream.WriteAsync(Encoding.ASCII.GetBytes(line + "\r\n"));
                await stream.FlushAsync();
                await Task.Delay(50);
            }
            finally { listener.Stop(); }
        });
        await Task.CompletedTask;
        return (listener, port);
    }

    [TestMethod]
    public async Task ReadsTheBannerFromTheResolvedRoute()
    {
        var (listener, port) = await BannerServerAsync("SSH-2.0-Tailscale");
        try
        {
            var probe = new RoutedSshBannerProbe(new FixedResolver(new ServerRoute("127.0.0.1", port)));
            var banner = await probe.ReadAsync(new ServerConfig { Host = "127.0.0.1" });
            Assert.AreEqual("SSH-2.0-Tailscale", banner);
        }
        finally { listener.Stop(); }
    }

    [TestMethod]
    public async Task AnUnreachableRoute_ReturnsNothingInsteadOfThrowing()
    {
        var probe = new RoutedSshBannerProbe(new FixedResolver(new ServerRoute("127.0.0.1", 1)));
        Assert.AreEqual(string.Empty, await probe.ReadAsync(new ServerConfig { Host = "127.0.0.1" }));
    }
}
