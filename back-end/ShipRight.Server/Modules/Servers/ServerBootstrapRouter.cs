using System.Diagnostics;
using Serilog;
using ShipRight.Modules.Projects;
using ShipRight.Modules.RemoteHost;
using ShipRight.Shared.Events;
using ShipRight.Shared.SshRunner;

namespace ShipRight.Modules.Servers;

/// <summary>
/// "Prepare server" and "Deploy" actions for a standalone server. Both return 202 with an opId;
/// output streams from the existing /api/servers/{id}/ssh/ops/{opId}/stream SSE endpoint.
/// Search the JSON log for the opId to follow one action end to end.
/// </summary>
public static class ServerBootstrapRouter
{
    public static void MapServerBootstrapRoutes(this WebApplication app)
    {
        app.MapPost("/api/servers/{serverId}/bootstrap", (
            string serverId, IServerStore store, ISshRunner ssh, BuildEventBus bus) =>
            StartAsync("bootstrap", serverId, store, bus, async (server, onLine, ct) =>
            {
                var r = await new RemoteServerBootstrapper(ssh).RunAsync(server, onLine, ct);
                return (r.Success, r.Message);
            }));

        app.MapPost("/api/servers/{serverId}/deploy", (
            string serverId, IServerStore store, ISshRunner ssh, BuildEventBus bus) =>
            StartAsync("deploy", serverId, store, bus, async (server, onLine, ct) =>
            {
                var deployer = new RemoteServerDeployer(ssh, new RemoteHealthChecker(ssh));
                var r = await deployer.DeployAsync(server, onLine, ct);
                return (r.Success, r.Message);
            }));
    }

    private static async Task<IResult> StartAsync(
        string actionName, string serverId, IServerStore store, BuildEventBus bus,
        Func<ServerConfig, Func<string, Task>, CancellationToken, Task<(bool Success, string Message)>> action)
    {
        var server = await store.GetByIdAsync(serverId);
        if (server is null)
        {
            Log.Warning("Server action '{Action}' rejected: server {ServerId} not found", actionName, serverId);
            return Results.NotFound(new { isError = true, message = $"Server '{serverId}' not found." });
        }
        if (string.IsNullOrWhiteSpace(server.SshKeyPath))
        {
            Log.Warning("Server action '{Action}' rejected: server {ServerId} has no SSH key", actionName, serverId);
            return Results.BadRequest(new { isError = true, message = "No SSH key configured — generate one in the Server settings." });
        }

        var opId = Guid.NewGuid().ToString("N");
        bus.Register(opId);
        Log.Information("Server action '{Action}' accepted: server={ServerId} host={Host} opId={OpId}",
            actionName, serverId, server.Host, opId);

        _ = Task.Run(async () =>
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var (success, message) = await action(
                    server, line => bus.EmitAsync(opId, "log", new { source = "stdout", line }), CancellationToken.None);

                if (success)
                    Log.Information("Server action '{Action}' succeeded: server={ServerId} opId={OpId} in {ElapsedMs}ms",
                        actionName, serverId, opId, sw.ElapsedMilliseconds);
                else
                    Log.Error("Server action '{Action}' failed: server={ServerId} opId={OpId} in {ElapsedMs}ms — {Message}",
                        actionName, serverId, opId, sw.ElapsedMilliseconds, message);

                await bus.EmitAsync(opId, "done", new { exitCode = success ? 0 : 1, message });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Server action '{Action}' crashed: server={ServerId} opId={OpId} after {ElapsedMs}ms",
                    actionName, serverId, opId, sw.ElapsedMilliseconds);
                await bus.EmitAsync(opId, "error", new { message = ex.Message });
            }
            finally
            {
                bus.Complete(opId);
            }
        });

        return Results.Accepted($"/api/servers/{serverId}/ssh/ops/{opId}/stream", new { opId });
    }
}
