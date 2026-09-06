namespace ShipRight.Modules.WatchBranch;

/// <summary>
/// Watch-branch history route for cloud mode. It binds MariaDbWatchBranchHistoryStore,
/// which is registered in the cloud DI container; the desktop WatchBranchHistoryStore
/// (JSON file-based) is not registered there, so it would fail request-delegate factory
/// metadata creation at first request.
/// </summary>
public static class CloudWatchBranchRouter
{
    public static void MapCloudWatchBranchHistoryRoutes(this WebApplication app)
    {
        app.MapGet("/api/watch-branch/history", async (
            MariaDbWatchBranchHistoryStore store,
            string? projectId = null, string? status = null, int limit = 100) =>
        {
            var records = await store.Query(projectId, status, limit);
            return Results.Ok(new { records, count = records.Count });
        });
    }
}
