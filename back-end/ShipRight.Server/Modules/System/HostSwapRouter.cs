using Serilog;

namespace ShipRight.Modules.System;

/// <summary>Header swap status and the Admin-only action that creates swap on the metal host.</summary>
public static class HostSwapRouter
{
    public static void MapHostSwapRoutes(this WebApplication app)
    {
        app.MapGet("/api/system/host-swap", async (
            IHostSwapStatsProvider stats, IHostSwapTargetProvider targets, CancellationToken ct) =>
        {
            var snapshot = await stats.GetAsync(ct);
            return Results.Ok(new
            {
                swapUsedBytes = snapshot.SwapUsedBytes,
                swapTotalBytes = snapshot.SwapTotalBytes,
                swapUsedPercent = snapshot.SwapUsedPercent,
                available = snapshot.Available,
                configured = targets.Get().IsConfigured,
            });
        });

        app.MapPost("/api/system/host-swap", async (
            HostSwapRequest? body, HostSwapService swap, CancellationToken ct) =>
        {
            try
            {
                var result = await swap.CreateAsync(body?.SizeGb, ct);
                Log.Information("Host swap creation ({Gb} GB): success={Success}", body?.SizeGb ?? 2, result.Success);
                return Results.Ok(new { success = result.Success, output = result.Output });
            }
            catch (HostSwapNotConfiguredException ex)
            {
                return Results.Json(new { isError = true, message = ex.Message }, statusCode: StatusCodes.Status501NotImplemented);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                return Results.BadRequest(new { isError = true, message = ex.Message });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Host swap creation failed");
                return Results.Json(new { isError = true, message = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        });
    }

    private sealed record HostSwapRequest(int? SizeGb);
}
