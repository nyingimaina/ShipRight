using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Serilog;
using ShipRight.Modules.Resources.Models;
using ShipRight.Shared.Store;

namespace ShipRight.Modules.Resources.Stores;

public class SqliteCredentialResourceStore : ICredentialResourceStore
{
    private readonly string _dbPath;
    private readonly string _dataDir;
    private List<CredentialResource> _cache = [];
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public SqliteCredentialResourceStore() : this(DataDirectory.Resolve()) { }

    internal SqliteCredentialResourceStore(string dataDir)
    {
        _dataDir = dataDir;
        _dbPath = Path.Combine(dataDir, "resources.db");
        EnsureSchema();
        LoadCache();
    }

    public int Count => _cache.Count;

    public Task<List<CredentialResource>> GetAllAsync() => Task.FromResult(_cache.ToList());

    public Task<List<CredentialResource>> GetByProjectAsync(string projectId) =>
        Task.FromResult(_cache.Where(c => c.ProjectId is null || c.ProjectId == projectId).ToList());

    public Task<CredentialResource?> GetByIdAsync(Guid id) =>
        Task.FromResult(_cache.FirstOrDefault(r => r.Id == id));

    public Task<CredentialResource?> GetByNameAsync(string name) =>
        Task.FromResult(_cache.FirstOrDefault(r =>
            r.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));

    public async Task SaveAsync(CredentialResource resource)
    {
        await _writeLock.WaitAsync();
        try
        {
            var idx = _cache.FindIndex(r => r.Id == resource.Id);
            if (idx >= 0) _cache[idx] = resource;
            else _cache.Add(resource);

            var data = JsonConvert.SerializeObject(WithEncryptedValue(resource));
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO credential_resources (id, name, data)
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
            cmd.CommandText = "DELETE FROM credential_resources WHERE id = $id";
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
            CREATE TABLE IF NOT EXISTS credential_resources (
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
        cmd.CommandText = "SELECT data FROM credential_resources ORDER BY rowid";
        using var reader = cmd.ExecuteReader();
        var list = new List<CredentialResource>();
        while (reader.Read())
        {
            var resource = JsonConvert.DeserializeObject<CredentialResource>(reader.GetString(0));
            if (resource is not null)
                list.Add(WithDecryptedValue(resource));
        }
        _cache = list;
        Log.Information("Loaded {Count} credential resources from {Path}", _cache.Count, _dbPath);
    }

    private CredentialResource WithEncryptedValue(CredentialResource r) => r with
    {
        Value = string.IsNullOrEmpty(r.Value)
            ? "" : SecureStore.Encrypt(r.Value, _dataDir),
    };

    private CredentialResource WithDecryptedValue(CredentialResource r) => r with
    {
        Value = string.IsNullOrEmpty(r.Value)
            ? "" : SecureStore.Decrypt(r.Value, _dataDir),
    };

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        return conn;
    }
}
