using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace ShipRight.Modules.Scheduler;

/// <summary>
/// Scheduler history routes for cloud mode. They bind MariaDbBackupHistoryStore,
/// which is registered in the cloud DI container; the desktop BackupHistoryStore
/// routes in SchedulerRouter are not mapped in cloud mode because that store is
/// not registered there (minimal-API metadata creation would fail).
/// </summary>
public static class CloudSchedulerRouter
{
    public static void MapCloudSchedulerHistoryRoutes(this WebApplication app)
    {
        app.MapGet("/api/scheduler/history", async (
            MariaDbBackupHistoryStore store,
            DateTime? since = null, DateTime? until = null,
            string? projectId = null, string? status = null,
            int limit = 100) =>
        {
            var records = await store.Query(since, until, projectId, status, limit);
            return Results.Ok(new { records, count = records.Count });
        });

        app.MapGet("/api/scheduler/history/summary", async (
            MariaDbBackupHistoryStore store,
            DateTime? since = null, DateTime? until = null) =>
        {
            var summary = await store.GetSummary(since, until);
            return Results.Ok(summary);
        });

        app.MapGet("/api/scheduler/history/daily", async (
            MariaDbBackupHistoryStore store,
            int days = 30) =>
        {
            var reports = await store.GetDailyReport(days);
            return Results.Ok(new { reports, days });
        });

        app.MapGet("/api/scheduler/history/by-project", async (
            MariaDbBackupHistoryStore store) =>
        {
            var reports = await store.GetProjectReports();
            return Results.Ok(new { reports, count = reports.Count });
        });
    }
}
