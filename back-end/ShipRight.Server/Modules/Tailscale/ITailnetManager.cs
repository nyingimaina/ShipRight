namespace ShipRight.Modules.ServerNetwork;

public sealed record TailnetRuntime(string Id, string Name, int SocksPort, bool Online);

/// <summary>Owns tailscaled processes. The network provider only asks for a running proxy endpoint.</summary>
public interface ITailnetManager
{
    Task<TailnetRuntime> EnsureRunningAsync(string tailnetId, CancellationToken ct = default);
    Task<TailnetRuntime> GetStatusAsync(string tailnetId, CancellationToken ct = default);
    Task StopAsync(string tailnetId, CancellationToken ct = default);
}
