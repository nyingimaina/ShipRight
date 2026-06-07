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

        // ── Backup ────────────────────────────────────────────────────────────

        app.MapPost("/api/projects/{id}/db/backup", async (
            string id, IProjectStore store, DatabaseOrchestrator orchestrator) =>
        {
            var project = await store.GetByIdAsync(id);
            if (project is null)
                return Results.NotFound(new { isError = true, message = $"Project '{id}' not found." });
            if (project.Database is null)
                return Results.BadRequest(new { isError = true, message = "No database configuration for this project." });

            var opId = Guid.NewGuid().ToString("N");
            _ = Task.Run(() => orchestrator.BackupAsync(project, opId, CancellationToken.None));
            return Results.Accepted($"/api/projects/{id}/db/ops/{opId}/stream",
                new { opId, message = "Backup started." });
        });

        // ── Restore ───────────────────────────────────────────────────────────

        app.MapPost("/api/projects/{id}/db/restore", async (
            string id, RestoreRequest request,
            IProjectStore store, DatabaseOrchestrator orchestrator) =>
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
            _ = Task.Run(() => orchestrator.RestoreAsync(project, opId, request.BackupFile, CancellationToken.None));
            return Results.Accepted($"/api/projects/{id}/db/ops/{opId}/stream",
                new { opId, message = "Restore started." });
        });

        // ── Query ─────────────────────────────────────────────────────────────

        app.MapPost("/api/projects/{id}/db/query", async (
            string id, QueryRequest request,
            IProjectStore store, DatabaseOrchestrator orchestrator) =>
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
            _ = Task.Run(() => orchestrator.QueryAsync(project, opId, request.LocalSqlPath, CancellationToken.None));
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

        // ── SSE stream for db operations ──────────────────────────────────────

        app.MapGet("/api/projects/{id}/db/ops/{opId}/stream", async (
            string id, string opId,
            BuildEventBus eventBus, HttpContext http, CancellationToken ct) =>
        {
            http.Response.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers["X-Accel-Buffering"] = "no";
            http.Response.Headers.Connection = "keep-alive";

            var reader = eventBus.Subscribe(opId);
            try
            {
                await foreach (var payload in reader.ReadAllAsync(ct))
                {
                    await http.Response.WriteAsync($"data: {payload}\n\n", ct);
                    await http.Response.Body.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException) { /* client disconnected */ }
            finally { eventBus.Unsubscribe(opId, reader); }
        });
    }

    private record RestoreRequest(string BackupFile);
    private record QueryRequest(string LocalSqlPath);
}
