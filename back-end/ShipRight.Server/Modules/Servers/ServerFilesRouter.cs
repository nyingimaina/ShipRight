using Microsoft.AspNetCore.Http.Features;
using Serilog;
using ShipRight.Modules.Projects;
using ShipRight.Modules.RemoteHost;
using ShipRight.Shared.SshRunner;

namespace ShipRight.Modules.Servers;

/// <summary>
/// Files tab API for a saved server. The connection (host, user, key) always comes from the stored
/// server — never from the request — so callers cannot point ShipRight at arbitrary hosts or keys.
/// Upload sends the raw file as the request body (?path=<dir>&amp;name=<file>) to avoid multipart limits.
/// </summary>
public static class ServerFilesRouter
{
    public static void MapServerFilesRoutes(this WebApplication app)
    {
        var g = app.MapGroup("/api/servers/{serverId}/files");

        g.MapGet("", (string serverId, string? path, bool? showHidden, IServerStore store, KnownHostsStore kh, ISshRunner ssh, CancellationToken ct) =>
            Run(serverId, store, kh, ssh, async (svc, s) => Results.Ok(await svc.ListAsync(s, path, showHidden ?? false, ct))));

        g.MapGet("/read", (string serverId, string path, IServerStore store, KnownHostsStore kh, ISshRunner ssh, CancellationToken ct) =>
            Run(serverId, store, kh, ssh, async (svc, s) => Results.Text(await svc.ReadTextAsync(s, path, ct), "text/plain; charset=utf-8")));

        g.MapGet("/download", (string serverId, string path, IServerStore store, KnownHostsStore kh, ISshRunner ssh, CancellationToken ct) =>
            Run(serverId, store, kh, ssh, async (svc, s) =>
            {
                var name = await svc.DownloadNameAsync(s, path, ct);   // 404s before any bytes are sent
                Log.Information("File download: server={ServerId} path={Path}", s.Id, path);
                return Results.Stream(stream => svc.DownloadAsync(s, path, stream, ct), "application/octet-stream", name);
            }));

        g.MapPost("/upload", (string serverId, string path, string name, HttpRequest req, IServerStore store, KnownHostsStore kh, ISshRunner ssh, CancellationToken ct) =>
            Run(serverId, store, kh, ssh, async (svc, s) =>
            {
                req.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize = RemoteFileService.MaxUploadBytes + 1;
                await svc.UploadAsync(s, path, name, req.Body, ct: ct);
                Log.Information("File upload: server={ServerId} dir={Dir} name={Name}", s.Id, path, name);
                return Results.Ok(new { uploaded = name });
            }));

        g.MapDelete("", (string serverId, string path, IServerStore store, KnownHostsStore kh, ISshRunner ssh, CancellationToken ct) =>
            Run(serverId, store, kh, ssh, async (svc, s) =>
            {
                await svc.DeleteAsync(s, path, ct);
                return Results.Ok(new { deleted = path });
            }));
    }

    private static async Task<IResult> Run(
        string serverId, IServerStore store, KnownHostsStore knownHosts, ISshRunner ssh,
        Func<RemoteFileService, ServerConfig, Task<IResult>> action)
    {
        var server = await store.GetByIdAsync(serverId);
        if (server is null)
            return Results.NotFound(new { isError = true, message = $"Server '{serverId}' not found." });
        if (string.IsNullOrWhiteSpace(server.SshKeyPath))
            return Results.BadRequest(new { isError = true, message = "No SSH key configured — generate one in the Settings tab." });

        try
        {
            return await action(new RemoteFileService(new SftpRemoteFileClient(knownHosts), ssh), server);
        }
        catch (RemoteFileException ex)
        {
            return Results.Json(new { isError = true, message = ex.Message }, statusCode: ex.StatusCode);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Files action failed for server {ServerId}", serverId);
            return Results.Json(new { isError = true, message = $"SSH error: {ex.Message}" }, statusCode: 502);
        }
    }
}
