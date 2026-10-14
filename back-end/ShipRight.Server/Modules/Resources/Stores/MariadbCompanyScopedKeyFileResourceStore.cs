using System.Text.Json;
using Dapper.Contrib.Extensions;
using Rocket.Libraries.DatabaseIntegrator;
using ShipRight.Modules.Resources.Models;
using ShipRight.Shared.Tenancy;

namespace ShipRight.Modules.Resources.Stores;

// Cloud store limited to the signed-in person's company: the company filter is part of every query
// (R8), the list is one ordered page plus one count in the same SQL (R9, PERF-001), and the .pem
// content only lives in the row's Data column and is never selected into a metadata read.
public class CompanyScopedKeyFileResourceStore(IDatabaseHelper<Guid> db, CompanyScope scope) : IKeyFileResourceStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private const string SelectPageScoped =
        "SELECT Id, CompanyId, Name, Data, Created, Modified FROM KeyFileResource " +
        "WHERE CompanyId = @CompanyId AND Deleted = 0 ORDER BY Created, Id LIMIT @Take OFFSET @Skip";
    private const string SelectPageSystem =
        "SELECT Id, CompanyId, Name, Data, Created, Modified FROM KeyFileResource " +
        "WHERE Deleted = 0 ORDER BY Created, Id LIMIT @Take OFFSET @Skip";
    private const string CountScoped =
        "SELECT COUNT(*) FROM KeyFileResource WHERE CompanyId = @CompanyId AND Deleted = 0";
    private const string CountSystem =
        "SELECT COUNT(*) FROM KeyFileResource WHERE Deleted = 0";
    private const string SelectOneScoped =
        "SELECT Id, CompanyId, Name, Data, Created, Modified FROM KeyFileResource " +
        "WHERE Id = @Id AND CompanyId = @CompanyId AND Deleted = 0";
    private const string SelectOneSystem =
        "SELECT Id, CompanyId, Name, Data, Created, Modified FROM KeyFileResource " +
        "WHERE Id = @Id AND Deleted = 0";
    private const string UpdateScoped =
        "UPDATE KeyFileResource SET Data = @Data, Name = @Name, Modified = @Modified " +
        "WHERE Id = @Id AND CompanyId = @CompanyId";
    private const string UpdateSystem =
        "UPDATE KeyFileResource SET Data = @Data, Name = @Name, Modified = @Modified WHERE Id = @Id";
    private const string Insert =
        "INSERT INTO KeyFileResource (Id, CompanyId, Name, Data, Created, Modified, Deleted) " +
        "VALUES (@Id, @CompanyId, @Name, @Data, @Created, @Modified, 0)";
    private const string DeleteScoped =
        "UPDATE KeyFileResource SET Deleted = 1 WHERE Id = @Id AND CompanyId = @CompanyId";
    private const string DeleteSystem =
        "UPDATE KeyFileResource SET Deleted = 1 WHERE Id = @Id";

    public async Task<(List<KeyFileResource> Items, int Total)> GetPageAsync(int page, int pageSize)
    {
        var companyId = scope.Filter();
        var parameters = new Dictionary<string, object>
        {
            ["Take"] = pageSize,
            ["Skip"] = (page - 1) * pageSize,
        };
        if (companyId is { } cid) parameters["CompanyId"] = cid;

        var rows = await db.GetManyAsync<Record>(companyId is null ? SelectPageSystem : SelectPageScoped, parameters);
        var total = await CountAsync();
        return (rows.Select(Deserialize).ToList(), total);
    }

    public async Task<KeyFileResource?> GetByIdAsync(Guid id)
    {
        var companyId = scope.Filter();
        var parameters = new Dictionary<string, object> { ["Id"] = id };
        if (companyId is { } cid) parameters["CompanyId"] = cid;
        var rows = await db.GetManyAsync<Record>(companyId is null ? SelectOneSystem : SelectOneScoped, parameters);
        var row = rows.FirstOrDefault();
        return row is null ? null : Deserialize(row);
    }

    public async Task SaveAsync(KeyFileResource resource)
    {
        if (await GetByIdAsync(resource.Id) is not null)
        {
            var companyId = scope.Filter();
            var update = new Dictionary<string, object>
            {
                ["Id"] = resource.Id,
                ["Name"] = resource.Name,
                ["Data"] = JsonSerializer.Serialize(resource, JsonOpts),
                ["Modified"] = DateTime.UtcNow,
            };
            if (companyId is { } cid) update["CompanyId"] = cid;
            await db.ExecuteAsync(companyId is null ? UpdateSystem : UpdateScoped, update);
            return;
        }

        var insert = new Dictionary<string, object>
        {
            ["Id"] = resource.Id,
            ["CompanyId"] = await scope.InsertCompanyAsync(),
            ["Name"] = resource.Name,
            ["Data"] = JsonSerializer.Serialize(resource, JsonOpts),
            ["Created"] = DateTime.UtcNow,
            ["Modified"] = DateTime.UtcNow,
        };
        await db.ExecuteAsync(Insert, insert);
    }

    public async Task DeleteAsync(Guid id)
    {
        var companyId = scope.Filter();
        var parameters = new Dictionary<string, object> { ["Id"] = id };
        if (companyId is { } cid) parameters["CompanyId"] = cid;
        await db.ExecuteAsync(companyId is null ? DeleteSystem : DeleteScoped, parameters);
    }

    private async Task<int> CountAsync()
    {
        var companyId = scope.Filter();
        return companyId is { } cid
            ? await db.ExecuteScalarAsync<int>(CountScoped, new { CompanyId = cid })
            : await db.ExecuteScalarAsync<int>(CountSystem, new { });
    }

    private static KeyFileResource Deserialize(Record record) =>
        JsonSerializer.Deserialize<KeyFileResource>(record.Data, JsonOpts)
            ?? throw new InvalidOperationException($"Failed to deserialize key file resource {record.Id}");

    [Table("KeyFileResource")]
    private class Record
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
