using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Serilog;
using ShipRight.Modules.Resources.Models;
using ShipRight.Shared.Store;

namespace ShipRight.Modules.Resources.Stores;

public class SqliteScriptResourceStore : IScriptResourceStore
{
    private readonly string _dbPath;
    private readonly string _dataDir;
    private List<ScriptResource> _cache = [];
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public SqliteScriptResourceStore() : this(DataDirectory.Resolve()) { }

    internal SqliteScriptResourceStore(string dataDir)
    {
        _dataDir = dataDir;
        _dbPath = Path.Combine(dataDir, "script_resources.db");
        EnsureSchema();
        LoadCache();
    }

    public int Count => _cache.Count;

    public Task<List<ScriptResource>> GetAllAsync() => Task.FromResult(_cache.ToList());

    public Task<List<ScriptResource>> GetGlobalAsync() =>
        Task.FromResult(_cache.Where(s => s.Scope == PipelineScope.Global).ToList());

    public Task<List<ScriptResource>> GetByProjectAsync(Guid projectId) =>
        Task.FromResult(_cache.Where(s => s.Scope == PipelineScope.Project && s.ProjectId == projectId).ToList());

    public Task<ScriptResource?> GetByIdAsync(Guid id) =>
        Task.FromResult(_cache.FirstOrDefault(r => r.Id == id));

    public Task<ScriptResource?> GetByNameAsync(string name) =>
        Task.FromResult(_cache.FirstOrDefault(r =>
            r.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));

    public async Task SaveAsync(ScriptResource resource)
    {
        await _writeLock.WaitAsync();
        try
        {
            var idx = _cache.FindIndex(r => r.Id == resource.Id);
            if (idx >= 0) _cache[idx] = resource;
            else _cache.Add(resource);

            var data = JsonConvert.SerializeObject(resource);
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO script_resources (id, name, data)
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
            _cache.RemoveAll(r => r.Id == id);
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM script_resources WHERE id = $id";
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
            CREATE TABLE IF NOT EXISTS script_resources (
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
        cmd.CommandText = "SELECT data FROM script_resources ORDER BY rowid";
        using var reader = cmd.ExecuteReader();
        var list = new List<ScriptResource>();
        while (reader.Read())
        {
            var resource = JsonConvert.DeserializeObject<ScriptResource>(reader.GetString(0));
            if (resource is not null)
                list.Add(resource);
        }
        _cache = list;
        Log.Information("Loaded {Count} script resources from {Path}", _cache.Count, _dbPath);
    }

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        return conn;
    }
}
