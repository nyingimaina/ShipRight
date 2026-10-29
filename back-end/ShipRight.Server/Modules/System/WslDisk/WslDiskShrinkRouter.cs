using Serilog;

namespace ShipRight.Modules.System.WslDisk;

public static class WslDiskShrinkRouter
{
    public static void MapWslDiskShrinkRoutes(this WebApplication app)
    {
        // Same job as /compact, but also works on Windows Home (diskpart fallback).
        app.MapPost("/api/system/wsl-disk/shrink", async (WslDiskShrinker shrinker, CancellationToken ct) =>
        {
            var result = await shrinker.ShrinkAsync(ct, msg => Log.Information("[wsl-disk] {Message}", msg));
            return result.Succeeded
                ? Results.Ok(result)
                : Results.Json(new { error = result.BlockedReason, messages = result.Messages, files = result.Files }, statusCode: 409);
        });
    }
}
