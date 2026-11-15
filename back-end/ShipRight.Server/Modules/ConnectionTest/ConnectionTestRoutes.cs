using Serilog;
using ShipRight.Modules.Servers;
using ShipRight.Shared.SshRunner;

namespace ShipRight.Modules.ConnectionTest;

public sealed record CheckView(string Id, string Title, string Status, string Message, string? Fix, string? Command);

public sealed record ConnectionTestView(bool Ready, string Summary, IReadOnlyList<CheckView> Items);

/// <summary>
/// POST /api/servers/{serverId}/connection-test. The connection (host, user, key) comes from the stored server, never
/// from the request, so callers cannot point ShipRight at arbitrary hosts or keys. A failing check is still a normal
/// answer (200) with the failure inside, so the screen can explain it.
/// </summary>
public static class ConnectionTestRoutes
{
    public static void MapConnectionTestRoutes(this WebApplication app) =>
        app.MapPost("/api/servers/{serverId}/connection-test",
            (string serverId, IServerStore store, ISshRunner ssh, CancellationToken ct) =>
                HandleAsync(serverId, store, ssh, new DiagnosticTcpProbe(new TcpProbe()), ct));

    internal static async Task<IResult> HandleAsync(string serverId, IServerStore store, ISshRunner ssh, ITcpProbe tcp, CancellationToken ct)
    {
        var server = await store.GetByIdAsync(serverId);
        if (server is null)
            return Results.NotFound(new { isError = true, message = $"Server '{serverId}' not found." });

        Log.Information("Connection test: server={ServerId} host={Host}", server.Id, server.Host);
        ConnectionTestResult result;
        try
        {
            result = await new TailscaleSshAwareTester(new ConnectionTester(ssh, tcp)).TestAsync(server, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Error(ex, "Connection test crashed for server {ServerId}", serverId);
            result = new ConnectionTestResult([new CheckItem("internal", "Connection test", CheckStatus.Fail,
                $"The test itself failed: {ex.Message}", "Try again. If it keeps happening, check the ShipRight log.")]);
        }

        Log.Information("Connection test done: server={ServerId} ready={Ready}", server.Id, result.Ready);
        return Results.Ok(new ConnectionTestView(
            result.Ready, result.Summary,
            result.Items.Select(i => new CheckView(i.Id, i.Title, i.Status.ToString().ToLowerInvariant(), i.Message, i.Fix, i.Command)).ToList()));
    }
}
