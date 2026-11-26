using Jattac.Libs.Tempo;
using Jattac.Libs.Tempo.Scheduling;
using Serilog;
using ShipRight.Modules.Projects;

namespace ShipRight.Modules.Scheduler;

public static class SchedulerRouter
{
    public static void MapSchedulerRoutes(this WebApplication app)
    {
        MapProjectScheduleRoutes(app);
        app.MapGet("/api/scheduler/inflight", (HttpContext context) =>
        {
            var queue = context.RequestServices
                .GetRequiredService<TempoQueue<TempoScheduledWork<BackupJob>>>();
            var inflight = queue.Inflight;
            if (inflight is null)
                return Results.Ok(new { inflight = (object?)null });

            return Results.Ok(new
            {
                inflight = new
                {
                    tenantId = inflight.TenantId,
                    correlationId = inflight.CorrelationId,
                    projectId = inflight.Work.Job.ProjectId,
                    projectName = inflight.Work.Job.ProjectName,
                    databaseName = inflight.Work.Job.DatabaseName,
                    scheduledAt = inflight.Work.ScheduledAt,
                    enqueuedAt = inflight.EnqueuedAt,
                    startedAt = inflight.StartedAt,
                    waitMs = (long)inflight.WaitTime.TotalMilliseconds,
                    elapsedMs = (long)inflight.ElapsedProcessingTime.TotalMilliseconds,
                }
            });
        });

        app.MapGet("/api/scheduler/pending", (HttpContext context, int depth = 10) =>
        {
            var queue = context.RequestServices
                .GetRequiredService<TempoQueue<TempoScheduledWork<BackupJob>>>();
            var snapshot = queue.GetQueueSnapshot(depth);
            var items = snapshot.Select(x => new
            {
                tenantId = x.TenantId,
                priority = x.Priority,
                projectId = x.Work.Job.ProjectId,
                projectName = x.Work.Job.ProjectName,
                correlationId = x.CorrelationId,
                scheduledAt = x.Work.ScheduledAt,
                enqueuedAt = x.EnqueuedAt,
                position = x.PositionWithinTenant,
                waitMs = (long)x.WaitTime.TotalMilliseconds,
            }).OrderBy(x => x.enqueuedAt).ToList();

            return Results.Ok(new { pending = items, count = items.Count });
        });

        app.MapGet("/api/scheduler/snapshot", (HttpContext context) =>
        {
            var queue = context.RequestServices
                .GetRequiredService<TempoQueue<TempoScheduledWork<BackupJob>>>();
            var snap = queue.Observability.GetSnapshot();
            return Results.Ok(new
            {
                totalPending = snap.TotalPending,
                throughputPerSecond = Math.Round(snap.ThroughputPerSecond, 1),
                enqueueRatePerSecond = Math.Round(snap.EnqueueRatePerSecond, 1),
                totalProcessed = snap.TotalProcessedSinceStart,
                totalEnqueued = snap.TotalEnqueuedSinceStart,
                totalFailures = snap.TotalFailuresSinceStart,
                failureRatePerSecond = Math.Round(snap.FailureRatePerSecond, 2),
                medianWaitMs = snap.MedianWaitTime.TotalMilliseconds,
                p95WaitMs = snap.P95WaitTime.TotalMilliseconds,
                p99WaitMs = snap.P99WaitTime.TotalMilliseconds,
                medianProcessingMs = snap.MedianProcessingTime.TotalMilliseconds,
                p95ProcessingMs = snap.P95ProcessingTime.TotalMilliseconds,
                tokenBucketUtilization = Math.Round(snap.TokenBucketUtilization, 2),
                healthStatus = snap.HealthStatus.ToString(),
                isDispatcherRunning = snap.IsDispatcherRunning,
                isBacklogGrowing = snap.IsBacklogGrowing,
                lastSuccessAt = snap.LastSuccessAt,
                lastFailureAt = snap.LastFailureAt,
                lastFailureError = snap.LastFailureError,
                circuitBrokenTenants = snap.CircuitBrokenTenants.Select(t => t.ToString()).ToList(),
                pendingByTenant = snap.PendingByTenant.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
                capturedAt = snap.SnapshotTakenAtUtc,
            });
        });

        app.MapGet("/api/scheduler/history", (
            SqliteBackupHistoryStore store,
            DateTime? since = null, DateTime? until = null,
            string? projectId = null, string? status = null,
            int limit = 100) =>
        {
            var records = store.Query(since, until, projectId, status, limit);
            return Results.Ok(new { records, count = records.Count });
        });

        app.MapGet("/api/scheduler/history/summary", (
            SqliteBackupHistoryStore store,
            DateTime? since = null, DateTime? until = null) =>
        {
            var summary = store.GetSummary(since, until);
            return Results.Ok(summary);
        });

        app.MapGet("/api/scheduler/history/daily", (
            SqliteBackupHistoryStore store,
            int days = 30) =>
        {
            var reports = store.GetDailyReport(days);
            return Results.Ok(new { reports, days });
        });

        app.MapGet("/api/scheduler/history/by-project", (
            SqliteBackupHistoryStore store) =>
        {
            var reports = store.GetProjectReports();
            return Results.Ok(new { reports, count = reports.Count });
        });

        app.MapGet("/api/scheduler/schedules", async (HttpContext context) =>
        {
            var scheduler = context.RequestServices
                .GetRequiredService<TempoScheduler<BackupJob>>();
            var definitions = await context.RequestServices
                .GetRequiredService<IBackupScheduleStore>().GetAllAsync();
            var byRuntime = definitions
                .Where(d => d.RuntimeScheduleId is not null)
                .ToDictionary(d => d.RuntimeScheduleId!.Value);

            var snapshot = scheduler.GetSnapshot();
            var jobs = snapshot.Jobs.Select(j =>
            {
                byRuntime.TryGetValue(j.ScheduleId, out var definition);
                return new
                {
                    scheduleId = j.ScheduleId,
                    definitionId = definition?.Id,
                    projectId = definition?.ProjectId,
                    projectName = definition?.ProjectName,
                    cron = definition?.Cron,
                    timeZone = definition?.TimeZone,
                    missedRunPolicy = definition?.MissedRunPolicy,
                    overlapPolicy = definition?.OverlapPolicy,
                    maxDuration = definition?.MaxDuration,
                    jobName = j.JobName,
                    scheduleType = j.Schedule.GetType().Name,
                    scheduleDescription = DescribeSchedule(j.Schedule),
                    lastFiredAt = j.LastFiredAt,
                    nextFireAt = j.NextFireAt,
                    inFlightCount = j.InFlightCount,
                    totalFired = j.TotalFired,
                    totalMissed = j.TotalMissed,
                    lastDrift = j.LastDrift,
                };
            }).ToList();

            return Results.Ok(new { jobs, count = jobs.Count, capturedAt = snapshot.CapturedAt });
        });

        app.MapPost("/api/scheduler/schedules", async (
            ScheduleRequest request,
            IProjectStore projectStore,
            HttpContext context) =>
        {
            var scheduler = context.RequestServices
                .GetRequiredService<TempoScheduler<BackupJob>>();
            var project = await projectStore.GetByIdAsync(request.ProjectId);
            if (project is null)
                return Results.NotFound(new { isError = true, message = $"Project '{request.ProjectId}' not found." });

            if (project.Database is null || string.IsNullOrWhiteSpace(project.Database.DatabaseName))
                return Results.BadRequest(new { isError = true, message = "Project has no database configuration." });

            if (string.IsNullOrWhiteSpace(request.CronExpression))
                return Results.BadRequest(new { isError = true, message = "cronExpression is required." });

            try
            {
                var definition = new BackupScheduleDefinition
                {
                    ProjectId = project.Id,
                    ProjectName = project.Name,
                    DatabaseName = project.Database.DatabaseName,
                    Cron = request.CronExpression,
                    TimeZone = request.TimeZone ?? project.TimeZone,
                    MissedRunPolicy = request.MissedRunPolicy.ToString(),
                    OverlapPolicy = request.OverlapPolicy.ToString(),
                    MaxDuration = request.MaxDuration?.ToString(),
                };

                var store = context.RequestServices.GetRequiredService<IBackupScheduleStore>();
                var scheduleId = SchedulerRegistration.Register(scheduler, definition);
                await store.SaveAsync(definition with { RuntimeScheduleId = scheduleId });

                Log.Information("Backup schedule saved for project {Project}: cron \"{Cron}\"",
                    project.Name, request.CronExpression);

                return Results.Ok(new { scheduleId, message = "Schedule registered." });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to register schedule for project {ProjectId}", request.ProjectId);
                return Results.BadRequest(new { isError = true, message = ex.Message });
            }
        });

        app.MapDelete("/api/scheduler/schedules/{scheduleId}", (
            Guid scheduleId,
            HttpContext context) =>
        {
            var scheduler = context.RequestServices
                .GetRequiredService<TempoScheduler<BackupJob>>();
            scheduler.Unregister(scheduleId);
            var store = context.RequestServices.GetRequiredService<IBackupScheduleStore>();
            store.DeleteByRuntimeIdAsync(scheduleId).GetAwaiter().GetResult();
            Log.Information("Schedule {ScheduleId} unregistered and removed", scheduleId);
            return Results.Ok(new { message = "Schedule removed." });
        });

        // Editing keeps the same saved schedule (and its project) while changing when it runs. If the new cron is
        // rejected, the old registration is put back, so a typo never leaves a project without its backup.
        app.MapPut("/api/scheduler/schedules/{scheduleId:guid}", async (
            Guid scheduleId,
            UpdateScheduleRequest request,
            HttpContext context) =>
        {
            var scheduler = context.RequestServices.GetRequiredService<TempoScheduler<BackupJob>>();
            var store = context.RequestServices.GetRequiredService<IBackupScheduleStore>();

            var existing = await store.GetByRuntimeIdAsync(scheduleId);
            if (existing is null)
                return Results.NotFound(new { isError = true, message = "That backup schedule no longer exists." });

            if (string.IsNullOrWhiteSpace(request.CronExpression))
                return Results.BadRequest(new { isError = true, message = "cronExpression is required." });

            var timeZone = string.IsNullOrWhiteSpace(request.TimeZone) ? existing.TimeZone : request.TimeZone.Trim();
            if (!ShipRight.Shared.Time.AppTimeZone.IsKnown(timeZone))
                return Results.BadRequest(new { isError = true, message = "Choose a recognised time zone." });

            var updated = existing with
            {
                Cron = request.CronExpression.Trim(),
                TimeZone = timeZone,
                MissedRunPolicy = request.MissedRunPolicy?.ToString() ?? existing.MissedRunPolicy,
                OverlapPolicy = request.OverlapPolicy?.ToString() ?? existing.OverlapPolicy,
                MaxDuration = request.MaxDuration?.ToString() ?? existing.MaxDuration,
                RuntimeScheduleId = null,
            };

            scheduler.Unregister(scheduleId);
            try
            {
                var newId = SchedulerRegistration.Register(scheduler, updated);
                await store.SaveAsync(updated with { RuntimeScheduleId = newId });
                Log.Information("Backup schedule {ScheduleId} updated to cron \"{Cron}\" ({Zone})",
                    newId, updated.Cron, updated.TimeZone);
                return Results.Ok(new { scheduleId = newId, message = "Schedule updated." });
            }
            catch (Exception ex)
            {
                // Put the previous schedule back exactly as it was.
                try
                {
                    var restored = SchedulerRegistration.Register(scheduler, existing with { RuntimeScheduleId = null });
                    await store.SaveAsync(existing with { RuntimeScheduleId = restored });
                }
                catch (Exception restoreError)
                {
                    Log.Error(restoreError, "Could not restore schedule {ScheduleId} after a rejected edit", scheduleId);
                }

                Log.Warning(ex, "Backup schedule edit rejected for {ScheduleId}", scheduleId);
                return Results.BadRequest(new { isError = true, message = ex.Message });
            }
        });

        app.MapPost("/api/scheduler/replay", async (
            ReplayRequest? request,
            SqliteBackupOverflowStore overflowStore) =>
        {
            await overflowStore.ReplayAsync(request?.ProjectId);
            return Results.Ok(new { message = "Replay initiated." });
        });

        app.MapGet("/api/scheduler/overflow", (
            SqliteBackupOverflowStore overflowStore,
            string? projectId = null) =>
        {
            var records = overflowStore.List(projectId);
            var items = records.Select(r => new
            {
                projectId = r.Job.ProjectId,
                projectName = r.Job.ProjectName,
                failedAt = r.FailedAt,
                errorMessage = r.ErrorMessage,
            }).ToList();

            return Results.Ok(new { items, count = items.Count });
        });
    }

