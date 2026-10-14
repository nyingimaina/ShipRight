using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper.Contrib.Extensions;
using Jattac.Libraries.QBuilder;
using Rocket.Libraries.DatabaseIntegrator;
using Serilog;
using ShipRight.Database;
using ShipRight.Shared.Tenancy;

namespace ShipRight.Modules.Builds;

public class MariaDbBuildStore : IBuildStore
{
    private readonly IDatabaseHelper<Guid> _db;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public MariaDbBuildStore(IDatabaseHelper<Guid> db)
    {
        _db = db;
    }

    public int Count
    {
        get
        {
            using var qb = AppQBuilderExtensions.NewQBuilder();
            var built = qb
                .SelectCountAs<Record>("Count")
                .UseTableBoundFilter<Record>()
                .WhereEqualTo(r => r.Deleted, 0)
                .Then()
                .BuildWithParameters();

            var result = _db.GetManyAsync<CountResult>(built.ParameterizedSql, built.Parameters).GetAwaiter().GetResult();
            return result.FirstOrDefault()?.Count ?? 0;
        }
    }

    public async Task SaveAsync(BuildRecord record)
    {
        var data = JsonSerializer.Serialize(record, JsonOpts);
        using var qb = AppQBuilderExtensions.NewQBuilder();
        var existing = await GetByIdAsync(record.Id);

        if (existing != null)
        {
            var built = qb
                .UseTableBoundUpdate<Record>()
                .Set(r => r.Data, data)
                .Set(r => r.ProjectId, Guid.Parse(record.ProjectId))
                .Set(r => r.ProjectName, record.ProjectName)
                .Set(r => r.Status, record.Status.ToString())
                .Set(r => r.GitTag, record.GitTag)
                .Set(r => r.StartedAt, record.StartedAt)
                .Set(r => r.CompletedAt, record.CompletedAt)
                .Set(r => r.Modified, DateTime.UtcNow)
                .WhereEqualTo(r => r.Id, Guid.Parse(record.Id))
                .BuildWithParameters();
            await _db.ExecuteAsync(built.ParameterizedSql, built.Parameters);
        }
        else
        {
            var built = qb
                .UseTableBoundInsert<Record>()
                .FromObject(new Record
                {
                    Id = Guid.Parse(record.Id),
                    CompanyId = Guid.Empty,
                    ProjectId = Guid.Parse(record.ProjectId),
                    ProjectName = record.ProjectName,
                    Status = record.Status.ToString(),
                    GitTag = record.GitTag,
                    Data = data,
                    StartedAt = record.StartedAt,
                    CompletedAt = record.CompletedAt,
                    Deleted = false,
                })
                .BuildWithParameters();
            await _db.ExecuteAsync(built.ParameterizedSql, built.Parameters);
        }
    }

    public async Task<BuildRecord?> GetByIdAsync(string id)
    {
        using var qb = AppQBuilderExtensions.NewQBuilder();
        var built = qb
            .UseSelector()
            .Select<Record>("Data")
            .Then()
            .UseTableBoundFilter<Record>()
            .WhereEqualTo(r => r.Id, Guid.Parse(id))
            .Then()
            .BuildWithParameters();

        var rows = await _db.GetManyAsync<JsonWrapper>(built.ParameterizedSql, built.Parameters);
        var row = rows.FirstOrDefault();
        return row != null ? Deserialize(row.Data) : null;
    }

    public async Task<List<BuildRecord>> QueryAsync(
        string? projectId, string? status, DateTime? from, DateTime? to,
        string? gitTag, int page, int pageSize)
    {
        using var qb = AppQBuilderExtensions.NewQBuilder();
        qb.UseSelector().Select<Record>("Data");

        if (!string.IsNullOrWhiteSpace(projectId))
            qb.UseTableBoundFilter<Record>()
              .WhereEqualTo(r => r.ProjectId, Guid.Parse(projectId));

        if (!string.IsNullOrWhiteSpace(status))
        {
            var statuses = status.Split(',', StringSplitOptions.RemoveEmptyEntries);
            qb.UseTableBoundFilter<Record>()
              .WhereIn(r => r.Status, statuses.Cast<object>().ToArray());
        }

        if (from.HasValue)
            qb.UseTableBoundFilter<Record>()
              .WhereGreaterThanOrEqualTo(r => r.StartedAt, from.Value);

        if (to.HasValue)
            qb.UseTableBoundFilter<Record>()
              .WhereLessThanOrEqualTo(r => r.StartedAt, to.Value);

        qb.UseMySqlServerPagingBuilder<Record>()
          .PageBy(r => r.StartedAt, (uint)page, (ushort)pageSize, false);

        var built = qb.BuildWithParameters();
        var rows = await _db.GetManyAsync<JsonWrapper>(built.ParameterizedSql, built.Parameters);
        return rows.Select(r => Deserialize(r.Data)).Where(b => b != null).ToList()!;
    }

