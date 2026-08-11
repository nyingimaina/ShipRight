using System.Text.Json;
using Dapper.Contrib.Extensions;
using Jattac.Libraries.QBuilder;
using Rocket.Libraries.DatabaseIntegrator;
using ShipRight.Database;

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
