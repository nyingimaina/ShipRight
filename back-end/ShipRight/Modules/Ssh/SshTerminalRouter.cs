using Serilog;
using ShipRight.Modules.Projects;
using ShipRight.Shared.Events;
using ShipRight.Shared.SshRunner;

namespace ShipRight.Modules.Ssh;

public static class SshTerminalRouter
{
    public static void MapSshTerminalRoutes(this WebApplication app)
    {
        // ── Execute arbitrary command via SSH ─────────────────────────────────
        app.MapPost("/api/projects/{id}/ssh/exec", async (
            string id, ExecRequest request,
            IProjectStore store, ISshRunner ssh, BuildEventBus bus) =>
        {
            var project = await store.GetByIdAsync(id);
            if (project is null)
                return Results.NotFound(new { isError = true, message = $"Project '{id}' not found." });

            if (string.IsNullOrWhiteSpace(request.Command))
                return Results.BadRequest(new { isError = true, message = "command is required." });

            if (string.IsNullOrWhiteSpace(project.Server.Host))
                return Results.BadRequest(new { isError = true, message = "Project has no server host configured." });

            var opId = Guid.NewGuid().ToString("N");
            bus.Register(opId);

            _ = Task.Run(async () =>
            {
                try
                {
                    Log.Information("SSH terminal exec for {ProjectId}: {Command}", id, request.Command);
                    var exit = await ssh.RunAsync(
                        project.Server.Host,
                        project.Server.Username,
                        project.Server.SshKeyPath,
                        request.Command,
                        onOutput: line => bus.EmitAsync(opId, "log", new { source = "stdout", line }),
                        onStderr: line => bus.EmitAsync(opId, "log", new { source = "stderr", line }));

                    await bus.EmitAsync(opId, "done", new { exitCode = exit });
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "SSH terminal exec failed for {ProjectId}", id);
                    await bus.EmitAsync(opId, "error", new { message = ex.Message });
                }
                finally
                {
                    bus.Complete(opId);
                }
            });

            return Results.Accepted($"/api/projects/{id}/ssh/ops/{opId}/stream",
                new { opId });
        });

        // ── SSE stream for terminal ops ───────────────────────────────────────
        app.MapGet("/api/projects/{id}/ssh/ops/{opId}/stream", async (
            string id, string opId,
            BuildEventBus eventBus, HttpContext http, CancellationToken ct) =>
        {
            http.Response.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers["X-Accel-Buffering"] = "no";
            http.Response.Headers.Connection = "keep-alive";

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

            var reader = eventBus.Subscribe(opId);
            try
            {
                await foreach (var payload in reader.ReadAllAsync(cts.Token))
                {
                    await http.Response.WriteAsync($"data: {payload}\n\n", cts.Token);
                    await http.Response.Body.FlushAsync(cts.Token);
                }
            }
            catch (OperationCanceledException) { /* client disconnected */ }
            finally
            {
                await cts.CancelAsync();
                await heartbeat;
                eventBus.Unsubscribe(opId, reader);
            }
        });
    }

    private record ExecRequest(string Command);
}
