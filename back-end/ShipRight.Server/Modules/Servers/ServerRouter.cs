using System.Text.Json;
using Serilog;
using ShipRight.Modules.Database;
using ShipRight.Modules.Projects;
using ShipRight.Modules.RemoteHost;
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

        // ── Monitoring metrics ────────────────────────────────────────────────

        app.MapGet("/api/servers/{serverId}/metrics", async (
            string serverId, IServerStore store, IMonitoringProvider monitoring, CancellationToken ct) =>
        {
            var server = await store.GetByIdAsync(serverId);
            if (server is null)
                return Results.NotFound(new { isError = true, message = $"Server '{serverId}' not found." });

            if (string.IsNullOrWhiteSpace(server.SshKeyPath))
                return Results.Ok(SystemMetrics.Unreachable(
                    "No SSH key configured — generate one in the Server settings."));

            var config = new RemoteHostConfig(server.Host, 22, server.Username);
            var metrics = await monitoring.GetMetricsAsync(config, server.SshKeyPath, ct);
            return Results.Ok(metrics);
        });

        // ── SSH exec (standalone, no project dependency) ──────────────────────

        app.MapPost("/api/servers/{serverId}/ssh/exec", async (
            string serverId, SshExecRequest request,
            IServerStore store, ISshRunner ssh, BuildEventBus bus) =>
        {
            var server = await store.GetByIdAsync(serverId);
            if (server is null)
                return Results.NotFound(new { isError = true, message = $"Server '{serverId}' not found." });

            var trimmed = request.Command?.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
                return Results.BadRequest(new { isError = true, message = "command is required." });

            var opId = Guid.NewGuid().ToString("N");
            bus.Register(opId);
            var sessionKey = $"server:{serverId}";
            var cwd = SshSessionStore.GetCwd(sessionKey);

            if (trimmed == "pwd")
            {
                _ = Task.Run(async () =>
                {
                    await bus.EmitAsync(opId, "cwd", new { cwd });
                    await bus.EmitAsync(opId, "log", new { source = "stdout", line = cwd });
                    await bus.EmitAsync(opId, "done", new { exitCode = 0, cwd });
                    bus.Complete(opId);
                });
                return Results.Accepted($"/api/servers/{serverId}/ssh/ops/{opId}/stream", new { opId });
            }

            var isCd = trimmed == "cd" || trimmed.StartsWith("cd ");
            string? actualCommand = null;
            string? newCwd = null;

            if (isCd)
            {
                var parts = trimmed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                var target = parts.Length > 1 ? parts[1].Trim() : "~";

                if (target == "~" || target.StartsWith('/'))
                {
                    newCwd = target;
                }
                else
                {
                    actualCommand = $"cd {target} && pwd";
                }
            }
            else
            {
                actualCommand = $"cd {cwd} && {request.Command}";
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    if (isCd && newCwd != null)
                    {
                        SshSessionStore.SetCwd(sessionKey, newCwd);
                        await bus.EmitAsync(opId, "cwd", new { cwd = newCwd });
                        await bus.EmitAsync(opId, "done", new { exitCode = 0, cwd = newCwd });
                        return;
                    }

                    if (isCd)
                    {
                        var captured = new List<string>();
                        var exit = await ssh.RunAsync(
                            server.Host, server.Username, server.SshKeyPath,
                            actualCommand!,
                            onOutput: line => { captured.Add(line); return Task.CompletedTask; },
                            onStderr: line => bus.EmitAsync(opId, "log", new { source = "stderr", line }));

                        var resolved = captured.LastOrDefault(l => !string.IsNullOrWhiteSpace(l));
                        newCwd = !string.IsNullOrWhiteSpace(resolved) ? resolved : cwd;
                        SshSessionStore.SetCwd(sessionKey, newCwd);
                        await bus.EmitAsync(opId, "cwd", new { cwd = newCwd });
                        await bus.EmitAsync(opId, "done", new { exitCode = exit, cwd = newCwd });
                    }
                    else
                    {
                        var exit = await ssh.RunAsync(
                            server.Host, server.Username, server.SshKeyPath,
                            actualCommand!,
                            onOutput: line => bus.EmitAsync(opId, "log", new { source = "stdout", line }),
                            onStderr: line => bus.EmitAsync(opId, "log", new { source = "stderr", line }));

                        await bus.EmitAsync(opId, "done", new { exitCode = exit, cwd = SshSessionStore.GetCwd(sessionKey) });
                    }
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

        // ── DB backup (standalone) ───────────────────────────────────────────

        app.MapPost("/api/servers/{serverId}/db/backup", async (
            string serverId, DatabaseConfig dbConfig,
            IServerStore store, DatabaseOrchestrator orchestrator, BuildEventBus bus) =>
        {
            var server = await store.GetByIdAsync(serverId);
            if (server is null)
                return Results.NotFound(new { isError = true, message = $"Server '{serverId}' not found." });

            if (string.IsNullOrWhiteSpace(dbConfig.DatabaseName))
                return Results.BadRequest(new { isError = true, message = "databaseName is required." });

            var project = ProjectFromServer(server, dbConfig);
            var opId = Guid.NewGuid().ToString("N");
            bus.Register(opId);
            _ = Task.Run(() => orchestrator.BackupAsync(project, opId, CancellationToken.None));
            return Results.Accepted($"/api/servers/{serverId}/db/ops/{opId}/stream",
                new { opId, message = "Backup started." });
        });

        // ── DB restore (standalone) ──────────────────────────────────────────

        app.MapPost("/api/servers/{serverId}/db/restore", async (
            string serverId, StandaloneRestoreRequest request,
            IServerStore store, DatabaseOrchestrator orchestrator, BuildEventBus bus) =>
        {
            var server = await store.GetByIdAsync(serverId);
            if (server is null)
                return Results.NotFound(new { isError = true, message = $"Server '{serverId}' not found." });

            if (string.IsNullOrWhiteSpace(request.BackupFile))
                return Results.BadRequest(new { isError = true, message = "backupFile is required." });
            if (!File.Exists(request.BackupFile))
                return Results.BadRequest(new { isError = true, message = $"Backup file not found: {request.BackupFile}" });

            var project = ProjectFromServer(server, request.DbConfig);
            var opId = Guid.NewGuid().ToString("N");
            bus.Register(opId);
            _ = Task.Run(() => orchestrator.RestoreAsync(project, opId, request.BackupFile, CancellationToken.None));
            return Results.Accepted($"/api/servers/{serverId}/db/ops/{opId}/stream",
                new { opId, message = "Restore started." });
        });

        // ── DB query from file (standalone) ──────────────────────────────────

        app.MapPost("/api/servers/{serverId}/db/query", async (
            string serverId, StandaloneQueryRequest request,
            IServerStore store, DatabaseOrchestrator orchestrator, BuildEventBus bus) =>
        {
            var server = await store.GetByIdAsync(serverId);
            if (server is null)
                return Results.NotFound(new { isError = true, message = $"Server '{serverId}' not found." });

            if (string.IsNullOrWhiteSpace(request.LocalSqlPath))
                return Results.BadRequest(new { isError = true, message = "localSqlPath is required." });
            if (!File.Exists(request.LocalSqlPath))
                return Results.BadRequest(new { isError = true, message = $"SQL file not found: {request.LocalSqlPath}" });

            var project = ProjectFromServer(server, request.DbConfig);
            var opId = Guid.NewGuid().ToString("N");
            bus.Register(opId);
            _ = Task.Run(() => orchestrator.QueryAsync(project, opId, request.LocalSqlPath, CancellationToken.None));
            return Results.Accepted($"/api/servers/{serverId}/db/ops/{opId}/stream",
                new { opId, message = "Query started." });
        });

        // ── DB raw SQL query (standalone) ────────────────────────────────────

        app.MapPost("/api/servers/{serverId}/db/query-raw", async (
            string serverId, StandaloneQueryRawRequest request,
            IServerStore store, DatabaseOrchestrator orchestrator, BuildEventBus bus) =>
        {
            var server = await store.GetByIdAsync(serverId);
            if (server is null)
                return Results.NotFound(new { isError = true, message = $"Server '{serverId}' not found." });

            if (string.IsNullOrWhiteSpace(request.Sql))
                return Results.BadRequest(new { isError = true, message = "sql is required." });

            var project = ProjectFromServer(server, request.DbConfig);
            var opId = Guid.NewGuid().ToString("N");
            bus.Register(opId);
            _ = Task.Run(() => orchestrator.QueryRawAsync(project, opId, request.Sql, CancellationToken.None));
            return Results.Accepted($"/api/servers/{serverId}/db/ops/{opId}/stream",
                new { opId, message = "Query started." });
        });

        // ── List backups (standalone) ────────────────────────────────────────

        app.MapGet("/api/servers/{serverId}/db/backups", async (
            string serverId, string? container, string? database,
            IServerStore store, DatabaseOrchestrator orchestrator) =>
        {
            var server = await store.GetByIdAsync(serverId);
            if (server is null)
                return Results.NotFound(new { isError = true, message = $"Server '{serverId}' not found." });

            var projectId = $"standalone-{serverId}-{container ?? ""}-{database ?? ""}";
            var backups = orchestrator.ListBackups(projectId);
            return Results.Ok(backups);
        });

        // ── Delete backup (standalone) ───────────────────────────────────────

        app.MapDelete("/api/servers/{serverId}/db/backups", async (
            string serverId, string file, string container, string database, string provider, string rootUser, int? backupRetainCount,
            IServerStore store, DatabaseOrchestrator orchestrator) =>
        {
            var server = await store.GetByIdAsync(serverId);
            if (server is null)
                return Results.NotFound(new { isError = true, message = $"Server '{serverId}' not found." });

            if (string.IsNullOrWhiteSpace(file))
                return Results.BadRequest(new { isError = true, message = "file query param is required." });

            var dbConfig = new DatabaseConfig
            {
                ContainerName = container,
                DatabaseName = database,
                Provider = Enum.TryParse<DbProviderType>(provider, ignoreCase: true, out var pt) ? pt : DbProviderType.MariaDb,
                RootUser = rootUser,
                BackupRetainCount = backupRetainCount ?? 10,
            };
            var project = ProjectFromServer(server, dbConfig);
            try
            {
                orchestrator.DeleteBackup(project.Id, file);
                return Results.Ok(new { message = "Backup deleted." });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { isError = true, message = ex.Message });
            }
        });

        // ── Open backup folder in Explorer (standalone) ───────────────────────

        app.MapGet("/api/servers/{serverId}/db/backups/open-folder", async (
            string serverId, string file, string? container, string? database,
            IServerStore store, DatabaseOrchestrator orchestrator) =>
        {
            var server = await store.GetByIdAsync(serverId);
            if (server is null)
                return Results.NotFound(new { isError = true, message = $"Server '{serverId}' not found." });

            var projectId = $"standalone-{serverId}-{container ?? ""}-{database ?? ""}";
            var filePath = orchestrator.ResolveBackupFile(projectId, file);
            if (filePath is null)
                return Results.NotFound(new { isError = true, message = "Backup file not found." });

            global::System.Diagnostics.Process.Start(new global::System.Diagnostics.ProcessStartInfo(
                "explorer.exe", $"/select,\"{filePath}\"") { UseShellExecute = true });

            return Results.Ok(new { message = "Opened." });
        });

        // ── Active operation query (standalone) ───────────────────────────────

        app.MapGet("/api/servers/{serverId}/db/ops/active", (
            string serverId, string? container, string? database,
            DatabaseOrchestrator orchestrator) =>
        {
            var projectId = $"standalone-{serverId}-{container ?? ""}-{database ?? ""}";
            var opId = orchestrator.GetActiveOpId(projectId);
            return opId is null
                ? Results.NoContent()
                : Results.Ok(new { opId });
        });

        // ── SSE stream for standalone DB ops ─────────────────────────────────

        app.MapGet("/api/servers/{serverId}/db/ops/{opId}/stream", SseStreamHandler);

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

    private static ProjectConfig ProjectFromServer(ServerConfig server, DatabaseConfig? dbConfig = null)
    {
        var key = dbConfig is not null
            ? $"standalone-{server.Id}-{dbConfig.ContainerName}-{dbConfig.DatabaseName}"
            : $"standalone-{server.Id}";
        return new()
        {
            Id = key,
            Server = server,
            Database = dbConfig,
        };
    }

    private record SshExecRequest(string Command);
    private record StandaloneRestoreRequest(DatabaseConfig DbConfig, string BackupFile);
    private record StandaloneQueryRequest(DatabaseConfig DbConfig, string LocalSqlPath);
    private record StandaloneQueryRawRequest(DatabaseConfig DbConfig, string Sql);
}
