using System.Text.Json;
using Serilog;
using ShipRight.Modules.Database;
using ShipRight.Modules.Projects;
using ShipRight.Shared.Events;
using ShipRight.Shared.SshRunner;

namespace ShipRight.Modules.Servers;

public static class ServerRouter
{
    public static void MapServerRoutes(this WebApplication app)
    {
        // ── CRUD ──────────────────────────────────────────────────────────────

        app.MapGet("/api/servers", async (IServerStore store) =>
        {
            var servers = await store.GetAllAsync();
            return Results.Ok(servers);
        });

        app.MapGet("/api/servers/{serverId}", async (string serverId, IServerStore store) =>
        {
            var server = await store.GetByIdAsync(serverId);
            return server is not null ? Results.Ok(server) : Results.NotFound();
        });

        app.MapPost("/api/servers", async (ServerConfig server, IServerStore store) =>
        {
            if (string.IsNullOrWhiteSpace(server.Host))
                return Results.BadRequest(new { isError = true, field = "host", message = "Host is required." });

            var saved = server with { Id = string.IsNullOrEmpty(server.Id) ? Guid.NewGuid().ToString("N") : server.Id };
            await store.SaveAsync(saved);
            return Results.Created($"/api/servers/{saved.Id}", saved);
        });

        app.MapPut("/api/servers/{serverId}", async (string serverId, ServerConfig server, IServerStore store) =>
        {
            var existing = await store.GetByIdAsync(serverId);
            if (existing is null)
                return Results.NotFound(new { isError = true, message = $"Server '{serverId}' not found." });

            var saved = server with { Id = serverId };
            await store.SaveAsync(saved);
            return Results.Ok(saved);
        });

        app.MapDelete("/api/servers/{serverId}", async (string serverId, IServerStore store) =>
        {
            var existing = await store.GetByIdAsync(serverId);
            if (existing is null)
                return Results.NotFound(new { isError = true, message = $"Server '{serverId}' not found." });

            await store.DeleteAsync(serverId);
            return Results.Ok(new { message = "Server deleted." });
        });

        // ── SSH exec (standalone, no project dependency) ──────────────────────

        app.MapPost("/api/servers/{serverId}/ssh/exec", async (
            string serverId, SshExecRequest request,
            IServerStore store, ISshRunner ssh, BuildEventBus bus) =>
        {
            var server = await store.GetByIdAsync(serverId);
            if (server is null)
                return Results.NotFound(new { isError = true, message = $"Server '{serverId}' not found." });

            if (string.IsNullOrWhiteSpace(request.Command))
                return Results.BadRequest(new { isError = true, message = "command is required." });

            var opId = Guid.NewGuid().ToString("N");
            bus.Register(opId);

            _ = Task.Run(async () =>
            {
                try
                {
                    var exit = await ssh.RunAsync(
                        server.Host, server.Username, server.SshKeyPath,
                        request.Command,
                        onOutput: line => bus.EmitAsync(opId, "log", new { source = "stdout", line }),
                        onStderr: line => bus.EmitAsync(opId, "log", new { source = "stderr", line }));

                    await bus.EmitAsync(opId, "done", new { exitCode = exit });
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "SSH exec failed for server {ServerId}", serverId);
                    await bus.EmitAsync(opId, "error", new { message = ex.Message });
                }
                finally
                {
                    bus.Complete(opId);
                }
            });

            return Results.Accepted($"/api/servers/{serverId}/ssh/ops/{opId}/stream",
                new { opId });
        });

        // ── SSE stream for standalone SSH ops ─────────────────────────────────

        app.MapGet("/api/servers/{serverId}/ssh/ops/{opId}/stream", SseStreamHandler);

        // ── DB container discovery (standalone) ───────────────────────────────

        app.MapGet("/api/servers/{serverId}/db/containers", async (
            string serverId, IServerStore store, DatabaseOrchestrator db) =>
        {
            var server = await store.GetByIdAsync(serverId);
            if (server is null)
                return Results.NotFound(new { isError = true, message = $"Server '{serverId}' not found." });

            try
            {
                var containers = await db.ListContainersAsync(ProjectFromServer(server));
                return Results.Ok(containers);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "ListContainers failed for server {ServerId}", serverId);
                return Results.BadRequest(new { isError = true, message = ex.Message });
            }
        });

        // ── DB database discovery (standalone) ────────────────────────────────

        app.MapGet("/api/servers/{serverId}/db/databases", async (
            string serverId, string container, string provider,
            IServerStore store, DatabaseOrchestrator db) =>
        {
            var server = await store.GetByIdAsync(serverId);
            if (server is null)
                return Results.NotFound(new { isError = true, message = $"Server '{serverId}' not found." });

            if (!Enum.TryParse<DbProviderType>(provider, ignoreCase: true, out var providerType))
                return Results.BadRequest(new { isError = true, message = $"Unknown provider '{provider}'." });

            try
            {
                var databases = await db.ListDatabasesAsync(ProjectFromServer(server), container, providerType);
                return Results.Ok(databases);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "ListDatabases failed for server {ServerId}", serverId);
                return Results.BadRequest(new { isError = true, message = ex.Message });
            }
        });

        // ── Container logs stream (standalone) ────────────────────────────────

        app.MapGet("/api/servers/{serverId}/container-logs/stream", async (
            string serverId, string container, int? tail,
            IServerStore store, ISshRunner ssh, HttpContext http, CancellationToken ct) =>
        {
            var server = await store.GetByIdAsync(serverId);
            if (server is null)
            {
                http.Response.StatusCode = 404;
                await http.Response.WriteAsJsonAsync(new { isError = true, message = $"Server '{serverId}' not found." });
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

            Log.Information("Container log stream started: server={ServerId} container={Container}", serverId, container);

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
                    server.Host, server.Username, server.SshKeyPath,
                    cmd,
                    onOutput: async line =>
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
                Log.Error(ex, "Container log stream failed: server={ServerId} container={Container}", serverId, container);
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

    private static async Task SseStreamHandler(
        string serverId, string opId,
        BuildEventBus eventBus, HttpContext http, CancellationToken ct)
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
    }

    private static ProjectConfig ProjectFromServer(ServerConfig server) =>
        new() { Server = server };

    private record SshExecRequest(string Command);
}
