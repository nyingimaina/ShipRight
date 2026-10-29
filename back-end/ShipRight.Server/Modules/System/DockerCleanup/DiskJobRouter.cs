using ShipRight.Modules.Builds;

namespace ShipRight.Modules.System.DockerCleanup;

public static class DiskJobRouter
{
    public static void MapDiskJobRoutes(this WebApplication app)
    {
        app.MapPost("/api/system/disk-job", (DiskJobRequest request, DiskCleanupJobService jobs) =>
            Results.Accepted("/api/system/disk-job/latest", jobs.Start(request)));

        app.MapGet("/api/system/disk-job/latest", (DiskCleanupJobService jobs) =>
            jobs.Latest() is { } job ? Results.Ok(job) : Results.NoContent());

        app.MapGet("/api/system/disk-job/{id}", (string id, DiskCleanupJobService jobs) =>
            jobs.Get(id) is { } job ? Results.Ok(job) : Results.NotFound());

        // The builds that stop the Windows disk from being shrunk, so the page can offer to abort them.
        app.MapGet("/api/system/wsl-disk/blockers", async (IBuildStore builds) =>
            Results.Ok(await ActiveBuilds.ListAsync(builds)));
    }
}

public static class DiskJobRegistration
{
    /// <summary>Wires the job to the real shrinker and to the real size of the WSL disk files.</summary>
    public static DiskCleanupJobService Create(IServiceProvider sp) => new(
        sp.GetRequiredService<DockerCleanupService>(),
        sp.GetRequiredService<IBuildStore>(),
        (ct, log) => sp.GetRequiredService<WslDisk.WslDiskShrinker>().ShrinkAsync(ct, log),
        async ct => (await sp.GetRequiredService<WslDisk.WslDiskService>().GetReportAsync(ct)).VhdxFiles.Sum(f => f.SizeBytes));
}
