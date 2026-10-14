using System.Text.Json;
using Dapper.Contrib.Extensions;
using Jattac.Libraries.QBuilder;
using Rocket.Libraries.DatabaseIntegrator;
using ShipRight.Database;
using ShipRight.Shared.Tenancy;

namespace ShipRight.Modules.Projects;

public class MariaDbProjectStore : IProjectStore
{
    private readonly IDatabaseHelper<Guid> _db;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public MariaDbProjectStore(IDatabaseHelper<Guid> db)
    {
        _db = db;
    }

    public int Count => GetAllAsync().GetAwaiter().GetResult().Count;

    public async Task<List<ProjectConfig>> GetAllAsync()
    {
        using var qb = AppQBuilderExtensions.NewQBuilder();
        var built = qb
            .UseSelector()
            .Select<ProjectRecord>("*")
            .Then()
            .UseTableBoundFilter<ProjectRecord>()
            .WhereEqualTo(r => r.Deleted, 0)
            .Then()
            .BuildWithParameters();

        var records = await _db.GetManyAsync<ProjectRecord>(built.ParameterizedSql, built.Parameters);
        return records.Select(Deserialize).ToList();
    }

    public async Task<ProjectConfig?> GetByIdAsync(string id)
    {
        using var qb = AppQBuilderExtensions.NewQBuilder();
        var built = qb
            .UseSelector()
            .Select<ProjectRecord>("*")
            .Then()
            .UseTableBoundFilter<ProjectRecord>()
            .WhereEqualTo(r => r.Id, Guid.Parse(id))
            .Then()
            .BuildWithParameters();

        var records = await _db.GetManyAsync<ProjectRecord>(built.ParameterizedSql, built.Parameters);
        var record = records.FirstOrDefault();
        return record != null ? Deserialize(record) : null;
    }

    public async Task<ProjectConfig?> GetByNameAsync(string name)
    {
        using var qb = AppQBuilderExtensions.NewQBuilder();
        var built = qb
            .UseSelector()
            .Select<ProjectRecord>("*")
            .Then()
            .UseTableBoundFilter<ProjectRecord>()
            .WhereEqualTo(r => r.Name, name)
            .Then()
            .BuildWithParameters();

        var records = await _db.GetManyAsync<ProjectRecord>(built.ParameterizedSql, built.Parameters);
        var record = records.FirstOrDefault();
        return record != null ? Deserialize(record) : null;
    }

    public async Task SaveAsync(ProjectConfig project)
    {
        using var qb = AppQBuilderExtensions.NewQBuilder();
        var existing = await GetByIdAsync(project.Id);
        if (existing != null)
        {
            var built = qb
                .UseTableBoundUpdate<ProjectRecord>()
                .Set(r => r.Data, JsonSerializer.Serialize(project, JsonOpts))
                .Set(r => r.Name, project.Name)
                .Set(r => r.Modified, DateTime.UtcNow)
                .WhereEqualTo(r => r.Id, Guid.Parse(project.Id))
                .BuildWithParameters();
            await _db.ExecuteAsync(built.ParameterizedSql, built.Parameters);
        }
        else
        {
            var record = new ProjectRecord
            {
                Id = Guid.Parse(project.Id),
                CompanyId = Guid.Empty,
                Name = project.Name,
                Data = JsonSerializer.Serialize(project, JsonOpts),
                Created = DateTime.UtcNow,
                Modified = DateTime.UtcNow,
            };
            var built = qb
                .UseTableBoundInsert<ProjectRecord>()
                .FromObject(record)
                .BuildWithParameters();
            await _db.ExecuteAsync(built.ParameterizedSql, built.Parameters);
        }
    }

    public async Task DeleteAsync(string id)
    {
        using var qb = AppQBuilderExtensions.NewQBuilder();
        var built = qb
            .UseTableBoundUpdate<ProjectRecord>()
            .Set(r => r.Deleted, true)
            .WhereEqualTo(r => r.Id, Guid.Parse(id))
            .BuildWithParameters();
        await _db.ExecuteAsync(built.ParameterizedSql, built.Parameters);
    }

    private static ProjectConfig Deserialize(ProjectRecord record)
    {
        var config = JsonSerializer.Deserialize<ProjectConfig>(record.Data, JsonOpts);
        if (config == null)
            throw new InvalidOperationException($"Failed to deserialize project {record.Id}");
        return config with
        {
            Id = record.Id.ToString(),
            Name = record.Name,
        };
    }