    /// <summary>
    /// Maps the scheduler routes that are shared between desktop and cloud modes
    /// (queue inspection, schedule management, replay, overflow).
    /// </summary>
    public static void MapSchedulerCoreRoutes(this WebApplication app)
    {
        MapProjectScheduleRoutes(app);
        app.MapGet("/api/scheduler/inflight", (HttpContext context) =>
        {
            var queue = context.RequestServices
                .GetRequiredService<TempoQueue<TempoScheduledWork<BackupJob>>>();
            var inflight = queue.Inflight;
            if (inflight is null)
                return Results.Ok(new { inflight = (object?)null });

            return Results.Ok(new
            {
                inflight = new
                {
                    tenantId = inflight.TenantId,
                    correlationId = inflight.CorrelationId,
                    projectId = inflight.Work.Job.ProjectId,
                    projectName = inflight.Work.Job.ProjectName,
                    databaseName = inflight.Work.Job.DatabaseName,
                    scheduledAt = inflight.Work.ScheduledAt,
                    enqueuedAt = inflight.EnqueuedAt,
                    startedAt = inflight.StartedAt,
                    waitMs = (long)inflight.WaitTime.TotalMilliseconds,
                    elapsedMs = (long)inflight.ElapsedProcessingTime.TotalMilliseconds,
                }
            });
        });

        app.MapGet("/api/scheduler/pending", (HttpContext context, int depth = 10) =>
        {
            var queue = context.RequestServices
                .GetRequiredService<TempoQueue<TempoScheduledWork<BackupJob>>>();
            var snapshot = queue.GetQueueSnapshot(depth);
            var items = snapshot.Select(x => new
            {
                tenantId = x.TenantId,
                priority = x.Priority,
                projectId = x.Work.Job.ProjectId,
                projectName = x.Work.Job.ProjectName,
                correlationId = x.CorrelationId,
                scheduledAt = x.Work.ScheduledAt,
                enqueuedAt = x.EnqueuedAt,
                position = x.PositionWithinTenant,
                waitMs = (long)x.WaitTime.TotalMilliseconds,
            }).OrderBy(x => x.enqueuedAt).ToList();

            return Results.Ok(new { pending = items, count = items.Count });
        });

        app.MapGet("/api/scheduler/snapshot", (HttpContext context) =>
        {
            var queue = context.RequestServices
                .GetRequiredService<TempoQueue<TempoScheduledWork<BackupJob>>>();
            var snap = queue.Observability.GetSnapshot();
            return Results.Ok(new
            {
                totalPending = snap.TotalPending,
                throughputPerSecond = Math.Round(snap.ThroughputPerSecond, 1),
                enqueueRatePerSecond = Math.Round(snap.EnqueueRatePerSecond, 1),
                totalProcessed = snap.TotalProcessedSinceStart,
                totalEnqueued = snap.TotalEnqueuedSinceStart,
                totalFailures = snap.TotalFailuresSinceStart,
                failureRatePerSecond = Math.Round(snap.FailureRatePerSecond, 2),
                medianWaitMs = snap.MedianWaitTime.TotalMilliseconds,
                p95WaitMs = snap.P95WaitTime.TotalMilliseconds,
                p99WaitMs = snap.P99WaitTime.TotalMilliseconds,
                medianProcessingMs = snap.MedianProcessingTime.TotalMilliseconds,
                p95ProcessingMs = snap.P95ProcessingTime.TotalMilliseconds,
                tokenBucketUtilization = Math.Round(snap.TokenBucketUtilization, 2),
                healthStatus = snap.HealthStatus.ToString(),
                isDispatcherRunning = snap.IsDispatcherRunning,
                isBacklogGrowing = snap.IsBacklogGrowing,
                lastSuccessAt = snap.LastSuccessAt,
                lastFailureAt = snap.LastFailureAt,
                lastFailureError = snap.LastFailureError,
                circuitBrokenTenants = snap.CircuitBrokenTenants.Select(t => t.ToString()).ToList(),
                pendingByTenant = snap.PendingByTenant.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
                capturedAt = snap.SnapshotTakenAtUtc,
            });
        });

        app.MapGet("/api/scheduler/schedules", async (HttpContext context) =>
        {
            var scheduler = context.RequestServices
                .GetRequiredService<TempoScheduler<BackupJob>>();
            var definitions = await context.RequestServices
                .GetRequiredService<IBackupScheduleStore>().GetAllAsync();
            var byRuntime = definitions
                .Where(d => d.RuntimeScheduleId is not null)
                .ToDictionary(d => d.RuntimeScheduleId!.Value);

            var snapshot = scheduler.GetSnapshot();
            var jobs = snapshot.Jobs.Select(j =>
            {
                byRuntime.TryGetValue(j.ScheduleId, out var definition);
                return new
                {
                    scheduleId = j.ScheduleId,
                    definitionId = definition?.Id,
                    projectId = definition?.ProjectId,
                    projectName = definition?.ProjectName,
                    cron = definition?.Cron,
                    timeZone = definition?.TimeZone,
                    missedRunPolicy = definition?.MissedRunPolicy,
                    overlapPolicy = definition?.OverlapPolicy,
                    maxDuration = definition?.MaxDuration,
                    jobName = j.JobName,
                    scheduleType = j.Schedule.GetType().Name,
                    scheduleDescription = DescribeSchedule(j.Schedule),
                    lastFiredAt = j.LastFiredAt,
                    nextFireAt = j.NextFireAt,
                    inFlightCount = j.InFlightCount,
                    totalFired = j.TotalFired,
                    totalMissed = j.TotalMissed,
                    lastDrift = j.LastDrift,
                };
            }).ToList();

            return Results.Ok(new { jobs, count = jobs.Count, capturedAt = snapshot.CapturedAt });
        });

        app.MapPost("/api/scheduler/schedules", async (
            ScheduleRequest request,
            IProjectStore projectStore,
            HttpContext context) =>
        {
            var scheduler = context.RequestServices
                .GetRequiredService<TempoScheduler<BackupJob>>();
            var project = await projectStore.GetByIdAsync(request.ProjectId);
            if (project is null)
                return Results.NotFound(new { isError = true, message = $"Project '{request.ProjectId}' not found." });

            if (project.Database is null || string.IsNullOrWhiteSpace(project.Database.DatabaseName))
                return Results.BadRequest(new { isError = true, message = "Project has no database configuration." });

            if (string.IsNullOrWhiteSpace(request.CronExpression))
                return Results.BadRequest(new { isError = true, message = "cronExpression is required." });

            try
            {
                var definition = new BackupScheduleDefinition
                {
                    ProjectId = project.Id,
                    ProjectName = project.Name,
                    DatabaseName = project.Database.DatabaseName,
                    Cron = request.CronExpression,
                    TimeZone = request.TimeZone ?? project.TimeZone,
                    MissedRunPolicy = request.MissedRunPolicy.ToString(),
                    OverlapPolicy = request.OverlapPolicy.ToString(),
                    MaxDuration = request.MaxDuration?.ToString(),
                };

                var store = context.RequestServices.GetRequiredService<IBackupScheduleStore>();
                var scheduleId = SchedulerRegistration.Register(scheduler, definition);
                await store.SaveAsync(definition with { RuntimeScheduleId = scheduleId });

                Log.Information("Backup schedule saved for project {Project}: cron \"{Cron}\"",
                    project.Name, request.CronExpression);

                return Results.Ok(new { scheduleId, message = "Schedule registered." });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to register schedule for project {ProjectId}", request.ProjectId);
                return Results.BadRequest(new { isError = true, message = ex.Message });
            }
        });

        app.MapDelete("/api/scheduler/schedules/{scheduleId}", (
            Guid scheduleId,
            HttpContext context) =>
        {
            var scheduler = context.RequestServices
                .GetRequiredService<TempoScheduler<BackupJob>>();
            scheduler.Unregister(scheduleId);
            var store = context.RequestServices.GetRequiredService<IBackupScheduleStore>();
            store.DeleteByRuntimeIdAsync(scheduleId).GetAwaiter().GetResult();
            Log.Information("Schedule {ScheduleId} unregistered and removed", scheduleId);
            return Results.Ok(new { message = "Schedule removed." });
        });

        // Editing keeps the same saved schedule (and its project) while changing when it runs. If the new cron is
        // rejected, the old registration is put back, so a typo never leaves a project without its backup.
        app.MapPut("/api/scheduler/schedules/{scheduleId:guid}", async (
            Guid scheduleId,
            UpdateScheduleRequest request,
            HttpContext context) =>
        {
            var scheduler = context.RequestServices.GetRequiredService<TempoScheduler<BackupJob>>();
            var store = context.RequestServices.GetRequiredService<IBackupScheduleStore>();

            var existing = await store.GetByRuntimeIdAsync(scheduleId);
            if (existing is null)
                return Results.NotFound(new { isError = true, message = "That backup schedule no longer exists." });

            if (string.IsNullOrWhiteSpace(request.CronExpression))
                return Results.BadRequest(new { isError = true, message = "cronExpression is required." });

            var timeZone = string.IsNullOrWhiteSpace(request.TimeZone) ? existing.TimeZone : request.TimeZone.Trim();
            if (!ShipRight.Shared.Time.AppTimeZone.IsKnown(timeZone))
                return Results.BadRequest(new { isError = true, message = "Choose a recognised time zone." });

            var updated = existing with
            {
                Cron = request.CronExpression.Trim(),
                TimeZone = timeZone,
                MissedRunPolicy = request.MissedRunPolicy?.ToString() ?? existing.MissedRunPolicy,
                OverlapPolicy = request.OverlapPolicy?.ToString() ?? existing.OverlapPolicy,
                MaxDuration = request.MaxDuration?.ToString() ?? existing.MaxDuration,
                RuntimeScheduleId = null,
            };

            scheduler.Unregister(scheduleId);
            try
            {
                var newId = SchedulerRegistration.Register(scheduler, updated);
                await store.SaveAsync(updated with { RuntimeScheduleId = newId });
                Log.Information("Backup schedule {ScheduleId} updated to cron \"{Cron}\" ({Zone})",
                    newId, updated.Cron, updated.TimeZone);
                return Results.Ok(new { scheduleId = newId, message = "Schedule updated." });
            }
            catch (Exception ex)
            {
                // Put the previous schedule back exactly as it was.
                try
                {
                    var restored = SchedulerRegistration.Register(scheduler, existing with { RuntimeScheduleId = null });
                    await store.SaveAsync(existing with { RuntimeScheduleId = restored });
                }
                catch (Exception restoreError)
                {
                    Log.Error(restoreError, "Could not restore schedule {ScheduleId} after a rejected edit", scheduleId);
                }

                Log.Warning(ex, "Backup schedule edit rejected for {ScheduleId}", scheduleId);
                return Results.BadRequest(new { isError = true, message = ex.Message });
            }
        });

        app.MapPost("/api/scheduler/replay", async (
            ReplayRequest? request,
            BackupOverflowStore overflowStore) =>
        {
            await overflowStore.ReplayAsync(request?.ProjectId);
            return Results.Ok(new { message = "Replay initiated." });
        });

        app.MapGet("/api/scheduler/overflow", (
            BackupOverflowStore overflowStore,
            string? projectId = null) =>
        {
            var records = overflowStore.List(projectId);
            var items = records.Select(r => new
            {
                projectId = r.Job.ProjectId,
                projectName = r.Job.ProjectName,
                failedAt = r.FailedAt,
                errorMessage = r.ErrorMessage,
            }).ToList();

            return Results.Ok(new { items, count = items.Count });
        });
    }

