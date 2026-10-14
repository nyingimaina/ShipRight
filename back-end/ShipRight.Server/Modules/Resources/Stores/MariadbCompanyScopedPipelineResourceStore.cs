using System.Text.Json;
using Dapper.Contrib.Extensions;
using Jattac.Libraries.QBuilder;
using Rocket.Libraries.DatabaseIntegrator;
using ShipRight.Database;
using ShipRight.Modules.Resources.Models;
using ShipRight.Shared.Tenancy;

namespace ShipRight.Modules.Resources.Stores;

// Cloud store limited to the signed-in person's company: the company filter is part of every query (R4).
public class CompanyScopedPipelineResourceStore(IDatabaseHelper<Guid> db, CompanyScope scope) : IPipelineResourceStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public int Count => scope.IsScoped ? GetAllAsync().GetAwaiter().GetResult().Count : 0;

    public Task<List<PipelineResource>> GetAllAsync() => QueryAsync(qb => qb.UseTableBoundFilter<Record>().WhereEqualTo(r => r.Deleted, 0));

    public Task<List<PipelineResource>> GetGlobalAsync() => QueryAsync(qb =>
        qb.UseTableBoundFilter<Record>().WhereEqualTo(r => r.Deleted, 0).WhereEqualTo(r => r.Scope, "Global"));

    public Task<List<PipelineResource>> GetByProjectAsync(string projectId) => QueryAsync(qb =>
        qb.UseTableBoundFilter<Record>().WhereEqualTo(r => r.Deleted, 0).WhereEqualTo(r => r.ProjectId, Guid.Parse(projectId)));

    public async Task<PipelineResource?> GetByIdAsync(Guid id) =>
        (await QueryAsync(qb => qb.UseTableBoundFilter<Record>().WhereEqualTo(r => r.Id, id))).FirstOrDefault();

    public async Task<PipelineResource?> GetByNameAsync(string name) =>
        (await QueryAsync(qb => qb.UseTableBoundFilter<Record>().WhereEqualTo(r => r.Name, name))).FirstOrDefault();

    public async Task SaveAsync(PipelineResource resource)
    {
        if (await GetByIdAsync(resource.Id) is not null)
        {
            using var qb = AppQBuilderExtensions.NewQBuilder();
            var update = qb.UseTableBoundUpdate<Record>()
                .Set(r => r.Data, JsonSerializer.Serialize(resource, JsonOpts))
                .Set(r => r.Name, resource.Name)
                .Set(r => r.Scope, resource.Scope.ToString())
                .Set(r => r.ProjectId, resource.ProjectId != null ? Guid.Parse(resource.ProjectId) : null)
                .Set(r => r.Modified, DateTime.UtcNow)
                .WhereEqualTo(r => r.Id, resource.Id);
            if (scope.Filter() is { } companyId) update = update.WhereEqualTo(r => r.CompanyId, companyId);
            var built = update.BuildWithParameters();
            await db.ExecuteAsync(built.ParameterizedSql, built.Parameters);
            return;
        }

        using var insert = AppQBuilderExtensions.NewQBuilder();
        var insertBuilt = insert
            .UseTableBoundInsert<Record>()
            .FromObject(new Record
            {
                Id = resource.Id,
                CompanyId = await scope.InsertCompanyAsync(),
                Name = resource.Name,
                Scope = resource.Scope.ToString(),
                ProjectId = resource.ProjectId != null ? Guid.Parse(resource.ProjectId) : null,
                Data = JsonSerializer.Serialize(resource, JsonOpts),
                Created = DateTime.UtcNow,
                Modified = DateTime.UtcNow,
            })
            .BuildWithParameters();
        await db.ExecuteAsync(insertBuilt.ParameterizedSql, insertBuilt.Parameters);
    }

    public async Task DeleteAsync(Guid id)
    {
        using var qb = AppQBuilderExtensions.NewQBuilder();
        var update = qb.UseTableBoundUpdate<Record>().Set(r => r.Deleted, true).WhereEqualTo(r => r.Id, id);
        if (scope.Filter() is { } companyId) update = update.WhereEqualTo(r => r.CompanyId, companyId);
        var built = update.BuildWithParameters();
        await db.ExecuteAsync(built.ParameterizedSql, built.Parameters);
    }

    private async Task<List<PipelineResource>> QueryAsync(Action<QBuilder> filter)
    {
        using var qb = AppQBuilderExtensions.NewQBuilder();
        qb.UseSelector().Select<Record>("*");
        filter(qb);
        if (scope.Filter() is { } companyId)
            qb.UseTableBoundFilter<Record>().WhereEqualTo(r => r.CompanyId, companyId);
        var built = qb.BuildWithParameters();
        return (await db.GetManyAsync<Record>(built.ParameterizedSql, built.Parameters)).Select(Deserialize).ToList();
    }

    private static PipelineResource Deserialize(Record record) =>
        JsonSerializer.Deserialize<PipelineResource>(record.Data, JsonOpts)
            ?? throw new InvalidOperationException($"Failed to deserialize pipeline resource {record.Id}");

    [Table("PipelineResource")]
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
}
