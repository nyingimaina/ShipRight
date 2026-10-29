using Serilog;
using ShipRight.Modules.Resources;

namespace ShipRight.Modules.Projects;

public record PolicyRequest(bool? DeleteAfterPush);

public static class ProjectSpaceRoutes
{
    public static void MapProjectSpaceRoutes(this WebApplication app)
    {
        app.MapGet("/api/projects/{id}/space", async (string id, ProjectSpaceService space, CancellationToken ct) =>
            await space.ReportAsync(id, ct) is { } report ? Results.Ok(report) : Results.NotFound(ProjectRouter.Error($"Project '{id}' not found.")));

        app.MapPost("/api/projects/{id}/space/clean", async (string id, ProjectSpaceService space, CancellationToken ct) =>
        {
            if (await space.ReportAsync(id, ct) is null) return Results.NotFound(ProjectRouter.Error($"Project '{id}' not found."));
            var result = await space.CleanAsync(id, ct);
            Log.Information("Project {ProjectId} space clean-up freed {Bytes} bytes", id, result.FreedBytes);
            return Results.Ok(result);
        });

        app.MapPost("/api/projects/{id}/local-cleanup-policy", (string id, PolicyRequest req, IProjectStore store) =>
            HandleSetPolicyAsync(id, req, store));
    }

    /// <summary>
    /// "Delete images as soon as they are pushed" for the whole project in one switch: every service gets a keep
    /// count of 0 (and is switched back on if it had opted out). Off returns to the default of keeping the newest two.
    /// </summary>
    internal static async Task<IResult> HandleSetPolicyAsync(string id, PolicyRequest req, IProjectStore store)
    {
        if (req.DeleteAfterPush is null)
            return Results.BadRequest(ProjectRouter.Error("Say whether images should be deleted after they are pushed.", "deleteAfterPush"));

        var project = await store.GetByIdAsync(id);
        if (project is null)
            return Results.NotFound(ProjectRouter.Error($"Project '{id}' not found."));

        var on = req.DeleteAfterPush.Value;
        var services = project.Services
            .Select(s => s with
            {
                LocalImageKeepCount = on ? 0 : BuildMachineImagePruner.DefaultLocalImageKeepCount,
                SkipLocalCleanup = on ? false : s.SkipLocalCleanup,
            })
            .ToList();
        await store.SaveAsync(project with { Services = services, ModifiedAt = DateTime.UtcNow });
        Log.Information("Project {ProjectId}: delete images after push = {On}", id, on);
        return Results.Ok(new { deleteAfterPush = on });
    }
}
