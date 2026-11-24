namespace ShipRight.Modules.ServerNetwork;

public sealed record TailnetRuntime(string Id, string Name, int SocksPort, bool Online);

/// <summary>Owns tailscaled processes. The network provider only asks for a running proxy endpoint.</summary>
public interface ITailnetManager
{
    Task<TailnetRuntime> EnsureRunningAsync(string tailnetId, CancellationToken ct = default);
    Task<TailnetRuntime> GetStatusAsync(string tailnetId, CancellationToken ct = default);
    Task StopAsync(string tailnetId, CancellationToken ct = default);

    /// <summary>Stops the node and forgets its on-disk state. Used when the network is removed, not on a restart.</summary>
    Task RemoveAsync(string tailnetId, CancellationToken ct = default) => StopAsync(tailnetId, ct);
}
