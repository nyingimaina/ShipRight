using Dapper.Contrib.Extensions;
using Jattac.Libraries.QBuilder;
using Rocket.Libraries.DatabaseIntegrator;
using ShipRight.Database;
using ShipRight.Shared.Tenancy;

namespace ShipRight.Modules.WatchBranch;

/// <summary>
/// MariaDB-backed store for watch-branch history, replacing
/// WatchBranchHistoryStore (JSON file-based) in cloud mode.
/// Public API matches the original file-based store. With a CompanyScope every query is limited to the
/// signed-in person's company (R4); rows written by the watcher are stamped with the project's company.
/// </summary>
public class MariaDbWatchBranchHistoryStore
{
    private const int MaxLimit = 500;

    private readonly IDatabaseHelper<Guid> _db;
    private readonly CompanyScope? _scope;

    public MariaDbWatchBranchHistoryStore(IDatabaseHelper<Guid> db)
    {
        _db = db;
    }

    public MariaDbWatchBranchHistoryStore(IDatabaseHelper<Guid> db, CompanyScope scope)
    {
        _db = db;
        _scope = scope;
    }

    public async Task Append(WatchBranchHistoryRecord record)
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
                BranchName = record.BranchName,
                TriggeredBuildId = record.TriggeredBuildId != null ? Guid.Parse(record.TriggeredBuildId) : null,
                Status = record.Status,
                TriggeredAt = record.TriggeredAt,
                ErrorMessage = record.ErrorMessage,
                ScheduleId = record.ScheduleId,
                CorrelationId = record.CorrelationId,
                Created = DateTime.UtcNow,
                Modified = DateTime.UtcNow,
            })
            .BuildWithParameters();
        await _db.ExecuteAsync(built.ParameterizedSql, built.Parameters);
    }

    public async Task<IReadOnlyList<WatchBranchHistoryRecord>> Query(
        string? projectId = null, string? status = null, int limit = 100)
    {
        using var qb = AppQBuilderExtensions.NewQBuilder();
        qb.UseSelector().Select<Record>("*");
        var filter = qb.UseTableBoundFilter<Record>();
        if (_scope?.Filter() is { } companyId)
            qb.UseTableBoundFilter<Record>().WhereEqualTo(r => r.CompanyId, companyId);

        if (!string.IsNullOrWhiteSpace(projectId))
            filter.WhereEqualTo(r => r.ProjectId, Guid.Parse(projectId));
        if (!string.IsNullOrWhiteSpace(status))
            filter.WhereEqualTo(r => r.Status, status);

        var built = qb.BuildWithParameters();
        var records = await _db.GetManyAsync<Record>(built.ParameterizedSql, built.Parameters);

        return records
            .OrderByDescending(r => r.TriggeredAt)
            .Take(Math.Clamp(limit, 1, MaxLimit))
            .Select(Map)
            .ToList();
    }

    private static WatchBranchHistoryRecord Map(Record r) => new()
    {
        Id = r.Id.ToString("N"),
        ProjectId = r.ProjectId.ToString(),
        ProjectName = r.ProjectName,
        BranchName = r.BranchName,
        TriggeredBuildId = r.TriggeredBuildId?.ToString(),
        Status = r.Status,
        TriggeredAt = r.TriggeredAt,
        ErrorMessage = r.ErrorMessage,
        ScheduleId = r.ScheduleId,
        CorrelationId = r.CorrelationId,
    };

    [Table("WatchBranchHistory")]
    private class Record
    {
        [ExplicitKey]
        public Guid Id { get; set; }
        public Guid CompanyId { get; set; }
        public Guid ProjectId { get; set; }
        public string ProjectName { get; set; } = "";
        public string BranchName { get; set; } = "";
        public Guid? TriggeredBuildId { get; set; }
        public string Status { get; set; } = "triggered";
        public DateTime TriggeredAt { get; set; }
        public string? ErrorMessage { get; set; }
        public Guid ScheduleId { get; set; }
        public Guid CorrelationId { get; set; }
        public DateTime Created { get; set; }
        public DateTime Modified { get; set; }
        public bool Deleted { get; set; }
    }
}
