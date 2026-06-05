using ShipRight.Modules.Builds;
using ShipRight.Modules.Projects;
using ShipRight.Shared.Store;

namespace ShipRight.Modules.System;

public static class HealthRouter
{
    private static readonly DateTime StartedAt = DateTime.UtcNow;

    public static void MapHealthRoutes(this WebApplication app)
    {
        app.MapGet("/api/health", (IProjectStore projectStore, IBuildStore buildStore) =>
            Results.Ok(new
            {
                status = "healthy",
                version = "1.0.0",
                startedAt = StartedAt,
                dataDirectory = DataDirectory.Resolve(),
                projectCount = projectStore.Count,
                buildCount = buildStore.Count
            }));
    }
}
