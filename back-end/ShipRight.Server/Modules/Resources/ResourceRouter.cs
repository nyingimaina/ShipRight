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

        // ── Credential Resources ──────────────────────────────────────────────

        app.MapGet("/api/resources/credentials", async (ICredentialResourceStore store) =>
        {
            var resources = await store.GetAllAsync();
            return Results.Ok(resources);
        });

        app.MapGet("/api/resources/credentials/{id}", async (Guid id, ICredentialResourceStore store) =>
        {
            var resource = await store.GetByIdAsync(id);
            return resource is not null ? Results.Ok(resource) : Results.NotFound();
        });

        app.MapPost("/api/resources/credentials", async (CredentialResource resource, ICredentialResourceStore store) =>
        {
            if (string.IsNullOrWhiteSpace(resource.Name))
                return Results.BadRequest(new { isError = true, field = "name", message = "Name is required." });
            if (string.IsNullOrWhiteSpace(resource.Value))
                return Results.BadRequest(new { isError = true, field = "value", message = "Credential value is required." });

            var saved = resource with { Id = resource.Id == Guid.Empty ? Guid.NewGuid() : resource.Id };
            await store.SaveAsync(saved);
            return Results.Created($"/api/resources/credentials/{saved.Id}", saved);
        });

        app.MapPut("/api/resources/credentials/{id}", async (Guid id, CredentialResource resource, ICredentialResourceStore store) =>
        {
            var existing = await store.GetByIdAsync(id);
            if (existing is null)
                return Results.NotFound(new { isError = true, message = $"Credential resource '{id}' not found." });

            var saved = resource with { Id = id, ModifiedAt = DateTime.UtcNow };
            await store.SaveAsync(saved);
            return Results.Ok(saved);
        });

        app.MapDelete("/api/resources/credentials/{id}", async (
            Guid id, ICredentialResourceStore store, IProjectStore projectStore) =>
        {
            var existing = await store.GetByIdAsync(id);
            if (existing is null)
                return Results.NotFound(new { isError = true, message = $"Credential resource '{id}' not found." });

            var projects = await projectStore.GetAllAsync();
            var referencingProjects = projects.Where(p =>
                p.GitRepos.Any(g => g.CredentialResourceId == id)).ToList();

            if (referencingProjects.Count > 0)
                return Results.Conflict(new
                {
                    isError = true,
                    message = $"Credential is referenced by {referencingProjects.Count} project(s). Unlink them first.",
                    projectIds = referencingProjects.Select(p => p.Id).ToList(),
                });

            await store.DeleteAsync(id);
            return Results.Ok(new { message = "Credential resource deleted." });
        });

        // ── AWS Profile Resources ────────────────────────────────────────────

        app.MapGet("/api/resources/aws-profiles", async (IAwsProfileResourceStore store) =>
        {
            var resources = await store.GetAllAsync();
            return Results.Ok(resources);
        });

        app.MapGet("/api/resources/aws-profiles/available", async (IAwsCredentialsReader reader) =>
        {
            var source = await reader.ReadAsync();
            return Results.Ok(source);
        });

        app.MapPost("/api/resources/aws-profiles/validate", async (
            AwsProfileValidateRequest request, AwsProfileValidator validator) =>
        {
            var result = await validator.ValidateAsync(request.ProfileId, request.Profile);
            return Results.Ok(result);
        });

        app.MapPost("/api/resources/aws-profiles/install-cli", async (
            AwsCliInstallRequest? request, AwsCliInstaller installer) =>
        {
            var result = await installer.RunAsync(request);
            return Results.Ok(result);
        });

        app.MapGet("/api/resources/aws-profiles/{id}", async (Guid id, IAwsProfileResourceStore store) =>
        {
            var resource = await store.GetByIdAsync(id);
            return resource is not null ? Results.Ok(resource) : Results.NotFound();
        });

        app.MapPost("/api/resources/aws-profiles", async (AwsProfileResource resource, IAwsProfileResourceStore store) =>
        {
            if (string.IsNullOrWhiteSpace(resource.Name))
                return Results.BadRequest(new { isError = true, field = "name", message = "Name is required." });
            if (string.IsNullOrWhiteSpace(resource.ProfileName) && !resource.UsesExplicitKeys)
                return Results.BadRequest(new
                {
                    isError = true,
                    field = "profile",
                    message = "Provide a named AWS profile or explicit access keys.",
                });

            var saved = resource with { Id = resource.Id == Guid.Empty ? Guid.NewGuid() : resource.Id };
            await store.SaveAsync(saved);
            return Results.Created($"/api/resources/aws-profiles/{saved.Id}", saved);
        });

        app.MapPut("/api/resources/aws-profiles/{id}", async (Guid id, AwsProfileResource resource, IAwsProfileResourceStore store) =>
        {
            var existing = await store.GetByIdAsync(id);
            if (existing is null)
                return Results.NotFound(new { isError = true, message = $"AWS profile resource '{id}' not found." });

            var saved = resource with { Id = id, ModifiedAt = DateTime.UtcNow };
            await store.SaveAsync(saved);
            return Results.Ok(saved);
        });

        app.MapDelete("/api/resources/aws-profiles/{id}", async (
            Guid id, IAwsProfileResourceStore store, IDockerRegistryResourceStore registryStore) =>
        {
            var existing = await store.GetByIdAsync(id);
            if (existing is null)
                return Results.NotFound(new { isError = true, message = $"AWS profile resource '{id}' not found." });

            var registries = await registryStore.GetAllAsync();
            var referencingRegistries = registries.Where(r => r.AwsProfileResourceId == id).ToList();

            if (referencingRegistries.Count > 0)
                return Results.Conflict(new
                {
                    isError = true,
                    message = $"AWS profile is referenced by {referencingRegistries.Count} registry resource(s). Unlink them first.",
                    registryIds = referencingRegistries.Select(r => r.Id).ToList(),
                });

            await store.DeleteAsync(id);
            return Results.Ok(new { message = "AWS profile resource deleted." });
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
