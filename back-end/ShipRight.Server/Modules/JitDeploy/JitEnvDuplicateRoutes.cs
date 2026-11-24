using Serilog;
using ShipRight.Modules.Projects;
using ShipRight.Modules.Servers;

namespace ShipRight.Modules.JitDeploy;

/// <summary>What the person chose to do about variables defined on both sides.</summary>
public sealed class EnvDuplicateRemoval
{
    /// <summary>server, project, or none (keep both).</summary>
    public string RemoveFrom { get; set; } = "none";
    public List<string> Names { get; set; } = [];
}

/// <summary>
/// Deploy-time duplicate check. Values are returned only here, on explicit request: the deploy log and the config
/// summary keep their names-only rule. Nothing is saved until the person picks a side.
/// </summary>
public static class JitEnvDuplicateRoutes
{
    private sealed record DuplicatesView(IReadOnlyList<EnvDuplicate> Duplicates, bool Skipped, string? Note);

    public static void MapJitEnvDuplicateRoutes(this WebApplication app)
    {
        app.MapGet("/api/jit/{id}/env-duplicates", (string id, IProjectStore projects, IJitConfigStore configs) =>
            HandleGetDuplicatesAsync(id, projects, configs));
        app.MapPost("/api/jit/{id}/env-duplicates/remove", (string id, EnvDuplicateRemoval body, IProjectStore projects, IJitConfigStore configs, IServerStore servers) =>
            HandleRemoveAsync(id, body, projects, configs, servers));
    }

    internal static async Task<IResult> HandleGetDuplicatesAsync(string id, IProjectStore projects, IJitConfigStore configs)
    {
        var project = await projects.GetByIdAsync(id);
        if (project is null) return Results.NotFound(ProjectRouter.Error($"Project '{id}' not found."));
        var cfg = await configs.GetAsync(id) ?? new JitDeployConfig();
        return Results.Ok(View(project, cfg));
    }

    internal static async Task<IResult> HandleRemoveAsync(string id, EnvDuplicateRemoval body, IProjectStore projects, IJitConfigStore configs, IServerStore servers)
    {
        var project = await projects.GetByIdAsync(id);
        if (project is null) return Results.NotFound(ProjectRouter.Error($"Project '{id}' not found."));
        var cfg = await configs.GetAsync(id) ?? new JitDeployConfig();
        var view = View(project, cfg);
        var choice = (body.RemoveFrom ?? "none").Trim().ToLowerInvariant();

        if (choice == "none") return Results.Ok(view);
        if (choice is not ("server" or "project"))
            return Results.BadRequest(ProjectRouter.Error("Choose which side to remove the duplicate variables from.", "removeFrom"));
        if (view.Skipped)
            return Results.BadRequest(ProjectRouter.Error("This project uses its own env file, so there are no variables to remove."));
        if (body.Names is null || body.Names.Count == 0)
            return Results.BadRequest(ProjectRouter.Error("Choose at least one duplicate variable.", "names"));

        var actual = view.Duplicates.Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
        var names = body.Names.Where(actual.Contains).Distinct(StringComparer.Ordinal).ToList();
        if (names.Count == 0)
            return Results.BadRequest(ProjectRouter.Error("Those variables are no longer duplicated. Reload and try again.", "names"));

        if (choice == "project")
        {
            var updated = cfg with { EnvText = JitEnvDuplicates.Remove(cfg.EnvText, names) };
            var problems = JitEnvConfigValidator.Validate(updated);
            if (problems.Count > 0)
                return Results.BadRequest(new { isError = true, message = problems[0].Message, errors = problems.Select(p => ProjectRouter.Error(p.Message, p.Field)).ToList() });
            await configs.SaveAsync(id, updated);
            Log.Information("Project {ProjectId}: removed duplicate variables {Names} from the project's variables", id, string.Join(", ", names));
            return Results.Ok(View(project, updated));
        }

        var serverText = JitEnvDuplicates.Remove(project.Server.EnvFile, names);
        var saved = project with { Server = project.Server with { EnvFile = serverText } };
        await projects.SaveAsync(saved);

        var serverId = string.IsNullOrWhiteSpace(project.Server.Id) ? project.ServerId : project.Server.Id;
        if (!string.IsNullOrWhiteSpace(serverId) && await servers.GetByIdAsync(serverId) is { } server)
            await servers.SaveAsync(server with { EnvFile = serverText });

        Log.Information("Project {ProjectId}: removed duplicate variables {Names} from the server's variables", id, string.Join(", ", names));
        return Results.Ok(View(saved, cfg));
    }

    private static DuplicatesView View(ProjectConfig project, JitDeployConfig cfg)
    {
        if (!string.IsNullOrWhiteSpace(cfg.EnvFile))
            return new DuplicatesView([], true, "This project uses its own env file, so the server and project variables are not merged.");
        return new DuplicatesView(JitEnvDuplicates.Detect(project.Server.EnvFile, cfg.EnvText), false, null);
    }
}
