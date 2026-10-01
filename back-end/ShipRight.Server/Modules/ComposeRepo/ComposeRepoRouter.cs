using ShipRight.Modules.Builds;
using ShipRight.Modules.Projects;
using ShipRight.Shared.Events;

namespace ShipRight.Modules.ComposeRepo;

/// <summary>
/// Project-detail endpoints for the WSL compose repo: inspect its health, set the clone URL
/// it should be restored from, re-clone it, and remove it.
/// <para>
/// Clone/delete are long-running, so they return 202 + an <c>opId</c> and stream progress over
/// the shared <c>/api/repos/ops/{opId}/stream</c> SSE endpoint.
/// </para>
/// </summary>
public static class ComposeRepoRouter
{
    /// <summary>Statuses that mean a build still owns the compose dir.</summary>
    private const string ActiveBuildStatuses = "Pending,Running,Paused,Deploying";

    public static void MapComposeRepoRoutes(this WebApplication app)
    {
        app.MapGet("/api/projects/{id}/compose-repo", async (
            string id, IProjectStore store, ComposeRepoService composeRepo) =>
            await HandleGetStatusAsync(id, store, composeRepo));

        app.MapPost("/api/projects/{id}/compose-repo/clone-url", async (
            string id, SetCloneUrlRequest request, IProjectStore store) =>
            await HandleSetCloneUrlAsync(id, request, store));

        app.MapPost("/api/projects/{id}/compose-repo/clone", async (
            string id, IProjectStore store, IBuildStore buildStore,
            ComposeRepoService composeRepo, BuildEventBus bus) =>
            await HandleStartCloneAsync(id, store, buildStore, composeRepo, bus));

        app.MapPost("/api/projects/{id}/compose-repo/delete", async (
            string id, IProjectStore store, IBuildStore buildStore,
            ComposeRepoService composeRepo, BuildEventBus bus) =>
            await HandleStartDeleteAsync(id, store, buildStore, composeRepo, bus));
    }

    public record SetCloneUrlRequest(string? CloneUrl);

    internal static async Task<IResult> HandleGetStatusAsync(
        string id, IProjectStore store, ComposeRepoService composeRepo)
    {
        var project = await store.GetByIdAsync(id);
        if (project is null)
            return Results.NotFound(new { isError = true, message = $"Project '{id}' not found." });

        if (!ComposeRepoService.AppliesTo(project))
            return Results.Ok(new
            {
                wslPath = project.Wsl.WorkingDir,
                windowsPath = (string?)null,
                exists = false,
                isGitRepo = false,
                cloneUrl = (string?)null,
                health = new { isHealthy = true, summary = "EnvCompose deploy — no compose repo involved." },
                appliesTo = false,
            });

        var status = await composeRepo.GetStatusAsync(project);
        return Results.Ok(new
        {
            status.WslPath,
            status.WindowsPath,
            status.Exists,
            status.IsGitRepo,
            status.CloneUrl,
            health = new { status.Health.IsHealthy, status.Health.Summary },
            appliesTo = true,
        });
    }

    internal static async Task<IResult> HandleSetCloneUrlAsync(
        string id, SetCloneUrlRequest request, IProjectStore store)
    {
        var project = await store.GetByIdAsync(id);
        if (project is null)
            return Results.NotFound(new { isError = true, message = $"Project '{id}' not found." });

        var url = request.CloneUrl?.Trim();
        if (string.IsNullOrEmpty(url))
            return Results.BadRequest(new { isError = true, message = "Clone URL is required." });

        await store.SaveAsync(project with { Wsl = project.Wsl with { ComposeRepoUrl = url } });
        return Results.Ok(new { cloneUrl = url, message = "Compose repo clone URL saved." });
    }

