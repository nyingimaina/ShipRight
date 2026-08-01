using System.Text.Json;
using Dapper.Contrib.Extensions;
using Jattac.Libraries.QBuilder;
using Rocket.Libraries.DatabaseIntegrator;
using ShipRight.Modules.Projects;

namespace ShipRight.Modules.Servers;

public class MariaDbServerStore : IServerStore
{
    private readonly IDatabaseHelper<Guid> _db;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public MariaDbServerStore(IDatabaseHelper<Guid> db)
    {
        _db = db;
    }

    public async Task<List<ServerConfig>> GetAllAsync()
    {
        using var qb = new QBuilder(parameterize: true);
        var built = qb
            .UseSelector()
            .Select<ServerRecord>("*")
            .Then()
            .UseTableBoundFilter<ServerRecord>()
            .WhereEqualTo(r => r.Deleted, 0)
            .Then()
            .BuildWithParameters();

        var records = await _db.GetManyAsync<ServerRecord>(built.ParameterizedSql, built.Parameters);
        return records.Select(Deserialize).ToList();
    }

    public async Task<ServerConfig?> GetByIdAsync(string id)
    {
        using var qb = new QBuilder(parameterize: true);
        var built = qb
            .UseSelector()
            .Select<ServerRecord>("*")
            .Then()
            .UseTableBoundFilter<ServerRecord>()
            .WhereEqualTo(r => r.Id, Guid.Parse(id))
            .Then()
            .BuildWithParameters();

        var records = await _db.GetManyAsync<ServerRecord>(built.ParameterizedSql, built.Parameters);
        var record = records.FirstOrDefault();
        return record != null ? Deserialize(record) : null;
    }

    public async Task SaveAsync(ServerConfig server)
    {
        using var qb = new QBuilder(parameterize: true);
        var existing = await GetByIdAsync(server.Id);
        if (existing != null)
        {
            var built = qb
                .UseTableBoundUpdate<ServerRecord>()
                .Set(r => r.Data, JsonSerializer.Serialize(server, JsonOpts))
                .Set(r => r.Name, server.Name)
                .Set(r => r.Modified, DateTime.UtcNow)
                .WhereEqualTo(r => r.Id, Guid.Parse(server.Id))
                .BuildWithParameters();
            await _db.ExecuteAsync(built.ParameterizedSql, built.Parameters);
        }
        else
        {
            var record = new ServerRecord
            {
                Id = Guid.Parse(server.Id),
                CompanyId = Guid.Empty,
                Name = server.Name,
                Data = JsonSerializer.Serialize(server, JsonOpts),
                Created = DateTime.UtcNow,
                Modified = DateTime.UtcNow,
            };
            var built = qb
                .UseTableBoundInsert<ServerRecord>()
                .FromObject(record)
                .BuildWithParameters();
            await _db.ExecuteAsync(built.ParameterizedSql, built.Parameters);
        }
    }

    public async Task DeleteAsync(string id)
    {
        using var qb = new QBuilder(parameterize: true);
        var built = qb
            .UseTableBoundUpdate<ServerRecord>()
            .Set(r => r.Deleted, true)
            .WhereEqualTo(r => r.Id, Guid.Parse(id))
            .BuildWithParameters();
        await _db.ExecuteAsync(built.ParameterizedSql, built.Parameters);
    }

    private static ServerConfig Deserialize(ServerRecord record)
    {
        var config = JsonSerializer.Deserialize<ServerConfig>(record.Data, JsonOpts);
        if (config == null)
            throw new InvalidOperationException($"Failed to deserialize server {record.Id}");
        return config with { Id = record.Id.ToString(), Name = record.Name };
    }

    [Table("Server")]
    private class ServerRecord
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
