using Serilog;
using ShipRight.Modules.Projects;
using ShipRight.Shared.Events;

namespace ShipRight.Modules.Database;

public static class DatabaseRouter
{
    public static void MapDatabaseRoutes(this WebApplication app)
    {
        // ── Discovery ─────────────────────────────────────────────────────────

        app.MapGet("/api/projects/{id}/db/containers", async (
            string id, IProjectStore store, DatabaseOrchestrator orchestrator) =>
        {
            var project = await store.GetByIdAsync(id);
            if (project is null)
                return Results.NotFound(new { isError = true, message = $"Project '{id}' not found." });

            try
            {
                var containers = await orchestrator.ListContainersAsync(project);
                return Results.Ok(containers);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "ListContainers failed for {ProjectId}", id);
                return Results.BadRequest(new { isError = true, message = ex.Message });
            }
        });

        // ── Inline container discovery (no project ID needed) ──────────────

        app.MapPost("/api/servers/db/containers-inline", async (
            InlineServerRequest request, DatabaseOrchestrator orchestrator) =>
        {
            var project = new ProjectConfig
            {
                Server = new ServerConfig
                {
                    Host = request.Host,
                    Username = request.Username,
                    SshKeyPath = request.SshKeyPath,
                    RemoteWorkingDir = "/",
                }
            };

            try
            {
                var containers = await orchestrator.ListContainersAsync(project);
                return Results.Ok(containers);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "ListContainers inline failed");
                return Results.BadRequest(new { isError = true, message = ex.Message });
            }
        });

        // ── Inline database listing (no project ID needed) ──────────────────

        app.MapPost("/api/servers/db/databases-inline", async (
            DatabasesInlineRequest request, DatabaseOrchestrator orchestrator) =>
        {
            if (!Enum.TryParse<DbProviderType>(request.Provider, ignoreCase: true, out var providerType))
                return Results.BadRequest(new { isError = true, message = $"Unknown provider '{request.Provider}'." });

            var server = new ServerConfig
            {
                Host = request.Host,
                Username = request.Username,
                SshKeyPath = request.SshKeyPath,
                RemoteWorkingDir = "/",
            };

            try
            {
                var databases = await orchestrator.ListDatabasesAsync(
                    new ProjectConfig { Server = server }, request.Container, providerType);
                return Results.Ok(databases);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "ListDatabases inline failed");
                return Results.BadRequest(new { isError = true, message = ex.Message });
            }
        });

        app.MapGet("/api/projects/{id}/db/databases", async (
            string id, string container, string provider,
            IProjectStore store, DatabaseOrchestrator orchestrator) =>
        {
            var project = await store.GetByIdAsync(id);
            if (project is null)
                return Results.NotFound(new { isError = true, message = $"Project '{id}' not found." });

            if (!Enum.TryParse<DbProviderType>(provider, ignoreCase: true, out var providerType))
                return Results.BadRequest(new { isError = true, message = $"Unknown provider '{provider}'." });

            try
            {
                var databases = await orchestrator.ListDatabasesAsync(project, container, providerType);
                return Results.Ok(databases);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "ListDatabases failed for {ProjectId} container={Container}", id, container);
                return Results.BadRequest(new { isError = true, message = ex.Message });
            }
        });

        // ── Infer from docker-compose ─────────────────────────────────────────

        app.MapGet("/api/projects/{id}/db/infer", async (
            string id, IProjectStore store, DatabaseOrchestrator orchestrator) =>
        {
            var project = await store.GetByIdAsync(id);
            if (project is null)
                return Results.NotFound(new { isError = true, message = $"Project '{id}' not found." });

            try
            {
                var result = await orchestrator.InferConfigAsync(project);
                if (result is null)
                    return Results.Ok(new { found = false, config = (object?)null, detected = Array.Empty<string>() });

                return Results.Ok(new
                {
                    found    = true,
                    config   = result.Config,
                    detected = result.Detected,
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "InferConfig failed for {ProjectId}", id);
                return Results.BadRequest(new { isError = true, message = ex.Message });
            }
        });

        // ── Backup ────────────────────────────────────────────────────────────

        app.MapPost("/api/projects/{id}/db/backup", async (
            string id, IProjectStore store, DatabaseOrchestrator orchestrator, BuildEventBus bus) =>
        {
            var project = await store.GetByIdAsync(id);
            if (project is null)
                return Results.NotFound(new { isError = true, message = $"Project '{id}' not found." });
            if (project.Database is null)
                return Results.BadRequest(new { isError = true, message = "No database configuration for this project." });
            if (string.IsNullOrWhiteSpace(project.Database.DatabaseName))
                return Results.BadRequest(new { isError = true, message = "Database name is not set. Go to Edit to configure it." });

            var opId = Guid.NewGuid().ToString("N");
            bus.Register(opId);
            _ = Task.Run(() => orchestrator.BackupAsync(project, opId, CancellationToken.None));
            return Results.Accepted($"/api/projects/{id}/db/ops/{opId}/stream",
                new { opId, message = "Backup started." });
        });

        // ── Restore ───────────────────────────────────────────────────────────

        app.MapPost("/api/projects/{id}/db/restore", async (
            string id, RestoreRequest request,
            IProjectStore store, DatabaseOrchestrator orchestrator, BuildEventBus bus) =>
        {
            var project = await store.GetByIdAsync(id);
            if (project is null)
                return Results.NotFound(new { isError = true, message = $"Project '{id}' not found." });
            if (project.Database is null)
                return Results.BadRequest(new { isError = true, message = "No database configuration for this project." });
            if (string.IsNullOrWhiteSpace(request.BackupFile))
                return Results.BadRequest(new { isError = true, message = "backupFile is required." });
            if (!File.Exists(request.BackupFile))
                return Results.BadRequest(new { isError = true, message = $"Backup file not found: {request.BackupFile}" });

            var opId = Guid.NewGuid().ToString("N");
            bus.Register(opId);
            _ = Task.Run(() => orchestrator.RestoreAsync(project, opId, request.BackupFile, CancellationToken.None));
            return Results.Accepted($"/api/projects/{id}/db/ops/{opId}/stream",
                new { opId, message = "Restore started." });
        });

        // ── Query ─────────────────────────────────────────────────────────────

        app.MapPost("/api/projects/{id}/db/query", async (
            string id, QueryRequest request,
            IProjectStore store, DatabaseOrchestrator orchestrator, BuildEventBus bus) =>
        {
            var project = await store.GetByIdAsync(id);
            if (project is null)
                return Results.NotFound(new { isError = true, message = $"Project '{id}' not found." });
            if (project.Database is null)
                return Results.BadRequest(new { isError = true, message = "No database configuration for this project." });
            if (string.IsNullOrWhiteSpace(request.LocalSqlPath))
                return Results.BadRequest(new { isError = true, message = "localSqlPath is required." });
            if (!File.Exists(request.LocalSqlPath))
                return Results.BadRequest(new { isError = true, message = $"SQL file not found: {request.LocalSqlPath}" });

            var opId = Guid.NewGuid().ToString("N");
            bus.Register(opId);
            _ = Task.Run(() => orchestrator.QueryAsync(project, opId, request.LocalSqlPath, CancellationToken.None));
            return Results.Accepted($"/api/projects/{id}/db/ops/{opId}/stream",
                new { opId, message = "Query started." });
        });

        // ── Raw SQL query ─────────────────────────────────────────────────────

        app.MapPost("/api/projects/{id}/db/query-raw", async (
            string id, QueryRawRequest request,
            IProjectStore store, DatabaseOrchestrator orchestrator, BuildEventBus bus) =>
        {
            var project = await store.GetByIdAsync(id);
            if (project is null)
                return Results.NotFound(new { isError = true, message = $"Project '{id}' not found." });
            if (project.Database is null)
                return Results.BadRequest(new { isError = true, message = "No database configuration for this project." });
            if (string.IsNullOrWhiteSpace(request.Sql))
                return Results.BadRequest(new { isError = true, message = "sql is required." });

            var opId = Guid.NewGuid().ToString("N");
            bus.Register(opId);
            _ = Task.Run(() => orchestrator.QueryRawAsync(project, opId, request.Sql, CancellationToken.None));
            return Results.Accepted($"/api/projects/{id}/db/ops/{opId}/stream",
                new { opId, message = "Query started." });
        });

        // ── List backups ──────────────────────────────────────────────────────

        app.MapGet("/api/projects/{id}/db/backups", async (
            string id, IProjectStore store, DatabaseOrchestrator orchestrator) =>
        {
            var project = await store.GetByIdAsync(id);
            if (project is null)
                return Results.NotFound(new { isError = true, message = $"Project '{id}' not found." });

            var backups = orchestrator.ListBackups(id);
            return Results.Ok(backups);
        });

        // ── Delete backup ─────────────────────────────────────────────────────

        app.MapDelete("/api/projects/{id}/db/backups", async (
            string id, string file,
            IProjectStore store, DatabaseOrchestrator orchestrator) =>
        {
            var project = await store.GetByIdAsync(id);
            if (project is null)
                return Results.NotFound(new { isError = true, message = $"Project '{id}' not found." });
            if (string.IsNullOrWhiteSpace(file))
                return Results.BadRequest(new { isError = true, message = "file query param is required." });

            try
            {
                orchestrator.DeleteBackup(id, file);
                return Results.Ok(new { message = "Backup deleted." });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { isError = true, message = ex.Message });
            }
        });

        // ── SSE stream for db operations ──────────────────────────────────────

        app.MapGet("/api/projects/{id}/db/ops/{opId}/stream", async (
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

    private record InlineServerRequest(string Host, string Username, string SshKeyPath);
    private record DatabasesInlineRequest(string Host, string Username, string SshKeyPath, string Container, string Provider);
    private record RestoreRequest(string BackupFile);
    private record QueryRequest(string LocalSqlPath);
    private record QueryRawRequest(string Sql);
}
