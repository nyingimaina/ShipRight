using Serilog;
using ShipRight.Modules.Projects;
using ShipRight.Modules.Servers;

namespace ShipRight.Modules.JitDeploy;

/// <summary>Which copy of the server's variables should become the single source of truth.</summary>
public sealed class EnvDivergenceResolution
{
    /// <summary>to-project, to-server, or none (keep both).</summary>
    public string Copy { get; set; } = "none";
}

/// <summary>
/// Deploy-time check for the server settings drifting between the server record and the project. This is separate from the
/// duplicate-variable check: that one compares the two sides JIT merges, this one compares the two copies of one side.
/// </summary>
public static class JitEnvDivergenceRoutes
{
    private sealed record DivergenceView(
        bool Differs,
        bool ServerFound,
        IReadOnlyList<EnvSideDifference> Variables,
        string ServerRecordText,
        string ProjectText);

    public static void MapJitEnvDivergenceRoutes(this WebApplication app)
    {
        app.MapGet("/api/jit/{id}/env-divergence", (string id, IProjectStore projects, IServerStore servers) =>
            HandleGetAsync(id, projects, servers));
        app.MapPost("/api/jit/{id}/env-divergence/resolve", (string id, EnvDivergenceResolution body, IProjectStore projects, IServerStore servers) =>
            HandleResolveAsync(id, body, projects, servers));
    }

    internal static async Task<IResult> HandleGetAsync(string id, IProjectStore projects, IServerStore servers)
    {
        var project = await projects.GetByIdAsync(id);
        if (project is null) return Results.NotFound(ProjectRouter.Error($"Project '{id}' not found."));
        return Results.Ok(View(project, await FindServerAsync(project, servers)));
    }

    internal static async Task<IResult> HandleResolveAsync(string id, EnvDivergenceResolution body, IProjectStore projects, IServerStore servers)
    {
        var project = await projects.GetByIdAsync(id);
        if (project is null) return Results.NotFound(ProjectRouter.Error($"Project '{id}' not found."));
        var server = await FindServerAsync(project, servers);
        var choice = (body.Copy ?? "none").Trim().ToLowerInvariant();

        if (choice == "none") return Results.Ok(View(project, server));
        if (choice is not ("to-project" or "to-server"))
            return Results.BadRequest(ProjectRouter.Error("Choose which side to copy the variables to.", "copy"));
        if (server is null)
            return Results.BadRequest(ProjectRouter.Error("This project has no server record to change."));

        if (choice == "to-project")
        {
            var saved = project with { Server = project.Server with { EnvFile = server.EnvFile } };
            await projects.SaveAsync(saved);
            Log.Information("Project {ProjectId}: copied the server record's variables into the project's server settings", id);
            return Results.Ok(View(saved, server));
        }

        var savedServer = server with { EnvFile = project.Server.EnvFile };
        // Go through the server route so every project pointing at this server is refreshed, not just this one.
        await ServerRouter.HandleUpdateAsync(server.Id, savedServer, servers, projects);
        Log.Information("Project {ProjectId}: copied the project's server variables onto the server record", id);
        return Results.Ok(View(project, savedServer));
    }

    private static async Task<ServerConfig?> FindServerAsync(ProjectConfig project, IServerStore servers)
    {
        var id = string.IsNullOrWhiteSpace(project.Server.Id) ? project.ServerId : project.Server.Id;
        return string.IsNullOrWhiteSpace(id) ? null : await servers.GetByIdAsync(id);
    }

    private static DivergenceView View(ProjectConfig project, ServerConfig? server)
    {
        var d = JitEnvDivergence.Detect(project.Server.EnvFile, server?.EnvFile);
        return new DivergenceView(d.Differs, server is not null, d.Variables, d.ServerRecordText, d.ProjectText);
    }
}
