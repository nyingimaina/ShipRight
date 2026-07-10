using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Serilog;
using ShipRight.Modules.Resources.Models;
using ShipRight.Shared.Store;

namespace ShipRight.Modules.Resources.Stores;

public class SqlitePipelineResourceStore : IPipelineResourceStore
{
    private readonly string _dbPath;
    private readonly string _dataDir;
    private List<PipelineResource> _cache = [];
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public SqlitePipelineResourceStore() : this(DataDirectory.Resolve()) { }

    internal SqlitePipelineResourceStore(string dataDir)
    {
        _dataDir = dataDir;
        _dbPath = Path.Combine(dataDir, "pipeline_resources.db");
        EnsureSchema();
        LoadCache();
    }

    public int Count => _cache.Count;

    public Task<List<PipelineResource>> GetAllAsync() => Task.FromResult(_cache.ToList());

    public Task<List<PipelineResource>> GetGlobalAsync() =>
        Task.FromResult(_cache.Where(p => p.Scope == PipelineScope.Global).ToList());

    public Task<List<PipelineResource>> GetByProjectAsync(Guid projectId) =>
        Task.FromResult(_cache.Where(p => p.Scope == PipelineScope.Project && p.ProjectId == projectId).ToList());

    public Task<PipelineResource?> GetByIdAsync(Guid id) =>
        Task.FromResult(_cache.FirstOrDefault(p => p.Id == id));

    public async Task SaveAsync(PipelineResource resource)
    {
        await _writeLock.WaitAsync();
        try
        {
            var idx = _cache.FindIndex(p => p.Id == resource.Id);
            if (idx >= 0) _cache[idx] = resource;
            else _cache.Add(resource);

            var data = JsonConvert.SerializeObject(resource);
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO pipeline_resources (id, name, data)
                VALUES ($id, $name, $data)
                ON CONFLICT(id) DO UPDATE SET name = $name, data = $data
                """;
            cmd.Parameters.AddWithValue("$id", resource.Id.ToString());
            cmd.Parameters.AddWithValue("$name", resource.Name);
            cmd.Parameters.AddWithValue("$data", data);
            await cmd.ExecuteNonQueryAsync();
        }
        finally { _writeLock.Release(); }
    }

    public async Task DeleteAsync(Guid id)
    {
        await _writeLock.WaitAsync();
        try
        {
            _cache.RemoveAll(p => p.Id == id);
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM pipeline_resources WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id.ToString());
            await cmd.ExecuteNonQueryAsync();
        }
        finally { _writeLock.Release(); }
    }

    private void EnsureSchema()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS pipeline_resources (
                id   TEXT NOT NULL PRIMARY KEY,
                name TEXT NOT NULL,
                data TEXT NOT NULL
            )
            """;
        cmd.ExecuteNonQuery();
    }

    private void LoadCache()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT data FROM pipeline_resources ORDER BY rowid";
        using var reader = cmd.ExecuteReader();
        var list = new List<PipelineResource>();
        while (reader.Read())
        {
            var resource = JsonConvert.DeserializeObject<PipelineResource>(reader.GetString(0));
            if (resource is not null)
                list.Add(resource);
        }
        _cache = list;
        Log.Information("Loaded {Count} pipeline resources from {Path}", _cache.Count, _dbPath);
    }

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        return conn;
    }
}
