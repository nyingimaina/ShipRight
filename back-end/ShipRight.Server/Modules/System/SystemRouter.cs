using Serilog;

namespace ShipRight.Modules.System;

public static class SystemRouter
{
    public static void MapSystemRoutes(this WebApplication app)
    {
        app.MapGet("/api/system/stats", (IHostStatsProvider stats, CancellationToken ct) => stats.GetAsync(ct));

        app.MapPost("/api/system/shutdown", async (IHostApplicationLifetime lifetime) =>
        {
            Log.Information("Shutdown requested via API");
            await Task.Delay(100);
            lifetime.StopApplication();
            return Results.Ok(new { status = "shutting_down" });
        });
    }
}
