using System.Security.Claims;
using Serilog;

namespace ShipRight.Modules.Projects;

public record RefreshPathsRequest(string? RootPath);

public static class ProjectPathRefreshRoutes
{
    public static void MapProjectPathRefreshRoutes(this WebApplication app)
    {
        app.MapPost("/api/projects/{id}/refresh-paths/preview",
            (string id, RefreshPathsRequest req, IProjectStore store, ClaimsPrincipal user) =>
                RunAsync(id, req, store, user, apply: false));

        app.MapPost("/api/projects/{id}/refresh-paths/apply",
            (string id, RefreshPathsRequest req, IProjectStore store, ClaimsPrincipal user) =>
                RunAsync(id, req, store, user, apply: true));
    }

    private static async Task<IResult> RunAsync(
        string id, RefreshPathsRequest req, IProjectStore store, ClaimsPrincipal user, bool apply)
    {
        var service = new ProjectPathRefreshService(store, root => ProjectDetector.Detect(root, pullLatest: false));
        try
        {
            var outcome = apply
                ? await service.ApplyAsync(id, req.RootPath)
                : await service.PreviewAsync(id, req.RootPath);
            if (outcome is null)
                return Results.NotFound(ProjectRouter.Error($"Project '{id}' not found."));

            if (apply && outcome.Plan.Changes.Count > 0)
                Log.Information("Refreshed paths for project {ProjectId}: {Changes}", id,
                    string.Join("; ", outcome.Plan.Changes.Select(c => $"{c.ServiceName}: {c.OldBuildContextPath} -> {c.NewBuildContextPath}")));

            return Results.Ok(new
            {
                plan = outcome.Plan,
                project = outcome.Project is null ? null : ProjectRouter.RedactCredentialsFor(user, outcome.Project),
            });
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(ProjectRouter.Error(ex.Message, "rootPath"));
        }
    }
}
