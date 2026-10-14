using Dapper.Contrib.Extensions;
using Jattac.Libraries.QBuilder;
using Rocket.Libraries.DatabaseIntegrator;
using ShipRight.Database;
using ShipRight.Shared.Tenancy;

namespace ShipRight.Modules.Scheduler;

/// <summary>
/// MariaDB-backed store for backup history, replacing BackupHistoryStore
/// (JSON file-based) when running in cloud mode.
/// Public API matches the original file-based store. With a CompanyScope every query is limited to the
/// signed-in person's company (R4); rows written by the scheduler are stamped with the project's company.
/// </summary>
public class MariaDbBackupHistoryStore
{
    private readonly IDatabaseHelper<Guid> _db;
    private readonly CompanyScope? _scope;

    public MariaDbBackupHistoryStore(IDatabaseHelper<Guid> db)
    {
        _db = db;
    }

    public MariaDbBackupHistoryStore(IDatabaseHelper<Guid> db, CompanyScope scope)
    {
        _db = db;
        _scope = scope;
    }

    public async Task Append(BackupHistoryRecord record)
    {
        var projectId = Guid.Parse(record.ProjectId);
        using var qb = AppQBuilderExtensions.NewQBuilder();
        var built = qb
            .UseTableBoundInsert<Record>()
            .FromObject(new Record
            {
                Id = Guid.NewGuid(),
                CompanyId = _scope is null ? Guid.Empty : await _scope.InsertCompanyAsync(projectId),
                ProjectId = projectId,
                ProjectName = record.ProjectName,
                DatabaseName = record.DatabaseName,
                Status = record.Status,
                StartedAt = record.StartedAt,
                CompletedAt = record.CompletedAt,
                DurationMs = record.DurationMs,
                ErrorMessage = record.ErrorMessage,
                BackupFileName = record.BackupFileName,
                BackupSizeBytes = record.BackupSizeBytes,
                ScheduleId = record.ScheduleId,
                CorrelationId = record.CorrelationId,
                Created = DateTime.UtcNow,
                Modified = DateTime.UtcNow,
            })
            .BuildWithParameters();
        await _db.ExecuteAsync(built.ParameterizedSql, built.Parameters);
    }

    public async Task<IReadOnlyList<BackupHistoryRecord>> Query(
        DateTime? since = null, DateTime? until = null,
        string? projectId = null, string? status = null,
        int? limit = null)
    {
        using var qb = AppQBuilderExtensions.NewQBuilder();
        qb.UseSelector().Select<Record>("*");
        var filter = qb.UseTableBoundFilter<Record>();
        ApplyCompany(qb);

        if (since.HasValue)
            filter.WhereGreaterThanOrEqualTo(r => r.StartedAt, since.Value);
        if (until.HasValue)
            filter.WhereLessThanOrEqualTo(r => r.StartedAt, until.Value);
        if (!string.IsNullOrWhiteSpace(projectId))
            filter.WhereEqualTo(r => r.ProjectId, Guid.Parse(projectId));
        if (!string.IsNullOrWhiteSpace(status))
            filter.WhereEqualTo(r => r.Status, status);

        var built = qb.BuildWithParameters();
        var records = await _db.GetManyAsync<Record>(built.ParameterizedSql, built.Parameters);

        return records
            .OrderByDescending(r => r.StartedAt)
            .Take(limit ?? 500)
            .Select(Map)
            .ToList();
    }

    public async Task<BackupReportSummary> GetSummary(DateTime? since = null, DateTime? until = null)
    {
        since ??= DateTime.UtcNow.AddDays(-30);
        until ??= DateTime.UtcNow;

        using var qb = AppQBuilderExtensions.NewQBuilder();
        qb.UseSelector().Select<Record>("Status, DurationMs, BackupSizeBytes");
        qb.UseTableBoundFilter<Record>()
            .WhereGreaterThanOrEqualTo(r => r.StartedAt, since.Value)
            .WhereLessThanOrEqualTo(r => r.StartedAt, until.Value);
        ApplyCompany(qb);
        var built = qb.BuildWithParameters();

        var records = await _db.GetManyAsync<Record>(built.ParameterizedSql, built.Parameters);
        var total = records.Count;
        var successful = records.Count(r => r.Status == "completed");
        var failed = records.Count(r => r.Status == "failed");

        return new BackupReportSummary
        {
            TotalRuns = total,
            SuccessfulRuns = successful,
            FailedRuns = failed,
            SuccessRate = total > 0 ? Math.Round((double)successful / total * 100, 1) : 100,
            AvgDurationMs = total > 0 ? Math.Round(records.Average(r => r.DurationMs), 0) : 0,
            TotalSizeBytes = records.Sum(r => r.BackupSizeBytes),
            From = since.Value,
            To = until.Value,
        };
    }

