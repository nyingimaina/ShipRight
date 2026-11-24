using ShipRight.Modules.Projects;

namespace ShipRight.Modules.ServerNetwork;

public enum ProxyKind { Socks5 }

public sealed record ProxyRoute(string Host, int Port, ProxyKind Kind);

public sealed record ServerRoute(string Host, int Port, ProxyRoute? Proxy = null);

public sealed class NetworkUnavailableException(string message) : InvalidOperationException(message);

/// <summary>Answers one question: for this server, where should clients connect?</summary>
public interface IServerNetworkProvider
{
    Task<ServerRoute> ResolveAsync(ServerConfig server, CancellationToken ct = default);
}

/// <summary>Shared routing surface. Only this type chooses a provider; callers consume a route.</summary>
public interface IServerNetworkResolver
{
    Task<ServerRoute> ResolveAsync(ServerConfig server, CancellationToken ct = default);
    Task<ServerRoute> ResolveForHostAsync(string host, CancellationToken ct = default);
}

public interface IServerRoutedSshRunner
{
    Task<int> RunAsync(ServerRoute route, string username, string keyPath, string command,
        Func<string, Task>? onOutput = null, Func<string, Task>? onStderr = null, CancellationToken ct = default);
}

public sealed class DirectNetworkProvider : IServerNetworkProvider
{
    public Task<ServerRoute> ResolveAsync(ServerConfig server, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(server.Host))
            throw new NetworkUnavailableException("This server has no host.");

        return Task.FromResult(new ServerRoute(server.Host, 22));
    }
}

public sealed class TailscaleNetworkProvider(ITailnetManager tailnets) : IServerNetworkProvider
{
    public async Task<ServerRoute> ResolveAsync(ServerConfig server, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(server.Host))
            throw new NetworkUnavailableException("This server has no host.");
        if (string.IsNullOrWhiteSpace(server.TailnetId))
            throw new NetworkUnavailableException("This server is not assigned to a Tailscale network.");

        var runtime = await tailnets.EnsureRunningAsync(server.TailnetId, ct);
        if (!runtime.Online)
            throw new NetworkUnavailableException($"Tailscale network '{runtime.Name}' is not online.");

        return new ServerRoute(server.Host, 22, new ProxyRoute("127.0.0.1", runtime.SocksPort, ProxyKind.Socks5));
    }
}

public sealed class ServerNetworkResolver(
    DirectNetworkProvider direct,
    TailscaleNetworkProvider tailscale,
    Modules.Servers.IServerStore servers) : IServerNetworkResolver
{
    public Task<ServerRoute> ResolveAsync(ServerConfig server, CancellationToken ct = default) =>
        string.IsNullOrWhiteSpace(server.TailnetId)
            ? direct.ResolveAsync(server, ct)
            : tailscale.ResolveAsync(server, ct);

    public async Task<ServerRoute> ResolveForHostAsync(string host, CancellationToken ct = default)
    {
        var server = (await servers.GetAllAsync())
            .FirstOrDefault(s => string.Equals(s.Host, host, StringComparison.OrdinalIgnoreCase));

        return server is null
            ? await direct.ResolveAsync(new ServerConfig { Host = host }, ct)
            : await ResolveAsync(server, ct);
    }
}