    internal static async Task<IResult> HandleStartCloneAsync(
        string id, IProjectStore store, IBuildStore buildStore,
        ComposeRepoService composeRepo, BuildEventBus bus)
    {
        var project = await store.GetByIdAsync(id);
        if (project is null)
            return Results.NotFound(new { isError = true, message = $"Project '{id}' not found." });

        if (await IsBuildActiveAsync(buildStore, id))
            return Results.Conflict(new
            {
                isError = true,
                message = "A build is currently running or paused for this project. Stop it before touching the compose repo.",
            });

        var url = await composeRepo.ResolveCloneUrlAsync(project);
        if (string.IsNullOrEmpty(url))
            return Results.BadRequest(new
            {
                isError = true,
                message = "No compose-repo URL is known. Set the clone URL on this project first — " +
                          "it cannot be derived from the app repo, which is a different repository.",
            });

        var opId = Guid.NewGuid().ToString("N");
        bus.Register(opId);
        _ = Task.Run(() => RunCloneAsync(project, url!, opId, bus, composeRepo));
        return Results.Accepted($"/api/repos/ops/{opId}/stream",
            new { opId, message = "Compose repo clone started." });
    }

    internal static async Task<IResult> HandleStartDeleteAsync(
        string id, IProjectStore store, IBuildStore buildStore,
        ComposeRepoService composeRepo, BuildEventBus bus)
    {
        var project = await store.GetByIdAsync(id);
        if (project is null)
            return Results.NotFound(new { isError = true, message = $"Project '{id}' not found." });

        if (await IsBuildActiveAsync(buildStore, id))
            return Results.Conflict(new
            {
                isError = true,
                message = "A build is currently running or paused for this project. Stop it before deleting the compose repo.",
            });

        var opId = Guid.NewGuid().ToString("N");
        bus.Register(opId);
        _ = Task.Run(() => RunDeleteAsync(project, opId, bus, composeRepo));
        return Results.Accepted($"/api/repos/ops/{opId}/stream",
            new { opId, message = "Compose repo deletion started." });
    }

    internal static async Task<bool> IsBuildActiveAsync(IBuildStore buildStore, string projectId)
    {
        var active = await buildStore.QueryAsync(
            projectId, ActiveBuildStatuses, null, null, null, page: 1, pageSize: 1);
        return active.Count > 0;
    }

    private static async Task RunCloneAsync(
        ProjectConfig project, string cloneUrl, string opId, BuildEventBus bus, ComposeRepoService composeRepo)
    {
        async Task Log(string message) => await bus.EmitAsync(opId, "log", new { message });
        try
        {
            await Log($"Cloning {cloneUrl} into {project.Wsl.WorkingDir}…");
            await composeRepo.CloneAsync(project, cloneUrl, Log);

            var health = await composeRepo.CheckHealthAsync(project.Wsl.WorkingDir);
            await Log(health.IsHealthy
                ? "Compose repo cloned and git fsck is clean."
                : $"Clone finished, but git fsck still reports: {health.Summary}");

            await composeRepo.PersistCloneUrlAsync(project, cloneUrl);
            await bus.EmitAsync(opId, "Completed", new { message = "Compose repo cloned." });
        }
        catch (Exception ex)
        {
            await bus.EmitAsync(opId, "Error", new { message = ex.Message });
        }
        finally
        {
            bus.Complete(opId);
        }
    }

    private static async Task RunDeleteAsync(
        ProjectConfig project, string opId, BuildEventBus bus, ComposeRepoService composeRepo)
    {
        async Task Log(string message) => await bus.EmitAsync(opId, "log", new { message });
        try
        {
            // Remember where it came from before it is gone, so a later clone needs no guesswork.
            var url = await composeRepo.ResolveCloneUrlAsync(project);
            if (!string.IsNullOrEmpty(url)) await composeRepo.PersistCloneUrlAsync(project, url);

            await Log($"Removing compose repo {project.Wsl.WorkingDir}…");
            var archive = await composeRepo.ArchiveAsync(project, Log);
            await Log(archive is null
                ? "Nothing to remove — the compose directory is already gone."
                : $"Removed. To restore it: git clone {url} {project.Wsl.WorkingDir}");

            await bus.EmitAsync(opId, "Completed", new { message = "Compose repo removed." });
        }
        catch (Exception ex)
        {
            await bus.EmitAsync(opId, "Error", new { message = ex.Message });
        }
        finally
        {
            bus.Complete(opId);
        }
    }
}
