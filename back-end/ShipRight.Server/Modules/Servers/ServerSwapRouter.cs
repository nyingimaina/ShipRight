using ShipRight.Modules.RemoteHost;
using ShipRight.Shared.SshRunner;

namespace ShipRight.Modules.Servers;

/// <summary>POST /api/servers/{id}/swap — creates a swap file on a server that has none (Admin, like other server changes).</summary>
public static class ServerSwapRouter
{
    public static void MapServerSwapRoutes(this WebApplication app)
    {
        app.MapPost("/api/servers/{serverId}/swap", async (
            string serverId, SwapRequest? body, IServerStore store, ISshRunner ssh, CancellationToken ct) =>
        {
            var server = await store.GetByIdAsync(serverId);
            if (server is null)
                return Results.NotFound(new { isError = true, message = $"Server '{serverId}' not found." });
            if (string.IsNullOrWhiteSpace(server.SshKeyPath))
                return Results.BadRequest(new { isError = true, message = "No SSH key configured." });

            var gb = body?.SizeGb ?? 2;
            if (gb is < 1 or > 8)
                return Results.BadRequest(new { isError = true, message = "Swap size must be between 1 and 8 GB." });

            try
            {
                var r = await new RemoteSwap(ssh).CreateAsync(server, gb, ct);
                Serilog.Log.Information("Swap creation on {Host} ({Gb} GB): success={Success}", server.Host, gb, r.Success);
                return Results.Ok(new { success = r.Success, output = r.Output });
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "Swap creation failed on {Host}", server.Host);
                return Results.Json(new { isError = true, message = ex.Message }, statusCode: 502);
            }
        });
    }

    private record SwapRequest(int? SizeGb);
}