    public async Task<int> CountQueryAsync(
        string? projectId, string? status, DateTime? from, DateTime? to, string? gitTag)
    {
        var all = await QueryAsync(projectId, status, from, to, gitTag, 1, int.MaxValue);
        return all.Count;
    }

    public async Task MarkInterruptedAsync()
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-5);

        using var qb = AppQBuilderExtensions.NewQBuilder();
        var built = qb
            .UseSelector()
            .Select<Record>("Id, Data")
            .Then()
            .UseTableBoundFilter<Record>()
            .WhereIn(r => r.Status, new object[] { "Running", "Deploying" })
            .WhereLessThanOrEqualTo(r => r.StartedAt, cutoff)
            .Then()
            .BuildWithParameters();

        var rows = await _db.GetManyAsync<Record>(built.ParameterizedSql, built.Parameters);
        foreach (var row in rows)
        {
            var record = Deserialize(row.Data);
            if (record == null) continue;

            record.Status = BuildStatus.Interrupted;
            record.AppendLogLine("[ShipRight] Build marked as interrupted: process was terminated during execution.");
            await SaveAsync(record);
            Log.Warning("Build {BuildId} marked as interrupted", record.Id);
        }
    }

    private static BuildRecord? Deserialize(string data)
    {
        try { return JsonSerializer.Deserialize<BuildRecord>(data, JsonOpts); }
        catch { return null; }
    }

    [Table("Build")]
    private class Record
    {
        [ExplicitKey]
        public Guid Id { get; set; }
        public Guid CompanyId { get; set; }
        public string Data { get; set; } = "";
        public Guid ProjectId { get; set; }
        public string ProjectName { get; set; } = "";
        public string Status { get; set; } = "";
        public string GitTag { get; set; } = "";
        public DateTime? StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public DateTime Modified { get; set; }
        public bool Deleted { get; set; }
    }

    private class CountResult
    {
        public int Count { get; set; }
    }

    private class JsonWrapper
    {
        public string Data { get; set; } = "";
    }
}

