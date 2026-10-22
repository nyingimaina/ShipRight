using System.Text;
using ShipRight.Modules.RemoteHost;
using ShipRight.Shared.SshRunner;

namespace ShipRight.Modules.Servers;

/// <summary>Downloads the server-side checkpoint log (~/.shipright/checkpoints.log) as a text file.</summary>
public static class ServerLogRouter
{
    public static void MapServerLogRoutes(this WebApplication app)
    {
        app.MapGet("/api/servers/{serverId}/checkpoint-log", async (
            string serverId, IServerStore store, ISshRunner ssh, CancellationToken ct) =>
        {
            var server = await store.GetByIdAsync(serverId);
            if (server is null)
                return Results.NotFound(new { isError = true, message = $"Server '{serverId}' not found." });
            if (string.IsNullOrWhiteSpace(server.SshKeyPath))
                return Results.BadRequest(new { isError = true, message = "No SSH key configured." });

            var r = await new RemoteCheckpointLogReader(ssh).ReadAsync(server, ct);
            if (!r.Success)
                return Results.Json(new { isError = true, message = r.Error }, statusCode: 502);

            var safe = new string((server.Name ?? server.Host).Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
            return Results.File(Encoding.UTF8.GetBytes(r.Content), "text/plain; charset=utf-8", $"{safe}-checkpoints.log");
        });
    }
}
