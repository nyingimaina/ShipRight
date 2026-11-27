namespace ShipRight.Modules.BootstrapDeployment;

/// <summary>
/// Writing a deployment configuration from scratch. Mounted only in desktop mode, beside the build-on-server routes it
/// feeds, so an established cloud install is untouched by this feature.
/// </summary>
public static class BootstrapDeploymentRoutes
{
    public static void MapBootstrapDeploymentRoutes(this WebApplication app)
    {
        app.MapPost("/api/bootstrap/plan", (BootstrapRequest body) => HandlePlan(body));
        app.MapPost("/api/bootstrap/apply", (BootstrapRequest body, BootstrapApplier applier, CancellationToken ct) =>
            HandleApplyAsync(body, applier, ct));
    }

    internal static IResult HandlePlan(BootstrapRequest body) =>
        Results.Ok(BootstrapPlanner.Plan(body));

    internal static async Task<IResult> HandleApplyAsync(
        BootstrapRequest body, BootstrapApplier applier, CancellationToken ct = default)
    {
        var plan = BootstrapPlanner.Plan(body);
        if (!plan.Ok)
            return Results.BadRequest(new
            {
                isError = true,
                message = plan.Problems[0],
                problems = plan.Problems,
                plan,
            });

        var result = await applier.ApplyAsync(plan, ct);
        return result.Ok
            ? Results.Ok(new { result, plan })
            : Results.BadRequest(new
            {
                isError = true,
                message = result.Problems.FirstOrDefault() ?? "The deployment could not be written.",
                problems = result.Problems,
            });
    }
}
