using Serilog;
using ShipRight.Modules.Projects;
using ShipRight.Modules.Resources.Models;
using ShipRight.Modules.Resources.Stores;

namespace ShipRight.Modules.Resources;

public static class ResourceRouter
{
    public static void MapResourceRoutes(this WebApplication app)
    {
        // ── Docker Registry Resources ────────────────────────────────────────

        app.MapGet("/api/resources/registries", async (IDockerRegistryResourceStore store) =>
        {
            var resources = await store.GetAllAsync();
            return Results.Ok(resources);
        });

        app.MapGet("/api/resources/registries/{id}", async (Guid id, IDockerRegistryResourceStore store) =>
        {
            var resource = await store.GetByIdAsync(id);
            return resource is not null ? Results.Ok(resource) : Results.NotFound();
        });

        app.MapPost("/api/resources/registries", async (DockerRegistryResource resource, IDockerRegistryResourceStore store) =>
        {
            if (string.IsNullOrWhiteSpace(resource.Name))
                return Results.BadRequest(new { isError = true, field = "name", message = "Name is required." });
            if (string.IsNullOrWhiteSpace(resource.Registry))
                return Results.BadRequest(new { isError = true, field = "registry", message = "Registry is required." });

            var saved = resource with { Id = resource.Id == Guid.Empty ? Guid.NewGuid() : resource.Id };
            await store.SaveAsync(saved);
            return Results.Created($"/api/resources/registries/{saved.Id}", saved);
        });

        app.MapPut("/api/resources/registries/{id}", async (Guid id, DockerRegistryResource resource, IDockerRegistryResourceStore store) =>
        {
            var existing = await store.GetByIdAsync(id);
            if (existing is null)
                return Results.NotFound(new { isError = true, message = $"Registry resource '{id}' not found." });

            var saved = resource with { Id = id, ModifiedAt = DateTime.UtcNow };
            await store.SaveAsync(saved);
            return Results.Ok(saved);
        });

        app.MapDelete("/api/resources/registries/{id}", async (
            Guid id, IDockerRegistryResourceStore store, IProjectStore projectStore) =>
        {
            var existing = await store.GetByIdAsync(id);
            if (existing is null)
                return Results.NotFound(new { isError = true, message = $"Registry resource '{id}' not found." });

            var projects = await projectStore.GetAllAsync();
            var referencingProjects = projects.Where(p =>
                p.Services.Any(s => s.DockerRegistryResourceId == id)).ToList();

            if (referencingProjects.Count > 0)
                return Results.Conflict(new
                {
                    isError = true,
                    message = $"Resource is referenced by {referencingProjects.Count} project(s). Unlink them first.",
                    projectIds = referencingProjects.Select(p => p.Id).ToList(),
                });

            await store.DeleteAsync(id);
            return Results.Ok(new { message = "Registry resource deleted." });
        });

        // ── Script Resources ─────────────────────────────────────────────────

        app.MapGet("/api/resources/scripts", async (IScriptResourceStore store) =>
        {
            var resources = await store.GetAllAsync();
            return Results.Ok(resources);
        });

        app.MapGet("/api/resources/scripts/{id}", async (Guid id, IScriptResourceStore store) =>
        {
            var resource = await store.GetByIdAsync(id);
            return resource is not null ? Results.Ok(resource) : Results.NotFound();
        });

        app.MapPost("/api/resources/scripts", async (ScriptResource resource, IScriptResourceStore store) =>
        {
            if (string.IsNullOrWhiteSpace(resource.Name))
                return Results.BadRequest(new { isError = true, field = "name", message = "Name is required." });

            var saved = resource with { Id = resource.Id == Guid.Empty ? Guid.NewGuid() : resource.Id };
            await store.SaveAsync(saved);
            return Results.Created($"/api/resources/scripts/{saved.Id}", saved);
        });

        app.MapPut("/api/resources/scripts/{id}", async (Guid id, ScriptResource resource, IScriptResourceStore store) =>
        {
            var existing = await store.GetByIdAsync(id);
            if (existing is null)
                return Results.NotFound(new { isError = true, message = $"Script resource '{id}' not found." });

            var saved = resource with { Id = id, ModifiedAt = DateTime.UtcNow };
            await store.SaveAsync(saved);
            return Results.Ok(saved);
        });

        app.MapDelete("/api/resources/scripts/{id}", async (
            Guid id, IScriptResourceStore store, IProjectStore projectStore) =>
        {
            var existing = await store.GetByIdAsync(id);
            if (existing is null)
                return Results.NotFound(new { isError = true, message = $"Script resource '{id}' not found." });

            var projects = await projectStore.GetAllAsync();
            var referencingProjects = projects.Where(p =>
                p.Server.RebuildScriptResourceId == id).ToList();

            if (referencingProjects.Count > 0)
                return Results.Conflict(new
                {
                    isError = true,
                    message = $"Resource is referenced by {referencingProjects.Count} project(s). Unlink them first.",
                    projectIds = referencingProjects.Select(p => p.Id).ToList(),
                });

            await store.DeleteAsync(id);
            return Results.Ok(new { message = "Script resource deleted." });
        });

        // ── Reference check (utility) ────────────────────────────────────────

        app.MapGet("/api/resources/used-by/{id}", async (
            Guid id, IDockerRegistryResourceStore registryStore, IScriptResourceStore scriptStore,
            IProjectStore projectStore) =>
        {
            var projects = await projectStore.GetAllAsync();
            var references = projects.Where(p =>
                p.Services.Any(s => s.DockerRegistryResourceId == id)
                || p.Server.RebuildScriptResourceId == id).ToList();

            return Results.Ok(new
            {
                count = references.Count,
                projectIds = references.Select(p => p.Id).ToList(),
            });
        });
    }
}
