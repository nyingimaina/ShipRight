using ShipRight.Modules.Projects;
using ShipRight.Modules.Resources.Models;
using ShipRight.Modules.Resources.Stores;

namespace ShipRight.Modules.Resources;

public static class PipelineRouter
{
    public static void MapPipelineRoutes(this WebApplication app)
    {
        // ── Pipeline Resources ──────────────────────────────────────────────

        app.MapGet("/api/resources/pipelines", async (
            IPipelineResourceStore store,
            Microsoft.AspNetCore.Http.HttpContext http) =>
        {
            var scope = http.Request.Query["scope"].FirstOrDefault();
            var projectIdStr = http.Request.Query["projectId"].FirstOrDefault();

            List<PipelineResource> pipelines;
            if (Guid.TryParse(projectIdStr, out var projectId))
            {
                pipelines = await store.GetByProjectAsync(projectId);
            }
            else if (scope == "global")
            {
                pipelines = await store.GetGlobalAsync();
            }
            else
            {
                pipelines = await store.GetAllAsync();
            }

            return Results.Ok(pipelines);
        });

        app.MapGet("/api/resources/pipelines/{id}", async (Guid id, IPipelineResourceStore store) =>
        {
            var resource = await store.GetByIdAsync(id);
            return resource is not null ? Results.Ok(resource) : Results.NotFound();
        });

        app.MapPost("/api/resources/pipelines", async (PipelineResource resource, IPipelineResourceStore store) =>
        {
            if (string.IsNullOrWhiteSpace(resource.Name))
                return Results.BadRequest(new { isError = true, field = "name", message = "Name is required." });

            var validationErrors = PipelineExecutor.ValidateSteps(resource.Steps);
            if (validationErrors.Count > 0)
                return Results.BadRequest(new { isError = true, message = "Invalid pipeline steps.", errors = validationErrors });

            var saved = resource with { Id = resource.Id == Guid.Empty ? Guid.NewGuid() : resource.Id };
            await store.SaveAsync(saved);
            return Results.Created($"/api/resources/pipelines/{saved.Id}", saved);
        });

        app.MapPut("/api/resources/pipelines/{id}", async (Guid id, PipelineResource resource, IPipelineResourceStore store) =>
        {
            var existing = await store.GetByIdAsync(id);
            if (existing is null)
                return Results.NotFound(new { isError = true, message = $"Pipeline resource '{id}' not found." });

            var validationErrors = PipelineExecutor.ValidateSteps(resource.Steps);
            if (validationErrors.Count > 0)
                return Results.BadRequest(new { isError = true, message = "Invalid pipeline steps.", errors = validationErrors });

            var saved = resource with { Id = id, ModifiedAt = DateTime.UtcNow };
            await store.SaveAsync(saved);
            return Results.Ok(saved);
        });

        app.MapDelete("/api/resources/pipelines/{id}", async (
            Guid id, IPipelineResourceStore store, IProjectStore projectStore) =>
        {
            var existing = await store.GetByIdAsync(id);
            if (existing is null)
                return Results.NotFound(new { isError = true, message = $"Pipeline resource '{id}' not found." });

            var projects = await projectStore.GetAllAsync();
            var referencingProjects = projects.Where(p =>
                p.Server.PipelineResourceId == id).ToList();

            if (referencingProjects.Count > 0)
                return Results.Conflict(new
                {
                    isError = true,
                    message = $"Pipeline is referenced by {referencingProjects.Count} project(s). Unlink them first.",
                    projectIds = referencingProjects.Select(p => p.Id).ToList(),
                });

            await store.DeleteAsync(id);
            return Results.Ok(new { message = "Pipeline resource deleted." });
        });

        // ── Pipeline Validation ────────────────────────────────────────────

        app.MapPost("/api/resources/pipelines/validate", async (PipelineResource resource) =>
        {
            var errors = PipelineExecutor.ValidateSteps(resource.Steps);
            return Results.Ok(new { valid = errors.Count == 0, errors });
        });
    }
}
