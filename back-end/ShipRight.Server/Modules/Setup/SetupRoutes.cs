using ShipRight.Modules.Projects;
using ShipRight.Shared.ProcessRunner;
using Serilog;

namespace ShipRight.Modules.Setup;

public record SetupPlanRequest(string? RootPath);
public record SetupApplyRequest(string? RootPath, IReadOnlyCollection<string>? ApprovedItemIds, bool Commit);
public record TreeStatusRequest(string? RepoPath);
public record CommitRequest(string? RepoPath, string? Message, IReadOnlyCollection<string>? Paths);

public static class SetupRoutes
{
    public static void MapSetupRoutes(this WebApplication app)
    {
        app.MapPost("/api/setup/plan", (SetupPlanRequest req) => HandlePlan(req));

        app.MapPost("/api/setup/apply", (SetupApplyRequest req, IProcessRunner runner, CancellationToken ct) =>
            HandleApplyAsync(req, runner, ct));

        app.MapPost("/api/setup/tree-status", (TreeStatusRequest req, IProcessRunner runner, CancellationToken ct) =>
            HandleTreeStatusAsync(req, runner, ct));

        app.MapGet("/api/setup/wsl-home", (IProcessRunner runner, CancellationToken ct) =>
            HandleWslHomeAsync(runner, ct));

        app.MapPost("/api/setup/commit", (CommitRequest req, IProcessRunner runner, CancellationToken ct) =>
            HandleCommitAsync(req, runner, ct));
    }

    internal static IResult HandlePlan(SetupPlanRequest req)
    {
        if (RequireFolder(req.RootPath, "rootPath") is { } bad) return bad;
        return Results.Ok(new { plan = SetupPlanner.CreateDefault().Plan(req.RootPath!) });
    }

    internal static async Task<IResult> HandleApplyAsync(SetupApplyRequest req, IProcessRunner runner, CancellationToken ct)
    {
        if (RequireFolder(req.RootPath, "rootPath") is { } bad) return bad;

        // The plan is rebuilt here and the client only says which of its items to carry out, so a caller can
        // never supply file paths or contents of its own.
        var planner = SetupPlanner.CreateDefault();
        var plan = planner.Plan(req.RootPath!);
        var known = plan.Items.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        var approved = (req.ApprovedItemIds ?? []).Where(known.Contains).ToList();

        var result = await new SetupApplier(runner).ApplyAsync(plan, new ApplyOptions(approved, req.Commit), ct);

        foreach (var r in result.Items.Where(r => r.Outcome == ApplyOutcome.Created))
            Log.Information("Setup created {ItemId} under {Root}", r.ItemId, plan.RepoRoot);

        return Results.Ok(new { result, plan = planner.Plan(req.RootPath!) });
    }

    internal static async Task<IResult> HandleTreeStatusAsync(TreeStatusRequest req, IProcessRunner runner, CancellationToken ct)
    {
        if (RequireFolder(req.RepoPath, "repoPath") is { } bad) return bad;
        return Results.Ok(await new CleanTreeGuard(runner).CheckAsync(req.RepoPath!, ct));
    }

    internal static async Task<IResult> HandleWslHomeAsync(IProcessRunner runner, CancellationToken ct)
    {
        try
        {
            return Results.Ok(new { home = await new BuildSnapshotService(runner).GetWslHomeAsync(ct) });
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(ProjectRouter.Error(ex.Message));
        }
    }

    internal static async Task<IResult> HandleCommitAsync(CommitRequest req, IProcessRunner runner, CancellationToken ct)
    {
        if (RequireFolder(req.RepoPath, "repoPath") is { } bad) return bad;

        var result = await new GitCommitter(runner)
            .CommitAsync(req.RepoPath!, req.Message ?? "", req.Paths ?? [], ct);
        return result.Success
            ? Results.Ok(new { success = true })
            : Results.BadRequest(ProjectRouter.Error(result.Error ?? "The commit failed."));
    }

    private static IResult? RequireFolder(string? path, string field)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Results.BadRequest(ProjectRouter.Error("Choose a folder first.", field));
        if (!Directory.Exists(path))
            return Results.BadRequest(ProjectRouter.Error("That folder does not exist.", field));
        return null;
    }
}
