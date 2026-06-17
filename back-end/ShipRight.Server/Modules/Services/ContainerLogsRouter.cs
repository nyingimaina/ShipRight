using System.Text.Json;
using Serilog;
using ShipRight.Modules.Projects;
using ShipRight.Shared.SshRunner;

namespace ShipRight.Modules.Services;

public static class ContainerLogsRouter
{
    public static void MapContainerLogRoutes(this WebApplication app)
    {
        app.MapGet("/api/projects/{id}/container-logs/stream", async (
            string id, string container, int? tail,
            IProjectStore store, ISshRunner ssh, HttpContext http, CancellationToken ct) =>
        {
            var project = await store.GetByIdAsync(id);
            if (project is null)
            {
                http.Response.StatusCode = 404;
                await http.Response.WriteAsJsonAsync(new { isError = true, message = $"Project '{id}' not found." });
                return;
            }

            if (string.IsNullOrWhiteSpace(container))
            {
                http.Response.StatusCode = 400;
                await http.Response.WriteAsJsonAsync(new { isError = true, message = "container query param is required." });
                return;
            }

            http.Response.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers["X-Accel-Buffering"] = "no";
            http.Response.Headers.Connection = "keep-alive";

            var tailLines = Math.Clamp(tail ?? 200, 1, 2000);
            var cmd = $"docker logs --tail {tailLines} --follow {container}";

            Log.Information("Container log stream started: project={ProjectId} container={Container}", id, container);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var heartbeat = Task.Run(async () =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(15), cts.Token);
                        await http.Response.WriteAsync(": heartbeat\n\n", cts.Token);
                        await http.Response.Body.FlushAsync(cts.Token);
                    }
                    catch (OperationCanceledException) { break; }
                }
            });

            try
            {
                await ssh.RunAsync(
                    project.Server.Host,
                    project.Server.Username,
                    project.Server.SshKeyPath,
                    cmd,
                    async line =>
                    {
                        var payload = JsonSerializer.Serialize(new { line });
                        await http.Response.WriteAsync($"data: {payload}\n\n", cts.Token);
                        await http.Response.Body.FlushAsync(cts.Token);
                    },
                    ct: cts.Token);
            }
            catch (OperationCanceledException) { /* client disconnected */ }
            catch (Exception ex)
            {
                Log.Error(ex, "Container log stream failed: project={ProjectId} container={Container}", id, container);
                try
                {
                    var errPayload = JsonSerializer.Serialize(new { line = $"[error] {ex.Message}" });
                    await http.Response.WriteAsync($"data: {errPayload}\n\n", cts.Token);
                    await http.Response.Body.FlushAsync(cts.Token);
                }
                catch { /* response already gone */ }
            }
            finally
            {
                await cts.CancelAsync();
                await heartbeat;
            }
        });
    }
}