    /// <summary>The project's saved schedules plus their live next/last fire times, for the project Backups tab.</summary>
    private static void MapProjectScheduleRoutes(WebApplication app)
    {
        app.MapGet("/api/projects/{id}/backup-schedule", async (
            string id, IProjectStore store, IBackupScheduleStore schedules, HttpContext context) =>
        {
            var project = await store.GetByIdAsync(id);
            if (project is null)
                return Results.NotFound(new { isError = true, message = $"Project '{id}' not found." });

            var definitions = (await schedules.GetAllAsync())
                .Where(d => string.Equals(d.ProjectId, id, StringComparison.Ordinal))
                .ToList();
            var snapshot = context.RequestServices
                .GetRequiredService<TempoScheduler<BackupJob>>()
                .GetSnapshot();

            var items = definitions.Select(definition =>
            {
                var runtime = definition.RuntimeScheduleId is Guid runtimeId
                    ? snapshot.Jobs.FirstOrDefault(j => j.ScheduleId == runtimeId)
                    : null;

                return new
                {
                    scheduleId = definition.RuntimeScheduleId,
                    cron = definition.Cron,
                    timeZone = definition.TimeZone,
                    description = $"cron \"{definition.Cron}\"",
                    nextFireAt = runtime?.NextFireAt,
                    lastFiredAt = runtime?.LastFiredAt,
                    totalFired = runtime?.TotalFired ?? 0,
                    totalMissed = runtime?.TotalMissed ?? 0,
                };
            }).ToList();

            return Results.Ok(items);
        });
    }

    private static string DescribeSchedule(TempoSchedule schedule)
    {
        return schedule switch
        {
            TempoSchedule.Cron c => $"cron \"{c.Expression}\"",
            TempoSchedule.Every e => $"every {e.Interval}",
            TempoSchedule.OnceAt o => $"once at {o.FireAt}",
            _ => "unknown",
        };
    }
}

public record ScheduleRequest
{
    public string ProjectId { get; init; } = string.Empty;
    public string CronExpression { get; init; } = string.Empty;
    public MissedRunPolicy MissedRunPolicy { get; init; } = MissedRunPolicy.RunOnce;
    public OverlapPolicy OverlapPolicy { get; init; } = OverlapPolicy.Skip;
    public TimeSpan? MaxDuration { get; init; } = TimeSpan.FromMinutes(30);
    public string? TimeZone { get; init; }
}

public record UpdateScheduleRequest
{
    public string? CronExpression { get; init; }
    public MissedRunPolicy? MissedRunPolicy { get; init; }
    public OverlapPolicy? OverlapPolicy { get; init; }
    public TimeSpan? MaxDuration { get; init; }
    public string? TimeZone { get; init; }
}

public record ReplayRequest
{
    public string? ProjectId { get; init; }
}
