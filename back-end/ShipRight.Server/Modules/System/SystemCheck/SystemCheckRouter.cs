namespace ShipRight.Modules.System.SystemCheck;

public record SystemFixRequest(string? FixId);

public static class SystemCheckRouter
{
    public static void MapSystemCheckRoutes(this WebApplication app)
    {
        app.MapGet("/api/system/check", async (SystemCheckService service, CancellationToken ct) =>
            Results.Ok(await service.CheckAsync(ct)));

        app.MapPost("/api/system/check/fix", async (SystemFixRequest req, SystemCheckService service, CancellationToken ct) =>
            string.IsNullOrWhiteSpace(req.FixId)
                ? Results.BadRequest(new { error = "Say which repair to run.", field = "fixId" })
                : Results.Ok(await service.FixAsync(req.FixId, ct)));
    }
}
