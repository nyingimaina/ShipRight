namespace ShipRight.Modules.System.DockerCleanup;

public static class DockerCleanupRouter
{
    public static void MapDockerCleanupRoutes(this WebApplication app)
    {
        app.MapGet("/api/system/docker-cleanup", async (DockerCleanupService service, CancellationToken ct) =>
            Results.Ok(await service.PreviewAsync(ct)));

        app.MapPost("/api/system/docker-cleanup/run", async (CleanupRequest request, DockerCleanupService service, CancellationToken ct) =>
            Results.Ok(await service.RunAsync(request, ct)));
    }
}
