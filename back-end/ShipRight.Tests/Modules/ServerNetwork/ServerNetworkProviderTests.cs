using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShipRight.Modules.Projects;
using ShipRight.Modules.ServerNetwork;
using ShipRight.Modules.Servers;

namespace ShipRight.Tests.Modules.ServerNetwork;

[TestClass]
public class ServerNetworkProviderTests
{
    private sealed class FakeTailnets : ITailnetManager
    {
        public TailnetRuntime Runtime { get; set; } = new("tailnet-1", "Acme", 43123, true);
        public Exception? Throw { get; set; }
        public List<string> Requested { get; } = [];

        public Task<TailnetRuntime> EnsureRunningAsync(string tailnetId, CancellationToken ct = default)
        {
            Requested.Add(tailnetId);
            if (Throw is not null) throw Throw;
            return Task.FromResult(Runtime with { Id = tailnetId });
        }

        public Task<TailnetRuntime> GetStatusAsync(string tailnetId, CancellationToken ct = default) =>
            Task.FromResult(Runtime with { Id = tailnetId });

        public Task StopAsync(string tailnetId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class MemoryServers(params ServerConfig[] servers) : IServerStore
    {
        public Task<List<ServerConfig>> GetAllAsync() => Task.FromResult(servers.ToList());
        public Task<ServerConfig?> GetByIdAsync(string id) => Task.FromResult(servers.FirstOrDefault(s => s.Id == id));
        public Task SaveAsync(ServerConfig server) => Task.CompletedTask;
        public Task DeleteAsync(string id) => Task.CompletedTask;
    }

    [TestMethod]
    public async Task DirectProvider_ReturnsTheSavedHostWithNoProxy()
    {
        var route = await new DirectNetworkProvider().ResolveAsync(new ServerConfig { Host = "box.example.com" });

        Assert.AreEqual("box.example.com", route.Host);
        Assert.AreEqual(22, route.Port);
        Assert.IsNull(route.Proxy);
    }

    [TestMethod]
    public async Task TailscaleProvider_ReturnsALoopbackSocksRouteForTheTailnet()
    {
        var tailnets = new FakeTailnets();
        var route = await new TailscaleNetworkProvider(tailnets).ResolveAsync(new ServerConfig
        {
            Host = "box.tailnet.ts.net",
            TailnetId = "tailnet-1",
        });

        Assert.AreEqual("box.tailnet.ts.net", route.Host);
        Assert.AreEqual(new ProxyRoute("127.0.0.1", 43123, ProxyKind.Socks5), route.Proxy);
        CollectionAssert.AreEqual(new[] { "tailnet-1" }, tailnets.Requested);
    }

    [TestMethod]
    public async Task TailscaleProvider_FailsClosedWhenTheTailnetIsOffline()
    {
        var tailnets = new FakeTailnets { Runtime = new("tailnet-1", "Acme", 43123, false) };

        await Assert.ThrowsExceptionAsync<NetworkUnavailableException>(() =>
            new TailscaleNetworkProvider(tailnets).ResolveAsync(new ServerConfig
            {
                Host = "box.tailnet.ts.net",
                TailnetId = "tailnet-1",
            }));
    }

    [TestMethod]
    public async Task Resolver_ChoosesTailscaleOnlyForServersAssignedToATailnet()
    {
        var direct = new DirectNetworkProvider();
        var tailnets = new FakeTailnets();
        var resolver = new ServerNetworkResolver(direct, new TailscaleNetworkProvider(tailnets),
            new MemoryServers(new ServerConfig
            {
                Id = "s1",
                Host = "box.tailnet.ts.net",
                TailnetId = "tailnet-1",
            }));

        var routed = await resolver.ResolveAsync(new ServerConfig { Host = "box.tailnet.ts.net", TailnetId = "tailnet-1" });
        var plain = await resolver.ResolveAsync(new ServerConfig { Host = "public.example.com" });

        Assert.IsNotNull(routed.Proxy);
        Assert.IsNull(plain.Proxy);
    }

    private sealed class FakeResolver(ServerRoute route) : IServerNetworkResolver
    {
        public Task<ServerRoute> ResolveAsync(ServerConfig server, CancellationToken ct = default) => Task.FromResult(route);
        public Task<ServerRoute> ResolveForHostAsync(string host, CancellationToken ct = default) => Task.FromResult(route);
    }

    private sealed class FakeSsh : ShipRight.Shared.SshRunner.ISshRunner
    {
        public int Calls { get; private set; }
        public Task<int> RunAsync(string host, string username, string keyPath, string command,
            Func<string, Task>? onOutput = null, Func<string, Task>? onStderr = null, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(0);
        }
    }

    private sealed class FakeRouted : IServerRoutedSshRunner
    {
        public ServerRoute? Route { get; private set; }
        public Task<int> RunAsync(ServerRoute route, string username, string keyPath, string command,
            Func<string, Task>? onOutput = null, Func<string, Task>? onStderr = null, CancellationToken ct = default)
        {
            Route = route;
            return Task.FromResult(0);
        }
    }

    [TestMethod]
    public async Task NetworkAwareSshRunner_UsesTheProxyRunnerOnlyWhenTheRouteHasAProxy()
    {
        var directSsh = new FakeSsh();
        var routed = new FakeRouted();
        var direct = new NetworkAwareSshRunner(
            directSsh,
            new FakeResolver(new ServerRoute("box", 22)),
            routed);

        await direct.RunAsync("box", "ubuntu", "key", "uptime");

        Assert.AreEqual(1, directSsh.Calls);
        Assert.IsNull(routed.Route);

        var proxied = new NetworkAwareSshRunner(
            directSsh,
            new FakeResolver(new ServerRoute("box", 22, new ProxyRoute("127.0.0.1", 41000, ProxyKind.Socks5))),
            routed);

        await proxied.RunAsync("box", "ubuntu", "key", "uptime");

        Assert.AreEqual(new ProxyRoute("127.0.0.1", 41000, ProxyKind.Socks5), routed.Route?.Proxy);
        Assert.AreEqual(1, directSsh.Calls);
    }

    [TestMethod]
    public async Task Resolver_ByHost_LeavesUnknownHostsOnTheDirectPath()
    {
        var resolver = new ServerNetworkResolver(
            new DirectNetworkProvider(),
            new TailscaleNetworkProvider(new FakeTailnets()),
            new MemoryServers());

        var route = await resolver.ResolveForHostAsync("unknown.example.com");

        Assert.AreEqual("unknown.example.com", route.Host);
        Assert.IsNull(route.Proxy);
    }
}
