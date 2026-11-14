using System.Security.Claims;
using Serilog;
using ShipRight.Modules.Projects;

namespace ShipRight.Modules.JitDeploy;

public record RollbackRequest(string? Tag);

/// <summary>Endpoints for build-on-server deploys. They live under their own path, so no existing endpoint changes.</summary>
public static class JitDeployRoutes
{
    public static void MapJitDeployRoutes(this WebApplication app)
    {
        static string? Who(ClaimsPrincipal? u) => u?.FindFirst("display_name")?.Value ?? u?.FindFirst(ClaimTypes.Name)?.Value ?? u?.Identity?.Name;

        app.MapGet("/api/jit/{id}/config", (string id, IProjectStore projects, IJitConfigStore configs) =>
            HandleGetConfigAsync(id, projects, configs));
        app.MapPut("/api/jit/{id}/config", (string id, JitDeployConfig body, IProjectStore projects, IJitConfigStore configs) =>
            HandleSaveConfigAsync(id, body, projects, configs));
        app.MapPost("/api/jit/{id}/deploy", (string id, JitDeployOptions? options, ClaimsPrincipal user, JitDeployService svc) =>
            HandleDeployAsync(id, options, Who(user), svc));
        app.MapPost("/api/jit/{id}/recover", (string id, ClaimsPrincipal user, JitDeployService svc) =>
            HandleRecoverAsync(id, Who(user), svc));
        app.MapPost("/api/jit/{id}/rollback", (string id, RollbackRequest req, ClaimsPrincipal user, JitDeployService svc) =>
            HandleRollbackAsync(id, req, Who(user), svc));
        app.MapGet("/api/jit/{id}/releases", (string id, JitDeployService svc) => HandleReleasesAsync(id, svc));
        app.MapGet("/api/jit/{id}/dependents", (string id, JitDeployService svc) => HandleDependentsAsync(id, svc));
    }

    private static object Err(string message, string? field = null) => ProjectRouter.Error(message, field);

    internal static async Task<IResult> HandleGetConfigAsync(string id, IProjectStore projects, IJitConfigStore configs)
    {
        var project = await projects.GetByIdAsync(id);
        if (project is null) return Results.NotFound(Err($"Project '{id}' not found."));

        JitDeployConfig cfg;
        try { cfg = await configs.GetAsync(id) ?? new JitDeployConfig(); }
        catch (InvalidDataException ex) { return Results.Problem(ex.Message); }

        var services = JitDeployMapper.Services(project).Select(s => new
        {
            name = s.Name,
            tagVariable = s.TagVariable,
            imageRef = s.ImageRef,
            composeImageLine = $"${{{s.TagVariable}:-{s.ImageRef}:latest}}",
        }).ToList();

        var problems = cfg.Enabled ? JitDeployMapper.Map(project, cfg).Problems : [];
        // A saved address may predate the rule against secrets in addresses: never send one to the screen.
        var shown = cfg with { RepoUrl = GitAddressForms.Analyse(cfg.RepoUrl).Original };
        return Results.Ok(new { config = shown, services, problems });
    }

    internal static async Task<IResult> HandleSaveConfigAsync(string id, JitDeployConfig body, IProjectStore projects, IJitConfigStore configs)
    {
        if (await projects.GetByIdAsync(id) is null) return Results.NotFound(Err($"Project '{id}' not found."));

        var problems = JitDeployConfigValidator.Validate(body);
        if (problems.Count > 0)
            return Results.BadRequest(new { isError = true, message = problems[0].Message, errors = problems.Select(p => Err(p.Message, p.Field)).ToList() });

        try { await configs.SaveAsync(id, body); }
        catch (InvalidDataException ex) { return Results.Problem(ex.Message); }
        Log.Information("Project {ProjectId}: build-on-server settings saved (enabled={Enabled})", id, body.Enabled);
        return Results.Ok(new { config = body });
    }

    internal static async Task<IResult> HandleDeployAsync(string id, JitDeployOptions? options, string? initiator, JitDeployService svc) =>
        Started(await svc.StartDeployAsync(id, options, initiator), "Deploy started");

    internal static async Task<IResult> HandleRecoverAsync(string id, string? initiator, JitDeployService svc) =>
        Started(await svc.StartRecoverAsync(id, initiator), "Recovery started");

    internal static async Task<IResult> HandleRollbackAsync(string id, RollbackRequest req, string? initiator, JitDeployService svc)
    {
        if (string.IsNullOrWhiteSpace(req.Tag))
            return Results.BadRequest(Err("Choose which release to go back to.", "tag"));
        return Started(await svc.StartRollbackAsync(id, req.Tag.Trim(), initiator), "Rollback started");
    }

    internal static async Task<IResult> HandleReleasesAsync(string id, JitDeployService svc) =>
        Results.Ok(await svc.GetReleasesAsync(id));

    internal static async Task<IResult> HandleDependentsAsync(string id, JitDeployService svc)
    {
        var view = await svc.GetDependentsAsync(id);
        return view.NotFound ? Results.NotFound(Err(view.Note ?? "Project not found.")) : Results.Ok(view);
    }

    private static IResult Started(JitStartResult r, string message)
    {
        if (r.NotFound) return Results.NotFound(Err(r.Error!));
        if (r.Error is not null) return Results.BadRequest(new { isError = true, message = r.Error });
        return Results.Accepted($"/api/builds/{r.Record!.Id}", new { buildId = r.Record.Id, status = r.Record.Status.ToString(), message });
    }
}
