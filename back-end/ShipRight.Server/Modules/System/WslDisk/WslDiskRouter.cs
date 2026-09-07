using Serilog;

namespace ShipRight.Modules.System.WslDisk;

public static class WslDiskRouter
{
    public static void MapWslDiskRoutes(this WebApplication app)
    {
        app.MapGet("/api/system/wsl-disk", async (WslDiskService service, CancellationToken ct) =>
            Results.Ok(await service.GetReportAsync(ct)));

        app.MapPost("/api/system/wsl-disk/compact", async (WslDiskService service, CancellationToken ct) =>
        {
            var result = await service.CompactAsync(ct, msg => Log.Information("[wsl-disk] {Message}", msg));
            return result.Succeeded
                ? Results.Ok(result)
                : Results.Json(new { error = result.BlockedReason, messages = result.Messages }, statusCode: 409);
        });
    }
}