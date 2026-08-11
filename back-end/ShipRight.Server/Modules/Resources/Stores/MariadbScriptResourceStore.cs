using System.Text.Json;
using Dapper.Contrib.Extensions;
using Jattac.Libraries.QBuilder;
using Rocket.Libraries.DatabaseIntegrator;
using ShipRight.Database;
using ShipRight.Modules.Resources.Models;

namespace ShipRight.Modules.Resources.Stores;

public class MariaDbScriptResourceStore : IScriptResourceStore
{
    private readonly IDatabaseHelper<Guid> _db;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public MariaDbScriptResourceStore(IDatabaseHelper<Guid> db)
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
                .UseTableBoundFilter<Record>().WhereEqualTo(r => r.Deleted, 0)
                .Then()
                .BuildWithParameters();
            var result = _db.GetManyAsync<CountResult>(built.ParameterizedSql, built.Parameters).GetAwaiter().GetResult();
            return result.FirstOrDefault()?.Count ?? 0;
        }
    }

    public async Task<List<ScriptResource>> GetAllAsync()
    {
        using var qb = AppQBuilderExtensions.NewQBuilder();
        var built = qb
            .UseSelector().Select<Record>("*").Then()
            .UseTableBoundFilter<Record>().WhereEqualTo(r => r.Deleted, 0)
            .Then()
            .BuildWithParameters();
        return (await _db.GetManyAsync<Record>(built.ParameterizedSql, built.Parameters)).Select(Deserialize).ToList();
    }

    public async Task<List<ScriptResource>> GetGlobalAsync()
    {
        using var qb = AppQBuilderExtensions.NewQBuilder();
        var built = qb
            .UseSelector().Select<Record>("*").Then()
            .UseTableBoundFilter<Record>().WhereEqualTo(r => r.Deleted, 0)
            .WhereEqualTo(r => r.Scope, "Global")
            .Then()
            .BuildWithParameters();
        return (await _db.GetManyAsync<Record>(built.ParameterizedSql, built.Parameters)).Select(Deserialize).ToList();
    }

    public async Task<List<ScriptResource>> GetByProjectAsync(string projectId)
    {
        using var qb = AppQBuilderExtensions.NewQBuilder();
        var built = qb
            .UseSelector().Select<Record>("*").Then()
            .UseTableBoundFilter<Record>().WhereEqualTo(r => r.Deleted, 0)
            .WhereEqualTo(r => r.ProjectId, Guid.Parse(projectId))
            .Then()
            .BuildWithParameters();
        return (await _db.GetManyAsync<Record>(built.ParameterizedSql, built.Parameters)).Select(Deserialize).ToList();
    }

    public async Task<ScriptResource?> GetByIdAsync(Guid id)
    {
        using var qb = AppQBuilderExtensions.NewQBuilder();
        var built = qb
            .UseSelector().Select<Record>("*").Then()
            .UseTableBoundFilter<Record>().WhereEqualTo(r => r.Id, id)
            .Then()
            .BuildWithParameters();
        var records = await _db.GetManyAsync<Record>(built.ParameterizedSql, built.Parameters);
        return records.Select(Deserialize).FirstOrDefault();
    }

    public async Task<ScriptResource?> GetByNameAsync(string name)
    {
        using var qb = AppQBuilderExtensions.NewQBuilder();
        var built = qb
            .UseSelector().Select<Record>("*").Then()
            .UseTableBoundFilter<Record>().WhereEqualTo(r => r.Name, name)
            .Then()
            .BuildWithParameters();
        var records = await _db.GetManyAsync<Record>(built.ParameterizedSql, built.Parameters);
        return records.Select(Deserialize).FirstOrDefault();
    }

    public async Task SaveAsync(ScriptResource resource)
    {
        using var qb = AppQBuilderExtensions.NewQBuilder();
        var checkBuilt = qb
            .UseSelector().Select<Record>("Id").Then()
            .UseTableBoundFilter<Record>().WhereEqualTo(r => r.Id, resource.Id)
            .Then()
            .BuildWithParameters();
        var existing = await _db.GetManyAsync<Record>(checkBuilt.ParameterizedSql, checkBuilt.Parameters);

        if (existing.Any())
        {
            var built = qb
                .UseTableBoundUpdate<Record>()
                .Set(r => r.Data, JsonSerializer.Serialize(resource, JsonOpts))
                .Set(r => r.Name, resource.Name)
                .Set(r => r.Scope, resource.Scope.ToString())
                .Set(r => r.ProjectId, resource.ProjectId != null ? Guid.Parse(resource.ProjectId) : null)
                .Set(r => r.Modified, DateTime.UtcNow)
                .WhereEqualTo(r => r.Id, resource.Id)
                .BuildWithParameters();
            await _db.ExecuteAsync(built.ParameterizedSql, built.Parameters);
        }
        else
        {
            var built = qb
                .UseTableBoundInsert<Record>()
                .FromObject(new Record
                {
                    Id = resource.Id,
                    CompanyId = Guid.Empty,
                    Name = resource.Name,
                    Scope = resource.Scope.ToString(),
                    ProjectId = resource.ProjectId != null ? Guid.Parse(resource.ProjectId) : null,
                    Data = JsonSerializer.Serialize(resource, JsonOpts),
                    Created = DateTime.UtcNow,
                    Modified = DateTime.UtcNow,
                })
                .BuildWithParameters();
            await _db.ExecuteAsync(built.ParameterizedSql, built.Parameters);
        }
    }

    public async Task DeleteAsync(Guid id)
    {
        using var qb = AppQBuilderExtensions.NewQBuilder();
        var built = qb
            .UseTableBoundUpdate<Record>().Set(r => r.Deleted, true)
            .WhereEqualTo(r => r.Id, id)
            .BuildWithParameters();
        await _db.ExecuteAsync(built.ParameterizedSql, built.Parameters);
    }

    private static ScriptResource Deserialize(Record record)
    {
        return JsonSerializer.Deserialize<ScriptResource>(record.Data, JsonOpts)
            ?? throw new InvalidOperationException($"Failed to deserialize script resource {record.Id}");
    }

    [Table("ScriptResource")]
    private class Record
    {
        [ExplicitKey]
        public Guid Id { get; set; }
        public Guid CompanyId { get; set; }
        public string Name { get; set; } = "";
        public string Scope { get; set; } = "Global";
        public Guid? ProjectId { get; set; }
        public string Data { get; set; } = "";
        public DateTime Created { get; set; }
        public DateTime Modified { get; set; }
        public bool Deleted { get; set; }
    }

    private class CountResult
    {
        public int Count { get; set; }
    }
}
