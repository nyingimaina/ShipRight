using Dapper.Contrib.Extensions;
using Jattac.Libraries.QBuilder;
using Rocket.Libraries.DatabaseIntegrator;

namespace ShipRight.Modules.WatchBranch;

/// <summary>
/// MariaDB-backed store for watch-branch history, replacing
/// WatchBranchHistoryStore (JSON file-based) in cloud mode.
/// Public API matches the original file-based store.
/// </summary>
public class MariaDbWatchBranchHistoryStore
{
    private readonly IDatabaseHelper<Guid> _db;

    public MariaDbWatchBranchHistoryStore(IDatabaseHelper<Guid> db)
    {
        _db = db;
    }

    public async Task Append(WatchBranchHistoryRecord record)
    {
        using var qb = new QBuilder(parameterize: true);
        var built = qb
            .UseTableBoundInsert<Record>()
            .FromObject(new Record
            {
                Id = Guid.NewGuid(),
                CompanyId = Guid.Empty,
                ProjectId = Guid.Parse(record.ProjectId),
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
        using var qb = new QBuilder(parameterize: true);
        qb.UseSelector().Select<Record>("*");
        var filter = qb.UseTableBoundFilter<Record>();

        if (!string.IsNullOrWhiteSpace(projectId))
            filter.WhereEqualTo(r => r.ProjectId, Guid.Parse(projectId));
        if (!string.IsNullOrWhiteSpace(status))
            filter.WhereEqualTo(r => r.Status, status);

        var built = qb.BuildWithParameters();
        var records = await _db.GetManyAsync<Record>(built.ParameterizedSql, built.Parameters);

        return records
            .OrderByDescending(r => r.TriggeredAt)
            .Take(limit)
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
