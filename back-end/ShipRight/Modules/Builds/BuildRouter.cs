using Serilog;
using ShipRight.Shared.Events;

namespace ShipRight.Modules.Builds;

public static class BuildRouter
{
    public static void MapBuildRoutes(this WebApplication app)
    {
        app.MapPost("/api/builds/start", async (StartBuildRequest request, BuildOrchestrator orchestrator) =>
        {
            try
            {
                var record = await orchestrator.StartAsync(request);
                Log.Information("Build {BuildId} started for project {ProjectId}", record.Id, request.ProjectId);
                return Results.Accepted($"/api/builds/{record.Id}", new
                {
                    buildId = record.Id,
                    status = record.Status.ToString(),
                    message = "Build pipeline started"
                });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { isError = true, message = ex.Message });
            }
        });

        app.MapGet("/api/builds", async (
            IBuildStore store,
            string? projectId, string? status, DateTime? from, DateTime? to,
            string? gitTag, int page = 1, int pageSize = 20) =>
        {
            pageSize = Math.Min(pageSize, 100);
            var items = await store.QueryAsync(projectId, status, from, to, gitTag, page, pageSize);
            var total = await store.CountQueryAsync(projectId, status, from, to, gitTag);
            return Results.Ok(new
            {
                items,
                totalCount = total,
                page,
                pageSize,
                totalPages = (int)Math.Ceiling(total / (double)pageSize)
            });
        });

        app.MapGet("/api/builds/{id}", async (string id, IBuildStore store) =>
        {
            var record = await store.GetByIdAsync(id);
            return record is null
                ? Results.NotFound(new { isError = true, message = $"Build '{id}' not found." })
                : Results.Ok(record);
        });

        app.MapGet("/api/builds/{id}/log", async (string id, IBuildStore store) =>
        {
            var record = await store.GetByIdAsync(id);
            if (record is null) return Results.NotFound();
            return Results.Text(record.LogOutput, "text/plain");
        });

        app.MapPost("/api/builds/{id}/respond", async (string id, RespondRequest request,
            IBuildStore store, BuildOrchestrator orchestrator) =>
        {
            var record = await store.GetByIdAsync(id);
            if (record is null) return Results.NotFound(new { isError = true, message = $"Build '{id}' not found." });
            if (record.Status != BuildStatus.Paused)
                return Results.BadRequest(new { isError = true, message = "Build is not in a paused state." });

            var handled = await orchestrator.RespondAsync(id, request);
            if (!handled) return Results.BadRequest(new { isError = true, message = "No pause waiter found for this build." });

            return Results.Ok(new { status = "Running", message = "Response accepted. Pipeline resuming." });
        });

        // SSE stream — client connects here to receive real-time build events
        app.MapGet("/api/builds/{id}/stream", async (
            string id, IBuildStore store, BuildEventBus eventBus,
            HttpContext http, CancellationToken ct) =>
        {
            var record = await store.GetByIdAsync(id);
            if (record is null) { http.Response.StatusCode = 404; return; }

            http.Response.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers["X-Accel-Buffering"] = "no";
            http.Response.Headers.Connection = "keep-alive";

            var reader = eventBus.Subscribe(id);
            try
            {
                await foreach (var payload in reader.ReadAllAsync(ct))
                {
                    await http.Response.WriteAsync($"data: {payload}\n\n", ct);
                    await http.Response.Body.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException) { /* client disconnected */ }
            finally { eventBus.Unsubscribe(id, reader); }
        });

        app.MapPost("/api/builds/{id}/cancel", async (string id, IBuildStore store, BuildOrchestrator orchestrator) =>
        {
            var record = await store.GetByIdAsync(id);
            if (record is null) return Results.NotFound(new { isError = true, message = $"Build '{id}' not found." });
            if (record.Status != BuildStatus.Running && record.Status != BuildStatus.Paused)
                return Results.BadRequest(new { isError = true, message = "Build is not running." });

            var cancelled = orchestrator.CancelBuild(id);
            if (!cancelled)
                return Results.BadRequest(new { isError = true, message = "No active pipeline found for this build." });

            return Results.Ok(new { message = "Cancellation requested." });
        });

        app.MapPost("/api/builds/{id}/push", async (string id, IBuildStore store, BuildOrchestrator orchestrator) =>
        {
            var record = await store.GetByIdAsync(id);
            if (record is null) return Results.NotFound(new { isError = true, message = $"Build '{id}' not found." });
            if (record.Status != BuildStatus.ImageBuilt)
                return Results.BadRequest(new { isError = true, message = "Only an image-built record can be pushed." });

            _ = Task.Run(() => orchestrator.PushAsync(id));
            return Results.Accepted($"/api/builds/{id}", new
            {
                buildId = id,
                status = BuildStatus.Running.ToString(),
                message = "Push to registry started"
            });
        });

        app.MapPost("/api/builds/{id}/deploy", async (string id, IBuildStore store, BuildOrchestrator orchestrator) =>
        {
            var record = await store.GetByIdAsync(id);
            if (record is null) return Results.NotFound(new { isError = true, message = $"Build '{id}' not found." });
            if (record.Status != BuildStatus.PushSucceeded)
                return Results.BadRequest(new { isError = true, message = "Only a pushed build can be deployed." });

            _ = Task.Run(() => orchestrator.DeployAsync(id));
            return Results.Accepted($"/api/builds/{id}", new
            {
                buildId = id,
                status = BuildStatus.Deploying.ToString(),
                message = "Deployment started"
            });
        });
    }
}