    [Table("Project")]
    private class ProjectRecord
    {
        [ExplicitKey]
        public Guid Id { get; set; }
        public Guid CompanyId { get; set; }
        public string Name { get; set; } = "";
        public string Data { get; set; } = "";
        public DateTime Created { get; set; }
        public DateTime Modified { get; set; }
        public bool Deleted { get; set; }
    }
}

// Cloud project store limited to the signed-in person's company: every query carries the company filter (R4),
// and a project belonging to someone else is simply not found. System work (start-up scheduling) sees all.
public class CompanyScopedProjectStore(IDatabaseHelper<Guid> db, CompanyScope scope) : IProjectStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
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

    public async Task<List<ProjectConfig>> GetAllAsync()
    {
        using var qb = AppQBuilderExtensions.NewQBuilder();
        qb.UseSelector().Select<Row>("*");
        qb.UseTableBoundFilter<Row>().WhereEqualTo(r => r.Deleted, 0);
        ApplyCompany(qb);
        var built = qb.BuildWithParameters();
        return (await db.GetManyAsync<Row>(built.ParameterizedSql, built.Parameters)).Select(Deserialize).ToList();
    }

    public async Task<ProjectConfig?> GetByIdAsync(string id)
    {
        if (!Guid.TryParse(id, out var guid)) return null;
        using var qb = AppQBuilderExtensions.NewQBuilder();
        qb.UseSelector().Select<Row>("*");
        qb.UseTableBoundFilter<Row>().WhereEqualTo(r => r.Id, guid);
        ApplyCompany(qb);
        var built = qb.BuildWithParameters();
        var row = (await db.GetManyAsync<Row>(built.ParameterizedSql, built.Parameters)).FirstOrDefault();
        return row != null ? Deserialize(row) : null;
    }

    public async Task<ProjectConfig?> GetByNameAsync(string name)
    {
        using var qb = AppQBuilderExtensions.NewQBuilder();
        qb.UseSelector().Select<Row>("*");
        qb.UseTableBoundFilter<Row>().WhereEqualTo(r => r.Name, name);
        ApplyCompany(qb);
        var built = qb.BuildWithParameters();
        var row = (await db.GetManyAsync<Row>(built.ParameterizedSql, built.Parameters)).FirstOrDefault();
        return row != null ? Deserialize(row) : null;
    }

    public async Task SaveAsync(ProjectConfig project)
    {
        var id = Guid.Parse(project.Id);
        if (await GetByIdAsync(project.Id) != null)
        {
            using var qb = AppQBuilderExtensions.NewQBuilder();
            var update = qb
                .UseTableBoundUpdate<Row>()
                .Set(r => r.Data, JsonSerializer.Serialize(project, JsonOpts))
                .Set(r => r.Name, project.Name)
                .Set(r => r.Modified, DateTime.UtcNow)
                .WhereEqualTo(r => r.Id, id);
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
                Id = id,
                CompanyId = await scope.InsertCompanyAsync(),
                Name = project.Name,
                Data = JsonSerializer.Serialize(project, JsonOpts),
                Created = DateTime.UtcNow,
                Modified = DateTime.UtcNow,
            })
            .BuildWithParameters();
        await db.ExecuteAsync(insertBuilt.ParameterizedSql, insertBuilt.Parameters);
    }

    public async Task DeleteAsync(string id)
    {
        if (!Guid.TryParse(id, out var guid)) return;
        using var qb = AppQBuilderExtensions.NewQBuilder();
        var update = qb.UseTableBoundUpdate<Row>().Set(r => r.Deleted, true).WhereEqualTo(r => r.Id, guid);
        if (scope.Filter() is { } companyId) update = update.WhereEqualTo(r => r.CompanyId, companyId);
        var built = update.BuildWithParameters();
        await db.ExecuteAsync(built.ParameterizedSql, built.Parameters);
    }

    private void ApplyCompany(QBuilder qb)
    {
        if (scope.Filter() is { } companyId)
            qb.UseTableBoundFilter<Row>().WhereEqualTo(r => r.CompanyId, companyId);
    }

    private static ProjectConfig Deserialize(Row row)
    {
        var config = JsonSerializer.Deserialize<ProjectConfig>(row.Data, JsonOpts)
            ?? throw new InvalidOperationException($"Failed to deserialize project {row.Id}");
        return config with { Id = row.Id.ToString(), Name = row.Name };
    }

    private class CountRow
    {
        public int Count { get; set; }
    }

    [Table("Project")]
    private class Row
    {
        [ExplicitKey]
        public Guid Id { get; set; }
        public Guid CompanyId { get; set; }
        public string Name { get; set; } = "";
        public string Data { get; set; } = "";
        public DateTime Created { get; set; }
        public DateTime Modified { get; set; }
        public bool Deleted { get; set; }
    }
}