// Cloud build store limited to the signed-in person's company: every query carries the company filter (R4).
// Builds started by background work (watch-branch) are stamped with the company that owns the project.
public class CompanyScopedBuildStore(IDatabaseHelper<Guid> db, CompanyScope scope) : IBuildStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public int Count
    {
        get
        {
            if (!scope.IsScoped) return 0;
            using var qb = AppQBuilderExtensions.NewQBuilder();
            qb.SelectCountAs<Row>("Count");
            qb.UseTableBoundFilter<Row>().WhereEqualTo(r => r.Deleted, 0);
            ApplyCompany(qb);
            var built = qb.BuildWithParameters();
            return db.GetManyAsync<CountRow>(built.ParameterizedSql, built.Parameters).GetAwaiter().GetResult()
                .FirstOrDefault()?.Count ?? 0;
        }
    }

    public async Task SaveAsync(BuildRecord record)
    {
        var data = JsonSerializer.Serialize(record, JsonOpts);
        var projectId = Guid.Parse(record.ProjectId);

        if (await GetByIdAsync(record.Id) != null)
        {
            using var qb = AppQBuilderExtensions.NewQBuilder();
            var update = qb
                .UseTableBoundUpdate<Row>()
                .Set(r => r.Data, data)
                .Set(r => r.ProjectId, projectId)
                .Set(r => r.ProjectName, record.ProjectName)
                .Set(r => r.Status, record.Status.ToString())
                .Set(r => r.GitTag, record.GitTag)
                .Set(r => r.StartedAt, record.StartedAt)
                .Set(r => r.CompletedAt, record.CompletedAt)
                .Set(r => r.Modified, DateTime.UtcNow)
                .WhereEqualTo(r => r.Id, Guid.Parse(record.Id));
            if (scope.Filter() is { } companyId) update = update.WhereEqualTo(r => r.CompanyId, companyId);
            var built = update.BuildWithParameters();
            await db.ExecuteAsync(built.ParameterizedSql, built.Parameters);
            return;
        }

        using var insert = AppQBuilderExtensions.NewQBuilder();
        var insertBuilt = insert
            .UseTableBoundInsert<Row>()
            .FromObject(new Row
            {
                Id = Guid.Parse(record.Id),
                CompanyId = await scope.InsertCompanyAsync(projectId),
                ProjectId = projectId,
                ProjectName = record.ProjectName,
                Status = record.Status.ToString(),
                GitTag = record.GitTag,
                Data = data,
                StartedAt = record.StartedAt,
                CompletedAt = record.CompletedAt,
                Deleted = false,
            })
            .BuildWithParameters();
        await db.ExecuteAsync(insertBuilt.ParameterizedSql, insertBuilt.Parameters);
    }

    public async Task<BuildRecord?> GetByIdAsync(string id)
    {
        if (!Guid.TryParse(id, out var guid)) return null;
        using var qb = AppQBuilderExtensions.NewQBuilder();
        qb.UseSelector().Select<Row>("Data");
        qb.UseTableBoundFilter<Row>().WhereEqualTo(r => r.Id, guid);
        ApplyCompany(qb);
        var built = qb.BuildWithParameters();
        var row = (await db.GetManyAsync<JsonRow>(built.ParameterizedSql, built.Parameters)).FirstOrDefault();
        return row != null ? Deserialize(row.Data) : null;
    }

    public async Task<List<BuildRecord>> QueryAsync(
        string? projectId, string? status, DateTime? from, DateTime? to,
        string? gitTag, int page, int pageSize)
    {
        using var qb = AppQBuilderExtensions.NewQBuilder();
        qb.UseSelector().Select<Row>("Data");
        ApplyQueryFilters(qb, projectId, status, from, to);
        qb.UseMySqlServerPagingBuilder<Row>().PageBy(r => r.StartedAt, (uint)page, (ushort)pageSize, false);
        var built = qb.BuildWithParameters();
        var rows = await db.GetManyAsync<JsonRow>(built.ParameterizedSql, built.Parameters);
        return rows.Select(r => Deserialize(r.Data)).Where(b => b != null).ToList()!;
    }

    // Counted in SQL: the total no longer means loading every build just to count them.
    public async Task<int> CountQueryAsync(string? projectId, string? status, DateTime? from, DateTime? to, string? gitTag)
    {
        using var qb = AppQBuilderExtensions.NewQBuilder();
        qb.SelectCountAs<Row>("Count");
        ApplyQueryFilters(qb, projectId, status, from, to);
        var built = qb.BuildWithParameters();
        return (await db.GetManyAsync<CountRow>(built.ParameterizedSql, built.Parameters)).FirstOrDefault()?.Count ?? 0;
    }

    // Start-up housekeeping: runs as system work across every company.
    public async Task MarkInterruptedAsync()
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-5);

        using var qb = AppQBuilderExtensions.NewQBuilder();
        qb.UseSelector().Select<Row>("Id, Data");
        qb.UseTableBoundFilter<Row>()
            .WhereIn(r => r.Status, new object[] { "Running", "Deploying" })
            .WhereLessThanOrEqualTo(r => r.StartedAt, cutoff);
        ApplyCompany(qb);
        var built = qb.BuildWithParameters();

        var rows = await db.GetManyAsync<Row>(built.ParameterizedSql, built.Parameters);
        foreach (var row in rows)
        {
            var record = Deserialize(row.Data);
            if (record == null) continue;

            record.Status = BuildStatus.Interrupted;
            record.AppendLogLine("[ShipRight] Build marked as interrupted: process was terminated during execution.");
            await SaveAsync(record);
            Log.Warning("Build {BuildId} marked as interrupted", record.Id);
        }
    }

    private void ApplyQueryFilters(QBuilder qb, string? projectId, string? status, DateTime? from, DateTime? to)
    {
        qb.UseTableBoundFilter<Row>().WhereEqualTo(r => r.Deleted, 0);
        ApplyCompany(qb);

        if (!string.IsNullOrWhiteSpace(projectId))
            qb.UseTableBoundFilter<Row>().WhereEqualTo(r => r.ProjectId, Guid.Parse(projectId));

        if (!string.IsNullOrWhiteSpace(status))
        {
            var statuses = status.Split(',', StringSplitOptions.RemoveEmptyEntries);
            qb.UseTableBoundFilter<Row>().WhereIn(r => r.Status, statuses.Cast<object>().ToArray());
        }

        if (from.HasValue)
            qb.UseTableBoundFilter<Row>().WhereGreaterThanOrEqualTo(r => r.StartedAt, from.Value);

        if (to.HasValue)
            qb.UseTableBoundFilter<Row>().WhereLessThanOrEqualTo(r => r.StartedAt, to.Value);
    }

    private void ApplyCompany(QBuilder qb)
    {
        if (scope.Filter() is { } companyId)
            qb.UseTableBoundFilter<Row>().WhereEqualTo(r => r.CompanyId, companyId);
    }

    private static BuildRecord? Deserialize(string data)
    {
        try { return JsonSerializer.Deserialize<BuildRecord>(data, JsonOpts); }
        catch { return null; }
    }

    [Table("Build")]
    private class Row
    {
        [ExplicitKey]
        public Guid Id { get; set; }
        public Guid CompanyId { get; set; }
        public string Data { get; set; } = "";
        public Guid ProjectId { get; set; }
        public string ProjectName { get; set; } = "";
        public string Status { get; set; } = "";
        public string GitTag { get; set; } = "";
        public DateTime? StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public DateTime Modified { get; set; }
        public bool Deleted { get; set; }
    }

    private class CountRow
    {
        public int Count { get; set; }
    }

    private class JsonRow
    {
        public string Data { get; set; } = "";
    }
}
