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
                    new ProjectConfig { Server = server }, request.Container, providerType,
                    request.RootUser, request.RootPassword);
                return Results.Ok(databases);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "ListDatabases inline failed");
                return Results.BadRequest(new { isError = true, message = ex.Message });
            }
        });

        app.MapPost("/api/projects/{id}/db/databases", async (
            string id, DatabasesProjectRequest request,
            IProjectStore store, DatabaseOrchestrator orchestrator) =>
        {
            var project = await store.GetByIdAsync(id);
            if (project is null)
                return Results.NotFound(new { isError = true, message = $"Project '{id}' not found." });

            if (!Enum.TryParse<DbProviderType>(request.Provider, ignoreCase: true, out var providerType))
                return Results.BadRequest(new { isError = true, message = $"Unknown provider '{request.Provider}'." });

            try
            {
                var databases = await orchestrator.ListDatabasesAsync(project, request.Container, providerType,
                    request.RootUser, request.RootPassword);
                return Results.Ok(databases);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "ListDatabases failed for {ProjectId} container={Container}", id, request.Container);
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
            string id, IProjectStore store, DatabaseBackupService backups, BuildEventBus bus) =>
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
            _ = Task.Run(() => backups.BackupAsync(project, opId, CancellationToken.None));
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
            if (string.IsNullOrWhiteSpace(request.FileName))
                return Results.BadRequest(new { isError = true, message = "fileName is required." });

            var known = orchestrator.ListBackups(id).Any(a => a.FileName == request.FileName);
            if (!known)
                return Results.BadRequest(new { isError = true, message = $"Backup not found: {request.FileName}" });

            var opId = Guid.NewGuid().ToString("N");
            bus.Register(opId);
            _ = Task.Run(() => orchestrator.RestoreAsync(project, opId, request.FileName!, CancellationToken.None));
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

        // ── Backup policy ─────────────────────────────────────────────────────

        app.MapPut("/api/projects/{id}/db/backup-policy", async (
            string id, BackupPolicyRequest request, IProjectStore store) =>
        {
            var project = await store.GetByIdAsync(id);
            if (project is null)
                return Results.NotFound(new { isError = true, message = $"Project '{id}' not found." });
            if (project.Database is null)
                return Results.BadRequest(new { isError = true, message = "No database configuration for this project." });
            if (request.KeepCount is < 1 or > 3650)
                return Results.BadRequest(new { isError = true, message = "Keep at least one backup and at most 3650." });
            if (request.KeepDays is < 0 or > 3650)
                return Results.BadRequest(new { isError = true, message = "The age limit must be between 0 and 3650 days." });
            if (!BackupCompression.IsValid(request.CompressionLevel))
                return Results.BadRequest(new { isError = true, message = "The compression level must be between 0 and 19." });

            var updated = project with
            {
                Database = project.Database with
                {
                    BackupRetainCount = request.KeepCount,
                    BackupRetainDays = request.KeepDays,
                    BackupCompressionLevel = request.CompressionLevel,
                    BackupEncrypt = request.Encrypt,
                },
            };
            await store.SaveAsync(updated);

            return Results.Ok(new
            {
                keepCount = updated.Database.BackupRetainCount,
                keepDays = updated.Database.BackupRetainDays,
                compressionLevel = updated.Database.BackupCompressionLevel,
                encrypt = updated.Database.BackupEncrypt,
            });
        });

        // ── List backups ──────────────────────────────────────────────────────

        app.MapGet("/api/projects/{id}/db/backups", async (
            string id, IProjectStore store, DatabaseOrchestrator orchestrator) =>
        {
            var project = await store.GetByIdAsync(id);
            if (project is null)
                return Results.NotFound(new { isError = true, message = $"Project '{id}' not found." });

            var artifacts = orchestrator.ListBackups(id);
            return Results.Ok(BackupViews.Build(
                artifacts,
                project.Database?.BackupRetainCount ?? 10,
                project.Database?.BackupRetainDays ?? 0));
        });

        // ── Download backup ───────────────────────────────────────────────────

        app.MapGet("/api/projects/{id}/db/backups/download", async (
            string id, string file, string? format,
            IProjectStore store, DatabaseOrchestrator orchestrator, BackupKeyring keyring, CancellationToken ct) =>
        {
            var project = await store.GetByIdAsync(id);
            if (project is null)
                return Results.NotFound(new { isError = true, message = $"Project '{id}' not found." });

            var known = orchestrator.ListBackups(id).FirstOrDefault(a => a.FileName == file);
            if (known is null)
                return Results.NotFound(new { isError = true, message = "Backup file not found." });

            var mode = (format ?? "plain").Trim().ToLowerInvariant();
            if (mode is not ("plain" or "zstd"))
                return Results.BadRequest(new { isError = true, message = "format must be plain or zstd." });

            try
            {
                var readMode = mode == "zstd" ? BackupReadMode.Compressed : BackupReadMode.Plain;
                var (stream, meta) = await orchestrator.OpenBackupAsync(id, file, readMode, ct);
                var name = BackupMetadata.OriginalFileName(meta);
                if (mode == "zstd") name += ".zst";

                return Results.Stream(stream, mode == "zstd" ? "application/zstd" : "application/octet-stream", name);
            }
            catch (FileNotFoundException)
            {
                return Results.NotFound(new { isError = true, message = "Backup file not found." });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { isError = true, message = ex.Message });
            }
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

        // ── Storage and recovery keys ─────────────────────────────────────────

        app.MapGet("/api/backups/storage", (DatabaseOrchestrator orchestrator) =>
            Results.Ok(orchestrator.StorageInfo()));

        app.MapGet("/api/backups/recovery-key", async (BackupKeyring keyring, CancellationToken ct) =>
            Results.Ok(new { keys = await keyring.ListAsync(ct) }));

        app.MapGet("/api/backups/recovery-key/export", async (BackupKeyring keyring, CancellationToken ct) =>
        {
            var json = await keyring.ExportJsonAsync(ct);
            return Results.File(global::System.Text.Encoding.UTF8.GetBytes(json), "application/json",
                $"shipright-backup-keys-{DateTime.UtcNow:yyyyMMdd}.json");
        });

        app.MapPost("/api/backups/recovery-key/rotate", async (BackupKeyring keyring, CancellationToken ct) =>
        {
            var key = await keyring.RotateAsync(ct);
            Log.Information("Backup key rotated; new active key {KeyId}", key.Id);
            return Results.Ok(new
            {
                activeKeyId = key.Id,
                message = "A new key is active. Download the recovery file again: it contains every key, including this one.",
                recovery = await keyring.ExportAsync(ct),
            });
        });

        app.MapPost("/api/backups/recovery-key/retire", async (
            RetireKeyRequest request, BackupKeyring keyring, BackupStorage storage, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.KeyId))
                return Results.BadRequest(new { isError = true, message = "keyId is required." });
            if (BackupArchive.ReferencedKeyIds(storage).Contains(request.KeyId))
                return Results.BadRequest(new
                {
                    isError = true,
                    message = "Existing backups still use this key, so it cannot be retired yet.",
                });

            try
            {
                await keyring.RetireAsync(request.KeyId, ct);
                return Results.Ok(new { message = "Key retired." });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { isError = true, message = ex.Message });
            }
        });

        // ── Active operation query ────────────────────────────────────────────

        app.MapGet("/api/projects/{id}/db/ops/active", (
            string id, DatabaseOrchestrator orchestrator) =>
        {
            var opId = orchestrator.GetActiveOpId(id);
            return opId is null
                ? Results.NoContent()
                : Results.Ok(new { opId });
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
    private record DatabasesInlineRequest(string Host, string Username, string SshKeyPath, string Container, string Provider, string RootUser = "root", string RootPassword = "");
    private record DatabasesProjectRequest(string Container, string Provider, string RootUser = "root", string RootPassword = "");
    private record RestoreRequest(string? FileName);
    private record RetireKeyRequest(string? KeyId);
    private record BackupPolicyRequest(int KeepCount, int KeepDays, int CompressionLevel, bool Encrypt = true);
    private record QueryRequest(string LocalSqlPath);
    private record QueryRawRequest(string Sql);
}
