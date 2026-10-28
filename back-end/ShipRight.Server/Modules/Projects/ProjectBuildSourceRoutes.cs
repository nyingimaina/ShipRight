using Serilog;

namespace ShipRight.Modules.Projects;

public record BuildSourceRequest(BuildSource? BuildSource);

/// <summary>A small, separate endpoint so switching how a project builds never has to re-send (or risk) the rest of its settings.</summary>
public static class ProjectBuildSourceRoutes
{
    public static void MapProjectBuildSourceRoutes(this WebApplication app)
    {
        app.MapPost("/api/projects/{id}/build-source", (string id, BuildSourceRequest req, IProjectStore store) =>
            HandleSetAsync(id, req, store));
    }

    internal static async Task<IResult> HandleSetAsync(string id, BuildSourceRequest req, IProjectStore store)
    {
        if (req.BuildSource is null)
            return Results.BadRequest(ProjectRouter.Error("Choose how this project should build.", "buildSource"));

        var project = await store.GetByIdAsync(id);
        if (project is null)
            return Results.NotFound(ProjectRouter.Error($"Project '{id}' not found."));

        await store.SaveAsync(project with { BuildSource = req.BuildSource, ModifiedAt = DateTime.UtcNow });
        Log.Information("Project {ProjectId} now builds from {BuildSource}", id, req.BuildSource);
        return Results.Ok(new { buildSource = req.BuildSource.ToString() });
    }
}