    public async Task<List<BackupDailyReport>> GetDailyReport(int days = 30)
    {
        var since = DateTime.UtcNow.Date.AddDays(-days);
        var until = DateTime.UtcNow.Date.AddDays(1);

        using var qb = AppQBuilderExtensions.NewQBuilder();
        qb.UseSelector().Select<Record>("StartedAt, Status, DurationMs, BackupSizeBytes");
        qb.UseTableBoundFilter<Record>()
            .WhereGreaterThanOrEqualTo(r => r.StartedAt, since)
            .WhereLessThan(r => r.StartedAt, until);
        ApplyCompany(qb);
        var built = qb.BuildWithParameters();

        var records = await _db.GetManyAsync<Record>(built.ParameterizedSql, built.Parameters);
        var byDay = records.GroupBy(r => r.StartedAt.Date).ToDictionary(g => g.Key, g => g.ToList());

        var results = new List<BackupDailyReport>();
        for (var date = since; date < until; date = date.AddDays(1))
        {
            if (!byDay.TryGetValue(date, out var day))
            {
                results.Add(new BackupDailyReport { Date = date });
                continue;
            }

            results.Add(new BackupDailyReport
            {
                Date = date,
                TotalRuns = day.Count,
                SuccessfulRuns = day.Count(r => r.Status == "completed"),
                FailedRuns = day.Count(r => r.Status == "failed"),
                AvgDurationMs = Math.Round(day.Average(r => r.DurationMs), 0),
                TotalSizeBytes = day.Sum(r => r.BackupSizeBytes),
            });
        }

        return results;
    }

    public async Task<List<BackupProjectReport>> GetProjectReports()
    {
        using var qb = AppQBuilderExtensions.NewQBuilder();
        qb.UseSelector().Select<Record>("*");
        qb.UseTableBoundFilter<Record>();
        ApplyCompany(qb);
        var built = qb.BuildWithParameters();

        var records = await _db.GetManyAsync<Record>(built.ParameterizedSql, built.Parameters);
        var byProject = records.GroupBy(r => r.ProjectId);

        return byProject.Select(g =>
        {
            var list = g.ToList();
            var successful = list.Count(r => r.Status == "completed");
            var total = list.Count;
            return new BackupProjectReport
            {
                ProjectId = g.Key.ToString(),
                ProjectName = list.First().ProjectName,
                TotalRuns = total,
                SuccessfulRuns = successful,
                FailedRuns = total - successful,
                SuccessRate = total > 0 ? Math.Round((double)successful / total * 100, 1) : 100,
                LastBackupAt = list.Max(r => r.StartedAt),
            };
        }).OrderByDescending(r => r.LastBackupAt).ToList();
    }

    public async Task Prune(int retainCount = 1000)
    {
        using var qb = AppQBuilderExtensions.NewQBuilder();
        qb.UseSelector().Select<Record>("Id, StartedAt");
        qb.UseTableBoundFilter<Record>();
        ApplyCompany(qb);
        var built = qb.BuildWithParameters();

        var records = await _db.GetManyAsync<Record>(built.ParameterizedSql, built.Parameters);
        if (records.Count <= retainCount) return;

        var toDelete = records
            .OrderByDescending(r => r.StartedAt)
            .Skip(retainCount)
            .ToList();

        foreach (var record in toDelete)
        {
            using var qb2 = AppQBuilderExtensions.NewQBuilder();
            var delBuilt = qb2
                .UseTableBoundDelete<Record>()
                .WhereEqualTo(r => r.Id, record.Id)
                .BuildWithParameters();
            await _db.ExecuteAsync(delBuilt.ParameterizedSql, delBuilt.Parameters);
        }
    }

    // No scope means the store was built without tenancy (unit tests only); the cloud registers it with one.
    private void ApplyCompany(QBuilder qb)
    {
        if (_scope?.Filter() is { } companyId)
            qb.UseTableBoundFilter<Record>().WhereEqualTo(r => r.CompanyId, companyId);
    }

    private static BackupHistoryRecord Map(Record r) => new()
    {
        Id = r.Id.ToString("N"),
        ProjectId = r.ProjectId.ToString(),
        ProjectName = r.ProjectName,
        DatabaseName = r.DatabaseName,
        Status = r.Status,
        StartedAt = r.StartedAt,
        CompletedAt = r.CompletedAt,
        DurationMs = r.DurationMs,
        ErrorMessage = r.ErrorMessage,
        BackupFileName = r.BackupFileName,
        BackupSizeBytes = r.BackupSizeBytes,
        ScheduleId = r.ScheduleId,
        CorrelationId = r.CorrelationId,
    };

    [Table("BackupHistory")]
    private class Record
    {
        [ExplicitKey]
        public Guid Id { get; set; }
        public Guid CompanyId { get; set; }
        public Guid ProjectId { get; set; }
        public string ProjectName { get; set; } = "";
        public string DatabaseName { get; set; } = "";
        public string Status { get; set; } = "completed";
        public DateTime StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public long DurationMs { get; set; }
        public string? ErrorMessage { get; set; }
        public string? BackupFileName { get; set; }
        public long BackupSizeBytes { get; set; }
        public Guid ScheduleId { get; set; }
        public Guid CorrelationId { get; set; }
        public DateTime Created { get; set; }
        public DateTime Modified { get; set; }
        public bool Deleted { get; set; }
    }
}
