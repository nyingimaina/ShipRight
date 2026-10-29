using Serilog;

namespace ShipRight.Modules.Projects;

/// <summary>
/// The original project wizard rebuilds every service from a fixed list of fields, so settings added later
/// (the container port, the local-cleanup opt-out) would be wiped by an ordinary edit. When a service arrives
/// without one of them, the stored value is kept; a value that is sent always wins.
/// </summary>
public class ServiceSettingsPreservingProjectStore : IProjectStore
{
    private readonly IProjectStore _inner;

    public ServiceSettingsPreservingProjectStore(IProjectStore inner) => _inner = inner;

    public int Count => _inner.Count;

    public Task<List<ProjectConfig>> GetAllAsync() => _inner.GetAllAsync();

    public Task<ProjectConfig?> GetByIdAsync(string id) => _inner.GetByIdAsync(id);

    public Task<ProjectConfig?> GetByNameAsync(string name) => _inner.GetByNameAsync(name);

    public async Task SaveAsync(ProjectConfig project)
    {
        var existing = await _inner.GetByIdAsync(project.Id);
        if (existing is not null && project.Services.Count > 0)
        {
            var services = project.Services.Select(s =>
            {
                var old = existing.Services.FirstOrDefault(o => o.Name.Equals(s.Name, StringComparison.OrdinalIgnoreCase));
                if (old is null) return s;
                return s with
                {
                    ContainerPort = s.ContainerPort ?? old.ContainerPort,
                    SkipLocalCleanup = s.SkipLocalCleanup ?? old.SkipLocalCleanup,
                };
            }).ToList();
            project = project with { Services = services };
        }
        await _inner.SaveAsync(project);
    }

    public Task DeleteAsync(string id) => _inner.DeleteAsync(id);
}

public record LocalCleanupRequest(string? Service, bool? SkipLocalCleanup);

/// <summary>Opt a single service out of (or back into) deleting its images from this computer.</summary>
public static class ProjectLocalCleanupRoutes
{
    public static void MapProjectLocalCleanupRoutes(this WebApplication app)
    {
        app.MapPost("/api/projects/{id}/local-cleanup", (string id, LocalCleanupRequest req, IProjectStore store) =>
            HandleSetAsync(id, req, store));
    }

    internal static async Task<IResult> HandleSetAsync(string id, LocalCleanupRequest req, IProjectStore store)
    {
        if (string.IsNullOrWhiteSpace(req.Service))
            return Results.BadRequest(ProjectRouter.Error("Say which service this is for.", "service"));
        if (req.SkipLocalCleanup is null)
            return Results.BadRequest(ProjectRouter.Error("Say whether to keep this service's images.", "skipLocalCleanup"));

        var project = await store.GetByIdAsync(id);
        if (project is null)
            return Results.NotFound(ProjectRouter.Error($"Project '{id}' not found."));
        if (!project.Services.Any(s => s.Name.Equals(req.Service, StringComparison.OrdinalIgnoreCase)))
            return Results.NotFound(ProjectRouter.Error($"Service '{req.Service}' not found in this project.", "service"));

        var services = project.Services
            .Select(s => s.Name.Equals(req.Service, StringComparison.OrdinalIgnoreCase) ? s with { SkipLocalCleanup = req.SkipLocalCleanup } : s)
            .ToList();
        await store.SaveAsync(project with { Services = services, ModifiedAt = DateTime.UtcNow });
        Log.Information("Project {ProjectId} service {Service}: skip local image cleanup = {Skip}", id, req.Service, req.SkipLocalCleanup);
        return Results.Ok(new { service = req.Service, skipLocalCleanup = req.SkipLocalCleanup });
    }
}
